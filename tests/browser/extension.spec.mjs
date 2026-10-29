import { test, expect, chromium } from '@playwright/test';
import { mkdtemp, rm, access } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { webUsage, webTasks } from '../fixtures.mjs';
import { harness, TEST_ORIGIN } from './harness.mjs';

const extensionPath = fileURLToPath(new URL('../../extension', import.meta.url));
let context, profile, id;
let loggedIn, usageStatus, tasksStatus, usageOverride, tasksOverride, delayUsage;
const privateToken = 'FAKE_TOKEN_MUST_STAY_IN_PAGE';
let errors;
let adapter, origin;
const realExtension = process.env.PET_TEST_REAL_EXTENSION === '1';

test.beforeEach(async () => {
  profile = await mkdtemp(path.join(os.tmpdir(), 'codex-pet-chrome-'));
  let executablePath = process.env.CHROMIUM_PATH;
  if (!executablePath) { try { await access('/usr/bin/chromium'); executablePath = '/usr/bin/chromium'; } catch {} }
  context = await chromium.launchPersistentContext(profile, {
    executablePath, channel: 'chromium', headless: true, viewport: { width: 398, height: 760 }, timezoneId: 'Asia/Seoul',
    ignoreDefaultArgs: realExtension ? ['--disable-extensions'] : [],
    args: ['--no-sandbox', '--disable-gpu', ...(realExtension ? ['--disable-extensions-except=' + extensionPath, '--load-extension=' + extensionPath] : [])],
  });
  loggedIn = true; usageStatus = 200; tasksStatus = 200; usageOverride = null; tasksOverride = null; delayUsage = null; errors = [];
  context.on('page', page => page.on('pageerror', error => errors.push(error.message)));
  await context.route('https://chatgpt.com/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: loggedIn ? { accessToken: privateToken, account: { id: 'fixture-account' }, user: { email: 'not-stored@example.test' } } : {} });
    if (url.pathname === '/backend-api/wham/usage') { if (delayUsage) await delayUsage(); return route.fulfill({ status: usageStatus, json: usageOverride ?? webUsage() }); }
    if (url.pathname === '/backend-api/wham/tasks/list') return route.fulfill({ status: tasksStatus, json: tasksOverride ?? webTasks() });
    return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>ChatGPT test fixture</title><h1>Official login page placeholder</h1>' });
  });
  if (realExtension) {
    const worker = context.serviceWorkers()[0] ?? await context.waitForEvent('serviceworker', { timeout: 10000 });
    id = new URL(worker.url()).host;
    origin = 'chrome-extension://' + id;
  } else {
    adapter = await harness(context);
    id = adapter.id;
    origin = TEST_ORIGIN;
  }
});

test.afterEach(async () => {
  await context?.close();
  if (profile) await rm(profile, { recursive: true, force: true });
});

async function dashboard() {
  const page = await context.newPage();
  await page.goto(origin + '/popup.html');
  await expect(page.locator('#refresh')).toBeEnabled();
  return page;
}
async function connect(page) {
  const opened = context.waitForEvent('page');
  await page.locator('#connect').click();
  const chatgpt = await opened;
  await chatgpt.waitForLoadState();
  await page.bringToFront();
  await page.locator('#refresh').click();
  return chatgpt;
}

test('browser-only login renders real normalized quotas, projects, filters, and safe task links', async ({}, testInfo) => {
  const page = await dashboard();
  await expect(page.locator('#quota-primary .quota-value')).toContainText('—');
  await expect(page.locator('#connect')).toBeVisible();
  expect((await page.locator('#connect').boundingBox()).y + (await page.locator('#connect').boundingBox()).height).toBeLessThan(600);
  await page.screenshot({ path: testInfo.outputPath('welcome.png'), fullPage: true });
  await connect(page);
  await expect(page.locator('#quota-primary .quota-value')).toHaveText('72%남음');
  await expect(page.locator('#quota-secondary .quota-value')).toHaveText('84%남음');
  await expect(page.locator('#project-name')).toHaveText('Codex-Pet-Plus');
  await expect(page.locator('#project-label')).toHaveText('실행 중인 웹 프로젝트');
  await expect(page.locator('.task')).toHaveCount(2);
  await expect(page.locator('.task-title').last()).toHaveText('<img src=x onerror=alert(1)>');
  await expect(page.locator('#task-list img')).toHaveCount(0);
  await page.locator('#active-only').click();
  await expect(page.locator('.task')).toHaveCount(1);
  await page.locator('#active-only').click();
  const saved = realExtension ? await context.serviceWorkers()[0].evaluate(async () => ({ local: await chrome.storage.local.get(null), session: await chrome.storage.session.get(null), badge: await chrome.action.getBadgeText({}) })) : adapter.inspect();
  expect(JSON.stringify(saved)).not.toContain(privateToken);
  expect(JSON.stringify(saved)).not.toContain('not-stored@example.test');
  expect(saved.badge).toBe('1');
  const taskOpened = context.waitForEvent('page');
  await page.locator('.task-link').first().click();
  const taskPage = await taskOpened;
  await expect(taskPage).toHaveURL('https://chatgpt.com/codex/tasks/task_web_running');
  await page.bringToFront();
  await page.screenshot({ path: testInfo.outputPath('dashboard.png'), fullPage: true });
  expect(errors).toEqual([]);
});

