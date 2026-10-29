/* Codex Pet Plus: loaded by a user's copied desktop app. No credential files are read. */
'use strict';
const path = require('node:path');
const fs = require('node:fs');
const readline = require('node:readline');
const { spawn } = require('node:child_process');

function isPetPage(value) {
  try {
    const url = new URL(value);
    if (!['file:', 'app:', 'codex:', 'chatgpt:', 'appfile:'].includes(url.protocol)) return false;
    return /(?:^|[\/?#=&])(?:avatar[-_]?overlay|pet[-_]?overlay|mascot[-_]?overlay)(?:$|[\/?#=&-])/i.test(decodeURIComponent(url.pathname + url.search + url.hash));
  } catch { return false; }
}

function readQuota(result, now = Date.now()) {
  const quota = result?.rateLimitsByLimitId?.codex ?? result?.rateLimits;
  if (!quota || (quota.limitId != null && quota.limitId !== 'codex')) return null;
  function window(value, fallback) {
    if (!value || typeof value.usedPercent !== 'number' || !Number.isFinite(value.usedPercent) || value.usedPercent < 0 || value.usedPercent > 100) return null;
    const reset = typeof value.resetsAt === 'number' && Number.isFinite(value.resetsAt) ? value.resetsAt * 1000 : null;
    const minutes = value.windowDurationMins;
    return { remaining: reset != null && reset <= now ? null : Math.round((100 - value.usedPercent) * 10) / 10,
      label: minutes === 10080 ? '주간' : Number.isFinite(minutes) && minutes > 0 && minutes % 60 === 0 ? minutes / 60 + 'h' : fallback,
      resetsAt: reset };
  }
  const primary = window(quota.primary, '기본'), secondary = window(quota.secondary, '추가');
  return primary || secondary ? { primary, secondary, at: now } : null;
}

function placement(pet, work, width = 180, height = 62) {
  const clamp = (v, low, high) => Math.min(Math.max(v, low), Math.max(low, high));
  const above = pet.y + pet.height + 5 + height > work.y + work.height;
  return { x: Math.round(clamp(pet.x + (pet.width - width) / 2, work.x, work.x + work.width - width)),
    y: Math.round(clamp(above ? pet.y - height - 5 : pet.y + pet.height + 5, work.y, work.y + work.height - height)), width, height };
}

function findCli(resources) {
  const candidates = [path.join(resources, 'codex.exe'), path.join(resources, 'bin', 'codex.exe')];
  let visited = 0;
  function scan(directory, depth) {
    if (depth > 5 || visited > 4000) return;
    let entries; try { entries = fs.readdirSync(directory, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      if (++visited > 4000) return;
      if (entry.isSymbolicLink()) continue;
      const file = path.join(directory, entry.name);
      if (entry.isFile() && entry.name.toLowerCase() === 'codex.exe') candidates.push(file);
      else if (entry.isDirectory()) scan(file, depth + 1);
    }
  }
  scan(resources, 0);
  return [...new Set(candidates)].filter(file => { try { return fs.statSync(file).isFile(); } catch { return false; } });
}

async function isCli(file, spawnProcess = spawn) {
  return new Promise(resolve => {
    let output = '', settled = false;
    const child = spawnProcess(file, ['--version'], { windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] });
    const finish = ok => { if (settled) return; settled = true; clearTimeout(timer); child.kill(); resolve(ok); };
    const timer = setTimeout(() => finish(false), 3000);
    child.stdout.on('data', chunk => { output += chunk.toString(); if (output.length > 16384) finish(false); });
    child.once('error', () => finish(false));
    child.once('exit', code => finish(code === 0 && output.trim().startsWith('codex-cli ')));
  });
}

class QuotaClient {
  constructor(spawnProcess = spawn, timeout = 15000) { this.spawn = spawnProcess; this.timeout = timeout; this.pending = new Map(); this.sequence = 0; this.child = null; }
  async connect(file) {
    const child = this.spawn(file, ['app-server'], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
    this.child = child;
    const disconnected = () => { if (this.child === child) this.stop(); };
    child.stdin.on('error', disconnected);
    child.once('error', disconnected); child.once('exit', disconnected);
    let buffered = '';
    this.lines = readline.createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    this.child.stdout.on('data', chunk => {
      if (this.child !== child) return;
      buffered += chunk.toString();
      const last = buffered.lastIndexOf('\n'); if (last >= 0) buffered = buffered.slice(last + 1);
      if (buffered.length > 4 * 1024 * 1024) this.stop();
    });
    this.lines.on('line', line => {
      if (this.child !== child) return;
      if (line.length > 4 * 1024 * 1024) { this.stop(); return; }
      let message; try { message = JSON.parse(line); } catch { return; }
      if (!message || typeof message !== 'object') return;
      if (message.method && message.id != null) {
        this.write({ id: message.id, error: { code: -32601, message: 'This companion only reads quotas.' } }); return;
      }
      const request = this.pending.get(message.id); if (!request) return;
      this.pending.delete(message.id); clearTimeout(request.timer);
      if (Object.hasOwn(message, 'result')) request.resolve(message.result);
      else request.reject(new Error('한도를 조회하지 못했습니다.'));
    });
    await this.request('initialize', { clientInfo: { name: 'codex_pet_plus_mod', title: 'Codex Pet Plus', version: '0.5.0' }, capabilities: { experimentalApi: false } });
    this.write({ method: 'initialized' });
  }
  write(value) { if (this.child && !this.child.killed && !this.child.stdin.destroyed) this.child.stdin.write(JSON.stringify(value) + '\n'); }
  request(method, params) {
    if (!this.child || this.child.killed) return Promise.reject(new Error('연결이 종료됐습니다.'));
    const id = ++this.sequence;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('한도 조회 시간이 초과됐습니다.')); }, this.timeout);
      this.pending.set(id, { resolve, reject, timer }); this.write({ id, method, params });
    });
  }
  async read() { return readQuota(await this.request('account/rateLimits/read', {})); }
  stop() {
    const child = this.child; this.child = null; this.lines?.close();
    for (const request of this.pending.values()) { clearTimeout(request.timer); request.reject(new Error('연결이 종료됐습니다.')); }
    this.pending.clear();
    if (child && !child.killed) { child.stdin.destroy(); child.kill(); }
  }
}

