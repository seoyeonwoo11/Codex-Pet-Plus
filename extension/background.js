import { readChatGPT } from './lib/browser-reader.js';
import { emptyState, normalizeWebTasks, normalizeWebUsage, visibleTasks } from './lib/model.js';

const CODEX_URL = 'https://chatgpt.com/codex';
let inFlight;
let connectionRevision = 0;

async function settings() {
  return { browserEnabled: false, ...await chrome.storage.local.get(['browserEnabled']) };
}

async function state() {
  return (await chrome.storage.session.get('state')).state ?? emptyState();
}

async function browserSnapshot() {
  try {
    const tabs = await chrome.tabs.query({ url: 'https://chatgpt.com/*', discarded: false });
    const tab = tabs.filter(tab => tab.status === 'complete').sort((a, b) => Number(b.active) - Number(a.active) || (b.lastAccessed ?? 0) - (a.lastAccessed ?? 0))[0];
    if (!tab) return { status: 'no_tab', data: null };
    const responses = await chrome.scripting.executeScript({ target: { tabId: tab.id }, world: 'MAIN', func: readChatGPT });
    const result = responses[0]?.result;
    if (!result?.ok) return { status: result?.code ?? 'service_unavailable', data: null };
    if (result.usageError === 'login_required' || result.tasksError === 'login_required') return { status: 'login_required', data: null };
    const fetchedAt = new Date().toISOString();
    let usage = null;
    let tasks = { tasks: [], hasMore: false };
    let usageError = result.usageError;
    let tasksError = result.tasksError;
    try { if (result.usage) usage = normalizeWebUsage(result.usage, fetchedAt); } catch { usageError = 'unexpected_response'; }
    try { if (result.tasks) tasks = normalizeWebTasks(result.tasks); } catch { tasksError = 'unexpected_response'; }
    return { status: 'connected', usageError, tasksError, data: { usage, ...tasks, fetchedAt } };
  } catch { return { status: 'service_unavailable', data: null }; }
}

async function refreshNow() {
  const revision = connectionRevision;
  const prefs = await settings();
  let browser = prefs.browserEnabled ? await browserSnapshot() : { status: 'disconnected', data: null };
  // Disconnect may be clicked while a request is in flight. Do not restore its data.
  if (revision !== connectionRevision) return state();
  const latest = await settings();
  if (!latest.browserEnabled) browser = { status: 'disconnected', data: null };
  const next = { browser, refreshedAt: new Date().toISOString() };
  await chrome.storage.session.set({ state: next });
  if (revision !== connectionRevision) return state();
  const count = visibleTasks(next, true).length;
  await Promise.all([
    chrome.action.setBadgeText({ text: count ? String(count) : '' }),
    chrome.action.setBadgeBackgroundColor({ color: '#DD754E' }),
    chrome.action.setTitle({ title: count ? `Codex Pet Plus · 진행 중 ${count}개` : 'Codex Pet Plus' }),
  ]);
  return next;
}

function refresh() {
  if (!inFlight) inFlight = refreshNow().finally(() => { inFlight = null; });
  return inFlight;
}

async function ensureAlarm() {
  const prefs = await settings();
  if (prefs.browserEnabled) await chrome.alarms.create('refresh', { periodInMinutes: 1 });
  else await chrome.alarms.clear('refresh');
}

chrome.runtime.onInstalled.addListener(() => { void ensureAlarm(); });
chrome.runtime.onStartup.addListener(() => { void ensureAlarm(); void refresh(); });
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'refresh') void refresh(); });
chrome.tabs.onUpdated.addListener((_id, change, tab) => {
  if (change.status === 'complete' && tab.url?.startsWith('https://chatgpt.com/')) void settings().then(prefs => { if (prefs.browserEnabled) return refresh(); });
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return false;
  const handle = async () => {
    switch (message?.type) {
      case 'state': return { state: await state(), settings: await settings() };
      case 'refresh': return { state: await refresh(), settings: await settings() };
      case 'connect':
        connectionRevision++;
        await chrome.storage.local.set({ browserEnabled: true });
        await ensureAlarm();
        await chrome.tabs.create({ url: CODEX_URL });
        return { ok: true };
      case 'disconnect':
        connectionRevision++;
        await chrome.storage.local.set({ browserEnabled: false });
        await chrome.storage.session.set({ state: emptyState() });
        await chrome.action.setBadgeText({ text: '' });
        await chrome.action.setTitle({ title: 'Codex Pet Plus' });
        await ensureAlarm();
        return { state: await state(), settings: await settings() };
      case 'open-task':
        if (typeof message.id !== 'string' || !/^[a-zA-Z0-9_-]{1,200}$/.test(message.id)) throw new Error('invalid_task');
        await chrome.tabs.create({ url: `${CODEX_URL}/tasks/${encodeURIComponent(message.id)}` });
        return { ok: true };
      default: throw new Error('unknown_message');
    }
  };
  handle().then(sendResponse, () => sendResponse({ error: 'operation_failed' }));
  return true;
});
