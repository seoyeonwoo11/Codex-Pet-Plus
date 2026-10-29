'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { spawn } = require('node:child_process');
const path = require('node:path');
const mod = require('../mod/runtime.cjs');

test('pet routes identify the app page independently of the custom skin', () => {
  for (const url of ['file:///app/index.html#/avatar-overlay', 'app://./index.html?route=avatar-overlay', 'codex://app/pet-overlay', 'chatgpt://app/index.html?windowType=avatarOverlay']) assert.equal(mod.isPetPage(url), true, url);
  for (const url of ['file:///app/index.html#/settings/pets', 'https://example.com/avatar-overlay', 'https://chatgpt.com/', 'file:///app/index.html?route=not-avatar-overlay', 'broken']) assert.equal(mod.isPetPage(url), false, url);
});
test('quota values use the Codex bucket, preserve unknowns, and expire at reset', () => {
  const result = mod.readQuota({ rateLimits: { limitId: 'other', primary: { usedPercent: 99 } }, rateLimitsByLimitId: { codex: { primary: { usedPercent: 28, windowDurationMins: 300 }, secondary: { usedPercent: 16, windowDurationMins: 10080 } } } });
  assert.equal(result.primary.remaining, 72); assert.equal(result.secondary.remaining, 84); assert.equal(result.secondary.label, '주간');
  for (const value of [null, {}, { rateLimits: { primary: { usedPercent: '0' } } }, { rateLimits: { primary: { usedPercent: -1 } } }]) assert.equal(mod.readQuota(value), null);
  assert.equal(mod.readQuota({ rateLimits: { primary: { usedPercent: 0, resetsAt: 1 } } }, 2000).primary.remaining, null);
});
test('mod panel follows below the pet across negative monitors and screen edges', () => {
  assert.deepEqual(mod.placement({ x: -1900, y: 100, width: 120, height: 120 }, { x: -1920, y: 0, width: 1920, height: 1040 }), { x: -1920, y: 225, width: 180, height: 62 });
  assert.equal(mod.placement({ x: 100, y: 950, width: 120, height: 80 }, { x: 0, y: 0, width: 1920, height: 1040 }).y, 883);
});
const fixture = mode => (_file, args, options) => spawn(process.execPath, [path.join(__dirname, 'fixtures/mod-rpc.cjs'), mode, ...args], options);
test('real mod stdio transport initializes and rejects incoming task requests', async () => {
  const client = new mod.QuotaClient(fixture('normal'), 2000);
  try { await client.connect('fixture'); assert.equal((await client.read()).primary.remaining, 72); }
  finally { client.stop(); }
});
test('mod transport discards raw server error and auth text', async () => {
  const client = new mod.QuotaClient(fixture('error'), 2000);
  try { await client.connect('fixture'); await assert.rejects(client.read(), error => !error.message.includes('PRIVATE_FIXTURE_TOKEN')); }
  finally { client.stop(); }
});
test('mod transport times out and stops its own child', async () => {
  const client = new mod.QuotaClient(fixture('hang'), 100);
  try { await assert.rejects(client.connect('fixture'), /초과/); }
  finally { const child = client.child; client.stop(); assert.equal(child.killed, true); }
});

function desktop() {
  const app = new EventEmitter(); app.whenReady = () => Promise.resolve();
  class Window extends EventEmitter {
    static windows = [];
    constructor(options = {}) {
      super(); this.options = options; this.visible = false; this.destroyed = false; this.bounds = options.bounds ?? { x: 100, y: 100, width: 160, height: 160 };
      this.webContents = new EventEmitter(); this.webContents.url = options.url ?? '';
      this.webContents.getURL = () => this.webContents.url; this.webContents.isLoading = () => false;
      this.webContents.setWindowOpenHandler = handler => { this.openHandler = handler; };
      this.webContents.executeJavaScript = async value => { this.script = value; };
      Window.windows.push(this); app.emit('browser-window-created', null, this);
    }
    static getAllWindows() { return Window.windows.filter(window => !window.destroyed); }
    isDestroyed() { return this.destroyed; } isVisible() { return this.visible; } isMinimized() { return false; }
    getBounds() { return this.bounds; } setBounds(bounds) { this.bounds = bounds; }
    setIgnoreMouseEvents(value) { this.clickThrough = value; }
    showInactive() { this.visible = true; } hide() { this.visible = false; }
    destroy() { this.destroyed = true; this.emit('closed'); }
    async loadURL(url) { this.webContents.url = url; this.webContents.emit('did-finish-load'); }
  }
  const client = { child: {}, read: async () => mod.readQuota({ rateLimits: { primary: { usedPercent: 25 } } }), stop() { this.stopped = true; } };
  return { app, BrowserWindow: Window, screen: { getDisplayMatching: () => ({ workArea: { x: 0, y: 0, width: 1920, height: 1040 } }) }, client };
}
const tick = () => new Promise(resolve => setImmediate(resolve));
test('app-internal mod attaches automatically, follows, hides, and reconnects with custom pets', async () => {
  const host = desktop();
  const main = new host.BrowserWindow({ url: 'file:///app/index.html#/main' });
  const pet = new host.BrowserWindow({ url: 'file:///app/index.html#/avatar-overlay' }); pet.visible = true;
  const running = mod.start(host, '/unused', host.client);
  try {
    await tick(); await running.refresh();
    let panels = host.BrowserWindow.windows.filter(window => window.options.parent);
    assert.equal(panels.length, 1); const panel = panels[0];
    assert.equal(panel.options.parent, pet); assert.equal(panel.clickThrough, true); assert.equal(panel.options.webPreferences.sandbox, true);
    assert.equal(panel.openHandler().action, 'deny'); assert.equal(panel.isVisible(), true); assert.equal(panel.bounds.y, 265);
    assert.match(panel.script, /"remaining":75/);
    pet.bounds.y = 300; pet.emit('move'); assert.equal(panel.bounds.y, 465);
    pet.visible = false; pet.emit('hide'); assert.equal(panel.isVisible(), false);
    pet.visible = true; pet.emit('show'); assert.equal(panel.isVisible(), true);
    // A custom skin and name do not change the app's pet route.
    const custom = new host.BrowserWindow({ url: 'file:///app/index.html#/avatar-overlay', title: '모찌 고양이' }); custom.visible = true;
    custom.emit('show'); pet.destroy(); await tick();
    panels = host.BrowserWindow.windows.filter(window => window.options.parent && !window.destroyed);
    assert.equal(panels.length, 1); assert.equal(panels[0].options.parent, custom);
    assert.equal(main.isDestroyed(), false);
  } finally { running.stop(); }
  assert.equal(host.client.stopped, true); assert.equal(main.isDestroyed(), false);
});
test('navigating away from the pet page hides the addon without modifying app windows', async () => {
  const host = desktop(); const pet = new host.BrowserWindow({ url: 'file:///app/index.html#/avatar-overlay' }); pet.visible = true;
  const running = mod.start(host, '/unused', host.client);
  try {
    await tick(); const panel = host.BrowserWindow.windows.find(window => window.options.parent);
    pet.webContents.url = 'file:///app/index.html#/settings'; pet.webContents.emit('did-navigate');
    assert.equal(panel.isVisible(), false); assert.equal(pet.isDestroyed(), false);
  } finally { running.stop(); }
});