const html = '<!doctype html><html lang="ko"><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src &#39;none&#39;; style-src &#39;unsafe-inline&#39;; script-src &#39;none&#39;"><style>' +
  '*{box-sizing:border-box}body{margin:0;background:transparent;font:11px "Segoe UI","Malgun Gothic",sans-serif;color:#f0f4e9;overflow:hidden}.card{height:62px;padding:7px 10px;border:1px solid #4e5c42;border-radius:10px;background:#222720f2}.cols{display:flex;gap:12px}.quota{flex:1;min-width:0}.row{display:flex;justify-content:space-between;align-items:center}.label{font-size:10px;color:#b6c2ac}.value{font-weight:600;font-size:12px}.track{height:3px;background:#414b39;margin-top:4px;border-radius:2px;overflow:hidden}.bar{height:3px;width:0;background:#9eb486}.source{font-size:9px;color:#a3b197;margin-top:5px;white-space:nowrap}</style>' +
  '<div class="card"><div class="cols"><div class="quota"><div class="row"><span class="label" id="label0">기본</span><span class="value" id="value0">—</span></div><div class="track"><div class="bar" id="bar0"></div></div></div><div class="quota"><div class="row"><span class="label" id="label1">추가</span><span class="value" id="value1">—</span></div><div class="track"><div class="bar" id="bar1"></div></div></div></div><div class="source" id="source">한도 확인 중</div></div></html>';

function render(snapshot) {
  const now = Date.now();
  [snapshot?.primary, snapshot?.secondary].forEach((quota, index) => {
    const value = quota && (quota.resetsAt == null || quota.resetsAt > now) ? quota.remaining : null;
    document.getElementById('label' + index).textContent = quota?.label ?? (index ? '추가' : '기본');
    document.getElementById('value' + index).textContent = value == null ? '—' : value + '%';
    const bar = document.getElementById('bar' + index); bar.style.width = (value ?? 0) + '%'; bar.style.background = value != null && value < 20 ? '#e1976e' : '#9eb486';
  });
  document.getElementById('source').textContent = snapshot ? '앱 연결 · 남은 Codex 한도' : '한도 조회 필요 · 로그인 확인';
}

