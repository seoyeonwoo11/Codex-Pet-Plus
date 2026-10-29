// Runs the actual background module against a small Chrome API adapter.
// The managed CI browser blocks unpacked extensions; no browser policy is changed.
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const TEST_ORIGIN = 'https://codex-pet.test';
const root = fileURLToPath(new URL('../../extension/', import.meta.url));

export async function harness(context) {
  const id = 'abcdefghijklmnopabcdefghijklmnop';
  const areas = { local: {}, session: {} };
  const alarms = new Map();
  const listeners = [];
  const tabsUpdated = [];
  const tabIds = new Map();
  let badge = '';
  const storage = area => ({
    async get(keys) {
      const wanted = typeof keys === 'string' ? [keys] : keys ?? Object.keys(areas[area]);
      return structuredClone(Object.fromEntries(wanted.filter(key => key in areas[area]).map(key => [key, areas[area][key]])));
    },
    async set(values) {
      const changes = {};
      for (const [key, value] of Object.entries(values)) { changes[key] = { oldValue: areas[area][key], newValue: structuredClone(value) }; areas[area][key] = structuredClone(value); }
      await Promise.all(context.pages().map(page => page.evaluate(({ changes, area }) => window.__petStorageChanged?.(changes, area), { changes, area }).catch(() => {})));
    },
  });
  const tabId = page => { if (!tabIds.has(page)) tabIds.set(page, tabIds.size + 1); return tabIds.get(page); };
  async function createTab({ url }) {
    const page = await context.newPage();
    await page.goto(url);
    const tab = { id: tabId(page), url, status: 'complete' };
    for (const listener of tabsUpdated) listener(tab.id, { status: 'complete' }, tab);
    return tab;
  }
  const api = {
    runtime: {
      id, onInstalled: { addListener() {} }, onStartup: { addListener() {} },
      onMessage: { addListener(listener) { listeners.push(listener); } },
    },
    storage: { local: storage('local'), session: storage('session') },
    tabs: {
      onUpdated: { addListener(listener) { tabsUpdated.push(listener); } }, create: createTab,
      async query() { return context.pages().filter(page => page.url().startsWith('https://chatgpt.com/')).map(page => ({ id: tabId(page), url: page.url(), status: 'complete', active: true, lastAccessed: tabId(page) })); },
    },
    scripting: {
      async executeScript({ target, func }) {
        const page = [...tabIds].find(([, id]) => id === target.tabId)?.[0];
        if (!page) throw new Error('No tab');
        return [{ result: await page.evaluate(func) }];
      },
    },
    action: { async setBadgeText({ text }) { badge = text; }, async setBadgeBackgroundColor() {}, async setTitle() {} },
    alarms: { async create(name, options) { alarms.set(name, options); }, async clear(name) { return alarms.delete(name); }, onAlarm: { addListener() {} } },
  };
  globalThis.chrome = api;
  await import('../../extension/background.js?test=' + Date.now() + Math.random());
  await context.exposeBinding('__petCall', async (_source, message) => {
    if (message.type === '__options') return createTab({ url: TEST_ORIGIN + '/options.html' });
    if (message.type === '__tab') return createTab({ url: message.url });
    return new Promise(resolve => listeners[0](message, { id }, resolve));
  });
  await context.addInitScript(id => {
    const changed = [];
    window.__petStorageChanged = (changes, area) => { for (const listener of changed) listener(changes, area); };
    window.chrome = {
      runtime: { id, sendMessage: message => window.__petCall(message), openOptionsPage: () => window.__petCall({ type: '__options' }) },
      storage: { onChanged: { addListener: listener => changed.push(listener) } },
      tabs: { create: ({ url }) => window.__petCall({ type: '__tab', url }) },
      windows: { getCurrent: async () => ({ id: 1 }) },
      sidePanel: { open: async () => {} },
    };
  }, id);
  await context.route(TEST_ORIGIN + '/**', async route => {
    const relative = new URL(route.request().url()).pathname.slice(1);
    const file = path.resolve(root, relative);
    if (!file.startsWith(root)) return route.abort();
    try {
      const ext = path.extname(file);
      await route.fulfill({ body: await readFile(file), contentType: ({ '.js': 'text/javascript', '.html': 'text/html', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' })[ext] ?? 'application/octet-stream', headers: { 'Content-Security-Policy': "script-src 'self'; object-src 'none'; base-uri 'none'" } });
    } catch { await route.fulfill({ status: 404, body: 'Not found' }); }
  });
  return {
    id,
    inspect() { return structuredClone({ local: areas.local, session: areas.session, badge }); },
    disconnected() { return structuredClone({ state: areas.session.state, alarm: alarms.get('refresh') }); },
  };
}
