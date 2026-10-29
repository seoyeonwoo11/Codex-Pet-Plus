import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readChatGPT } from '../extension/lib/browser-reader.js';
import { webUsage, webTasks } from './fixtures.mjs';

test('session token is used only in same-site requests and never returned', async t => {
  const calls = [];
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    calls.push({ url, options });
    const data = url === '/api/auth/session' ? { accessToken: 'TEST_PRIVATE_TOKEN', user: { email: 'private@example.test' }, account: { id: 'account-test' } } : url.includes('/usage') ? { ...webUsage(), secret_debug: 'PRIVATE_ACCOUNT_DATA' } : webTasks();
    return Response.json(data);
  });
  const result = await readChatGPT();
  assert.equal(result.ok, true);
  assert.equal(calls.length, 3);
  assert.equal(calls[1].options.headers.Authorization, 'Bearer TEST_PRIVATE_TOKEN');
  assert.ok(calls.every(call => call.url.startsWith('/') && call.options.credentials === 'same-origin'));
  assert.doesNotMatch(JSON.stringify(result), /TEST_PRIVATE_TOKEN|private@example|PRIVATE_ACCOUNT_DATA|accessToken|account-test/);
  assert.match(result.accountKey, /^[a-f0-9]{64}$/);
  assert.equal(result.usage.rate_limit.primary_window.used_percent, 28);
});
test('alert account key is stable per account and changes without exposing identity', async t => {
  let account = 'account-a';
  t.mock.method(globalThis, 'fetch', async url => Response.json(url === '/api/auth/session' ? { accessToken: 'test', account: { id: account } } : url.includes('/usage') ? webUsage() : webTasks()));
  const first = await readChatGPT();
  assert.equal((await readChatGPT()).accountKey, first.accountKey);
  account = 'account-b';
  assert.notEqual((await readChatGPT()).accountKey, first.accountKey);
});
test('signed-out session makes no quota or task requests', async t => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => Response.json({}));
  assert.deepEqual(await readChatGPT(), { ok: false, code: 'login_required' });
  assert.equal(fetch.mock.callCount(), 1);
});
test('quota failure does not discard a successful task list', async t => {
  t.mock.method(globalThis, 'fetch', async url => url === '/api/auth/session' ? Response.json({ accessToken: 'test' }) : url.includes('/usage') ? new Response('', { status: 429 }) : Response.json(webTasks()));
  const result = await readChatGPT();
  assert.equal(result.ok, true);
  assert.equal(result.usage, null);
  assert.equal(result.usageError, 'rate_limited');
  assert.equal(result.tasks.items.length, 2);
});
test('access denials are surfaced without leaking exception details', async t => {
  t.mock.method(globalThis, 'fetch', async () => new Response('', { status: 403 }));
  assert.deepEqual(await readChatGPT(), { ok: false, code: 'access_denied' });
});
test('HTML challenge responses are not mistaken for account data', async t => {
  t.mock.method(globalThis, 'fetch', async () => new Response('<html>challenge</html>'));
  assert.deepEqual(await readChatGPT(), { ok: false, code: 'unexpected_response' });
});
test('changed JSON response shapes produce explicit compatibility errors', async t => {
  t.mock.method(globalThis, 'fetch', async url => Response.json(url === '/api/auth/session' ? { accessToken: 'test' } : { unexpected: true }));
  const result = await readChatGPT();
  assert.equal(result.usage, null);
  assert.equal(result.tasks, null);
  assert.equal(result.usageError, 'unexpected_response');
  assert.equal(result.tasksError, 'unexpected_response');
});