function start(electron, resources = process.resourcesPath, client = new QuotaClient(), stateDirectory) {
  const { app, BrowserWindow, screen } = electron;
  if (stateDirectory === undefined) stateDirectory = process.env.PET_PLUS_MOD_STATE_DIR
    || (process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'CodexPetPlus', 'mod')
      : typeof app.getPath === 'function' ? path.join(app.getPath('userData'), 'CodexPetPlus', 'mod') : null);
  const seen = new WeakSet(), panels = new Map();
  // Guard only this addon's callbacks; do not intercept the host app's errors.
  const safely = action => { try { return action(); } catch { return undefined; } };
  let snapshot = null, busy = false, stopped = false, interval, observed = 0;
  function report() {
    if (!stateDirectory) return;
    try {
      fs.mkdirSync(stateDirectory, { recursive: true });
      fs.writeFileSync(path.join(stateDirectory, 'runtime-status.json'), JSON.stringify({ running: !stopped, processId: process.pid, appVersion: app.getVersion?.() ?? '', petWindows: panels.size, observedWindows: observed, quotaAvailable: snapshot != null, updatedAt: new Date().toISOString() }));
    } catch { }
  }
  const paint = panel => {
    safely(() => { if (!panel.isDestroyed() && !panel.webContents.isLoading()) panel.webContents.executeJavaScript('(' + render.toString() + ')(' + JSON.stringify(snapshot) + ')').catch(() => {}); });
  };
  function follow(pet, panel) {
    safely(() => {
    if (pet.isDestroyed() || panel.isDestroyed()) return;
    if (!isPetPage(pet.webContents.getURL()) || !pet.isVisible() || pet.isMinimized()) { panel.hide(); return; }
    const bounds = placement(pet.getBounds(), screen.getDisplayMatching(pet.getBounds()).workArea);
    panel.setBounds(bounds, false); if (!panel.isVisible()) panel.showInactive();
    });
  }
  function attach(pet) {
    safely(() => {
    if (stopped || pet.isDestroyed() || !isPetPage(pet.webContents.getURL()) || panels.has(pet)) return;
    const panel = new BrowserWindow({ width: 180, height: 62, show: false, frame: false, transparent: true, resizable: false,
      focusable: false, skipTaskbar: true, alwaysOnTop: true, hasShadow: false, parent: pet,
      webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true, webSecurity: true } });
    panels.set(pet, panel); panel.setIgnoreMouseEvents(true); report();
    panel.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    panel.webContents.on('will-navigate', event => event.preventDefault());
    const update = () => follow(pet, panel);
    for (const event of ['move', 'resize', 'show', 'hide', 'minimize', 'restore']) pet.on(event, update);
    pet.once('closed', () => { for (const event of ['move', 'resize', 'show', 'hide', 'minimize', 'restore']) pet.removeListener(event, update); panels.delete(pet); safely(() => { if (!panel.isDestroyed()) panel.destroy(); }); report(); });
    panel.webContents.on('did-finish-load', () => { paint(panel); update(); });
    panel.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(html)).catch(() => {});
    });
  }
  function observe(_event, window) {
    safely(() => {
    if (seen.has(window)) return; seen.add(window); observed++;
    window.webContents.on('did-finish-load', () => attach(window));
    window.webContents.on('did-navigate', () => { attach(window); if (panels.has(window)) follow(window, panels.get(window)); });
    attach(window);
    });
  }
  async function refresh() {
    if (busy || stopped) return; busy = true;
    try {
      if (!client.child) {
        let file;
        for (const candidate of findCli(resources)) { if (await isCli(candidate, client.spawn)) { file = candidate; break; } }
        if (!file || stopped) throw new Error('Codex 연결 도구가 없습니다.');
        await client.connect(file);
      }
      snapshot = await client.read();
    } catch { snapshot = null; client.stop(); }
    finally { busy = false; for (const panel of panels.values()) paint(panel); report(); }
  }
  app.on('browser-window-created', observe);
  app.whenReady().then(() => {
    if (stopped) return;
    for (const window of BrowserWindow.getAllWindows()) observe(null, window);
    refresh(); interval = setInterval(() => { for (const [pet, panel] of panels) follow(pet, panel); }, 200); interval.unref?.();
  }).catch(() => {});
  const usageTimer = setInterval(refresh, 60000); usageTimer.unref?.();
  const stop = () => { if (stopped) return; stopped = true; process.removeListener('exit', stop); clearInterval(interval); clearInterval(usageTimer); app.removeListener('browser-window-created', observe); safely(() => client.stop()); for (const panel of panels.values()) safely(() => { if (!panel.isDestroyed()) panel.destroy(); }); panels.clear(); report(); };
  process.once('exit', stop);
  app.once('will-quit', stop);
  return { stop, refresh };
}

module.exports = { isPetPage, readQuota, placement, findCli, isCli, QuotaClient, html, render, start };
if (process.versions.electron && process.type === 'browser') {
  try { start(require('electron')); } catch { /* A failed optional mod must not stop the app. */ }
}
