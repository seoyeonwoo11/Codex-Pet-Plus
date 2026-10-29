// Chrome permission prompts and native OS banners are represented by the API
// adapter; actual page code, refresh logic, and threshold tracking run unchanged.
import { test, expect, chromium } from '@playwright/test';
import { access, mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { harness, TEST_ORIGIN } from './harness.mjs';
import { webUsage, webTasks } from '../fixtures.mjs';

let context, profile, adapter, options, usage, account, usageGate;
test.beforeEach(async () => {
  profile = await mkdtemp(path.join(os.tmpdir(), 'codex-pet-alerts-'));
  let executablePath = process.env.CHROMIUM_PATH;
  if (!executablePath) { try { await access('/usr/bin/chromium'); executablePath = '/usr/bin/chromium'; } catch {} }
  context = await chromium.launchPersistentContext(profile, { executablePath, channel: 'chromium', headless: true, args: ['--no-sandbox', '--disable-gpu'], viewport: { width: 800, height: 1000 } });
  usage = webUsage(); account = 'account-a'; usageGate = null;
  await context.route('https://chatgpt.com/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: { accessToken: 'TEST_TOKEN_NOT_STORED', account: { id: account } } });
    if (url.pathname === '/backend-api/wham/usage') { if (usageGate) await usageGate(); return route.fulfill({ json: usage }); }
    if (url.pathname === '/backend-api/wham/tasks/list') return route.fulfill({ json: webTasks() });
    return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>ChatGPT fixture</title>' });
  });
  adapter = await harness(context);
  options = await context.newPage();
  await options.goto(TEST_ORIGIN + '/options.html');
  const opened = context.waitForEvent('page');
  await options.locator('#connect').click();
  await (await opened).waitForLoadState();
  await options.bringToFront();
  await refresh();
});
test.afterEach(async () => { await context?.close(); if (profile) await rm(profile, { recursive: true, force: true }); });

async function refresh() { return options.evaluate(() => chrome.runtime.sendMessage({ type: 'refresh' })); }
async function enable() {
  await options.getByRole('switch').check();
  await expect(options.getByRole('switch')).toBeEnabled();
  await expect(options.getByRole('switch')).toBeChecked();
  await expect.poll(() => adapter.inspect().session.usageWatch?.windows.primary?.bucket).toBe(2);
}

test('notification settings are opt-in and the test button sends a quiet preview', async ({}, testInfo) => {
  await expect(options.getByRole('switch')).not.toBeChecked();
  await expect(options.locator('#test-notification')).toBeDisabled();
  expect(adapter.notifications()).toHaveLength(0);
  await enable();
  expect(adapter.notifications()).toHaveLength(0);
  await options.locator('#test-notification').click();
  await expect(options.locator('#feedback')).toContainText('테스트 알림을 보냈어요');
  expect(adapter.notifications()).toHaveLength(1);
  expect(adapter.notifications()[0].id).toBe('codex-usage-test');
  expect(adapter.notifications()[0].options.silent).toBe(true);
  await options.screenshot({ path: testInfo.outputPath('notification-settings.png'), fullPage: true });
});

test('quota drops aggregate both windows and stay deduplicated across worker restarts', async () => {
  await enable();
  usage.rate_limit.primary_window.used_percent = 31;
  usage.rate_limit.secondary_window.used_percent = 21;
  await refresh();
  expect(adapter.notifications()).toHaveLength(1);
  expect(adapter.notifications()[0].options.message).toContain('70% 구간 통과 · 현재 69% 남음');
  expect(adapter.notifications()[0].options.message).toContain('80% 구간 통과 · 현재 79% 남음');
  await refresh();
  await adapter.restartWorker();
  await refresh();
  expect(adapter.notifications()).toHaveLength(1);
  usage.rate_limit.primary_window.used_percent = 74;
  await refresh();
  expect(adapter.notifications()).toHaveLength(2);
  expect(adapter.notifications()[1].options.message).toContain('60%·50%·40%·30%');
  expect(adapter.inspect().local).not.toHaveProperty('usageWatch');
});

test('reset periods, account switches, and disabling notifications never replay old steps', async () => {
  await enable();
  usage.rate_limit.primary_window.used_percent = 31;
  await refresh();
  expect(adapter.notifications()).toHaveLength(1);
  usage.rate_limit.primary_window.reset_at += 18000;
  usage.rate_limit.primary_window.used_percent = 5;
  await refresh();
  expect(adapter.notifications()).toHaveLength(1);
  usage.rate_limit.primary_window.used_percent = 25;
  await refresh();
  expect(adapter.notifications()).toHaveLength(2);
  account = 'account-b';
  usage.rate_limit.primary_window.used_percent = 65;
  await refresh();
  expect(adapter.notifications()).toHaveLength(2);
  await options.getByRole('switch').uncheck();
  await expect(options.getByRole('switch')).toBeEnabled();
  usage.rate_limit.primary_window.used_percent = 99;
  await refresh();
  expect(adapter.notifications()).toHaveLength(2);
  expect(adapter.inspect().session.usageWatch).toBeNull();
});

test('declining optional notification permission leaves the setting off', async () => {
  adapter.denyNotificationRequests();
  await options.getByRole('switch').check();
  await expect(options.getByRole('switch')).toBeEnabled();
  await expect(options.getByRole('switch')).not.toBeChecked();
  await expect(options.locator('#feedback')).toContainText('권한이 허용되지 않아');
  expect(adapter.inspect().local.usageNotificationsEnabled).not.toBe(true);
  expect(adapter.notifications()).toHaveLength(0);
});

test('permission revocation clears tracking and prevents subsequent alerts', async () => {
  await enable();
  adapter.revokeNotificationPermission();
  await expect(options.getByRole('switch')).not.toBeChecked();
  usage.rate_limit.primary_window.used_percent = 90;
  await refresh();
  expect(adapter.notifications()).toHaveLength(0);
  expect(adapter.inspect().session.usageWatch).toBeNull();
});

test('blocked Chrome notifications explain the problem and disable the preview', async () => {
  adapter.blockNotifications();
  await options.getByRole('switch').check();
  await expect(options.getByRole('switch')).toBeEnabled();
  await expect(options.locator('#notification-state')).toContainText('알림이 차단');
  await expect(options.locator('#test-notification')).toBeDisabled();
  usage.rate_limit.primary_window.used_percent = 90;
  await refresh();
  expect(adapter.notifications()).toHaveLength(0);
});

test('turning alerts off during an in-flight refresh prevents a late notification', async () => {
  await enable();
  let release, started;
  const gate = new Promise(resolve => { release = resolve; });
  const requestStarted = new Promise(resolve => { started = resolve; });
  usageGate = async () => { started(); await gate; };
  usage.rate_limit.primary_window.used_percent = 80;
  const pending = refresh();
  await requestStarted;
  try {
    await options.getByRole('switch').uncheck();
    await expect(options.getByRole('switch')).toBeEnabled();
  } finally { release(); }
  await pending;
  expect(adapter.notifications()).toHaveLength(0);
  expect(adapter.inspect().session.usageWatch).toBeNull();
});
