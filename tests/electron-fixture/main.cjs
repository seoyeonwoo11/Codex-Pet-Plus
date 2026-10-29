'use strict';
const { app, BrowserWindow } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const assert = require('node:assert/strict');
app.setPath('userData', path.join(process.env.PET_MOD_SMOKE_DIR, 'user-data'));
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(read, description) {
  for (let count = 0; count < 100; count++) { const value = read(); if (value) return value; await delay(50); }
  throw new Error('Timed out: ' + description);
}
async function pet(title) {
  const window = new BrowserWindow({ width: 160, height: 160, x: 100, y: 100, frame: false, transparent: true, show: false, title,
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true } });
  await window.loadFile(path.join(__dirname, 'index.html'), { hash: '/avatar-overlay' }); window.showInactive(); return window;
}
const panelFor = window => BrowserWindow.getAllWindows().find(candidate => candidate.getParentWindow() === window && candidate.webContents.getURL().startsWith('data:'));
app.whenReady().then(async () => {
  let cat, custom;
  try {
    // The real desktop app keeps its main window alive when the pet is closed.
    const main = new BrowserWindow({ show: false, width: 800, height: 600,
      webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true } });
    cat = await pet('기본 펫');
    const panel = await until(() => panelFor(cat), 'automatic native pet attachment');
    await until(() => !panel.webContents.isLoading() && panel.isVisible(), 'panel visibility');
    assert.equal(await panel.webContents.executeJavaScript('document.getElementById("value0").textContent'), '—');
    const runtime = require('./codex-pet-plus-runtime.cjs');
    const fixture = runtime.readQuota({ rateLimits: { primary: { usedPercent: 28, windowDurationMins: 300 }, secondary: { usedPercent: 16, windowDurationMins: 10080 } } });
    await panel.webContents.executeJavaScript('(' + runtime.render.toString() + ')(' + JSON.stringify(fixture) + ')');
    assert.equal(await panel.webContents.executeJavaScript('document.getElementById("value0").textContent'), '72%');
    assert.equal(await panel.webContents.executeJavaScript('document.getElementById("value1").textContent'), '84%');
    fs.writeFileSync(path.join(process.env.PET_MOD_SMOKE_DIR, 'quota-panel.png'), (await panel.webContents.capturePage()).toPNG());
    cat.setPosition(300, 200);
    await until(() => panel.getBounds().y === cat.getBounds().y + cat.getBounds().height + 5, 'following pet movement');
    cat.hide(); await until(() => !panel.isVisible(), 'hiding with the pet');
    cat.showInactive(); await until(() => panel.isVisible(), 'reappearing with the pet');
    cat.destroy(); await until(() => panel.isDestroyed(), 'pet close cleanup');
    custom = await pet('모찌 커스텀 고양이');
    const nextPanel = await until(() => panelFor(custom), 'custom pet reconnection');
    await until(() => nextPanel.isVisible(), 'custom pet panel visibility');
    assert.equal(main.isDestroyed(), false);
    console.log('MOD_SMOKE_PASS ' + JSON.stringify({ electron: process.versions.electron, automatic: true, customPet: true, follows: true, hides: true, unknownQuota: true }));
    app.quit();
  } catch (error) { console.error(error.stack); app.exit(1); }
});
setTimeout(() => { console.error('Electron smoke test timeout'); app.exit(1); }, 20000).unref();
