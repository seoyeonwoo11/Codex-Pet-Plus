import { test } from 'node:test';
import assert from 'node:assert/strict';
import { planUsageAlerts, usageNotification } from '../extension/lib/usage-alerts.js';

const now = Date.now();
const account = 'a'.repeat(64);
const reset = new Date(now + 3600000).toISOString();
function usage(primary, secondary = null, resetsAt = reset) {
  const window = value => value === null ? null : { usedPercent: value, windowMinutes: 300, resetsAt };
  return { recordedAt: new Date(now).toISOString(), plan: 'plus', primary: window(primary), secondary: window(secondary) };
}
const advance = (values, previous, key = account) => planUsageAlerts(values, key, previous, now);

test('first observation establishes a baseline without historical alerts', () => {
  const result = advance(usage(78, 44));
  assert.deepEqual(result.events, []);
  assert.equal(result.watch.windows.primary.bucket, 7);
  assert.equal(result.watch.windows.secondary.bucket, 4);
});
test('crossing exactly ten percentage points alerts once per quota period', () => {
  let result = advance(usage(9.9));
  result = advance(usage(10), result.watch);
  assert.deepEqual(result.events[0].thresholds, [90]);
  assert.equal(result.events[0].remainingPercent, 90);
  assert.deepEqual(advance(usage(10), result.watch).events, []);
  assert.deepEqual(advance(usage(19.9), result.watch).events, []);
  assert.deepEqual(advance(usage(20), result.watch).events[0].thresholds, [80]);
});
test('large drops combine all crossed steps in one notification', () => {
  const initial = advance(usage(2));
  const result = advance(usage(39), initial.watch);
  assert.deepEqual(result.events[0].thresholds, [90, 80, 70]);
  const notification = usageNotification(account, result.events, 'icon.png');
  assert.match(notification.options.message, /90%·80%·70%/);
  assert.match(notification.options.message, /현재 61% 남음/);
  assert.equal(notification.options.silent, true);
  assert.equal(notification.options.requireInteraction, false);
});
test('primary and secondary quotas advance independently and share one message', () => {
  const initial = advance(usage(28, 16));
  const result = advance(usage(31, 21), initial.watch);
  assert.equal(result.events.length, 2);
  assert.deepEqual(result.events.map(event => event.thresholds), [[70], [80]]);
  assert.equal(usageNotification(account, result.events, 'icon').options.message.split('\n').length, 2);
});
test('an upward correction cannot replay a threshold already crossed', () => {
  let result = advance(usage(28));
  result = advance(usage(31), result.watch);
  result = advance(usage(21), result.watch);
  assert.deepEqual(result.events, []);
  result = advance(usage(31), result.watch);
  assert.deepEqual(result.events, []);
  assert.deepEqual(advance(usage(41), result.watch).events[0].thresholds, [60]);
});
test('new reset periods and account changes start new baselines', () => {
  const initial = advance(usage(10));
  const newPeriod = usage(70, null, new Date(now + 7200000).toISOString());
  const resetResult = advance(newPeriod, initial.watch);
  assert.deepEqual(resetResult.events, []);
  assert.deepEqual(advance(usage(80, null, newPeriod.primary.resetsAt), resetResult.watch).events[0].thresholds, [20]);
  const otherAccount = advance(usage(70), initial.watch, 'b'.repeat(64));
  assert.deepEqual(otherAccount.events, []);
  assert.equal(otherAccount.watch.accountKey, 'b'.repeat(64));
});
test('expired or missing reset information, stale usage, and unknown accounts do not alert', () => {
  const initial = advance(usage(0));
  for (const resetsAt of [null, 'invalid', new Date(now - 1000).toISOString()]) assert.deepEqual(advance(usage(50, null, resetsAt), initial.watch).events, []);
  const stale = { ...usage(50), recordedAt: new Date(now - 120001).toISOString() };
  assert.equal(advance(stale, initial.watch).watch, null);
  assert.equal(advance(usage(50), initial.watch, null).watch, null);
});
test('a temporarily absent window retains its deduplication history', () => {
  let result = advance(usage(0, 0));
  result = advance(usage(30, 30), result.watch);
  result = advance(usage(31, null), result.watch);
  assert.deepEqual(result.events, []);
  assert.deepEqual(advance(usage(31, 30), result.watch).events, []);
});
test('zero remaining is the final step, without mutating prior state', () => {
  const initial = advance(usage(99));
  const previous = structuredClone(initial.watch);
  const result = advance(usage(100), initial.watch);
  assert.deepEqual(result.events[0].thresholds, [0]);
  assert.equal(result.events[0].remainingPercent, 0);
  assert.deepEqual(advance(usage(100), result.watch).events, []);
  assert.deepEqual(initial.watch, previous);
});