test('expired browser login clears usage and task data', async () => {
  const page = await dashboard();
  await connect(page);
  await expect(page.locator('#quota-primary .quota-value')).toContainText('72%');
  loggedIn = false;
  await page.locator('#refresh').click();
  await expect(page.locator('#connect-copy')).toContainText('로그인이 필요해요');
  await expect(page.locator('#quota-primary .quota-value')).toContainText('—');
  await expect(page.locator('.task')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('partial failures and expired reset values do not invent remaining allowance', async () => {
  const page = await dashboard();
  usageStatus = 429;
  await connect(page);
  await expect(page.locator('#quota-error')).toContainText('요청이 잠시 제한');
  await expect(page.locator('#quota-primary .quota-value')).toContainText('—');
  await expect(page.locator('.task')).toHaveCount(2);
  usageStatus = 200;
  usageOverride = webUsage();
  usageOverride.rate_limit.primary_window.reset_at = Math.floor(Date.now() / 1000) - 60;
  tasksStatus = 403;
  await page.locator('#refresh').click();
  await expect(page.locator('#quota-primary .quota-reset')).toContainText('초기화 시각 지남');
  await expect(page.locator('#quota-primary .quota-value')).toContainText('—');
  await expect(page.locator('#tasks-error')).toContainText('접근을 허용하지');
  expect(errors).toEqual([]);
});

test('settings disconnect clears cache and stops background polling', async () => {
  const page = await dashboard();
  await connect(page);
  await expect(page.locator('.task')).toHaveCount(2);
  const options = await context.newPage();
  await options.goto(origin + '/options.html');
  await options.locator('#disconnect').click();
  await expect(options.locator('#browser-state')).toHaveText('브라우저 연결 전');
  const saved = realExtension ? await context.serviceWorkers()[0].evaluate(async () => ({ state: (await chrome.storage.session.get('state')).state, alarm: await chrome.alarms.get('refresh') })) : adapter.disconnected();
  expect(saved.state.browser.data).toBeNull();
  expect(saved.alarm).toBeUndefined();
  expect(errors).toEqual([]);
});

test('panel layout fits a narrow Chrome side panel', async () => {
  const page = await dashboard();
  await connect(page);
  const panel = await context.newPage();
  await panel.setViewportSize({ width: 320, height: 800 });
  await panel.goto(origin + '/panel.html');
  await expect(panel.locator('#quota-primary .quota-value')).toContainText('72%');
  expect(await panel.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
  await expect(panel.locator('#panel')).toBeHidden();
  expect(errors).toEqual([]);
});

test('disconnect during an in-flight refresh cannot resurrect cached account data', async () => {
  const page = await dashboard();
  await connect(page);
  await expect(page.locator('#quota-primary .quota-value')).toContainText('72%');
  await expect(page.locator('#refresh')).toBeEnabled();
  let release;
  let started;
  const requestStarted = new Promise(resolve => { started = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  delayUsage = async () => { started(); await gate; };
  await page.locator('#refresh').click();
  await requestStarted;
  const options = await context.newPage();
  await options.goto(origin + '/options.html');
  try {
    await options.locator('#disconnect').click();
    await expect(options.locator('#browser-state')).toHaveText('브라우저 연결 전');
  } finally { release(); }
  await expect(page.locator('#refresh')).toBeEnabled();
  const saved = realExtension ? await context.serviceWorkers()[0].evaluate(async () => ({ state: (await chrome.storage.session.get('state')).state })) : adapter.disconnected();
  expect(saved.state.browser.status).toBe('disconnected');
  expect(saved.state.browser.data).toBeNull();
  expect(errors).toEqual([]);
});
