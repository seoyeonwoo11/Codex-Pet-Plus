import { test } from 'node:test';
import assert from 'node:assert/strict';
import { emptyState, normalizeWebUsage, normalizeWebTasks, normalizeWindow, timestamp, visibleTasks, windowLabel } from '../extension/lib/model.js';
import { webUsage, webTasks } from './fixtures.mjs';

test('allowance is quota remaining, not context or token totals', () => {
  const usage = normalizeWebUsage(webUsage());
  assert.equal(usage.primary.remainingPercent, 72);
  assert.equal(usage.secondary.remainingPercent, 84);
  assert.equal(windowLabel(usage.primary), '5시간 한도');
  assert.equal(windowLabel(usage.secondary), '주간 한도');
  assert.equal(normalizeWindow({ used_percent: 100 }).remainingPercent, 0);
  assert.equal(normalizeWindow({ used_percent: 0 }).remainingPercent, 100);
});
test('missing and invalid windows stay unknown', () => {
  for (const used_percent of [null, undefined, '28', -1, 101, NaN, Infinity]) assert.equal(normalizeWindow({ used_percent }), null);
  assert.equal(normalizeWebUsage({ plan_type: 'free' }).primary, null);
  assert.equal(timestamp(1e100), null);
  assert.throws(() => normalizeWebUsage({ error: 'unavailable' }), /unsupported_usage/);
});
test('cloud status vocabulary preserves unknown and queued states', () => {
  const tasks = normalizeWebTasks(webTasks()).tasks;
  assert.deepEqual(tasks.map(task => task.status), ['working', 'completed']);
  assert.equal(tasks[0].project, 'Codex-Pet-Plus');
  assert.equal(normalizeWebTasks({ items: [{ id: 'unknown', task_status_display: { state: 'new_status' } }] }).tasks[0].status, 'unknown');
  assert.equal(normalizeWebTasks({ items: [{ id: 'unknown', task_status_display: { state: '__proto__' } }] }).tasks[0].status, 'unknown');
  assert.equal(normalizeWebTasks({ items: [{ id: 'waiting', task_status_display: { state: 'pending' } }] }).tasks[0].status, 'queued');
  assert.throws(() => normalizeWebTasks({ data: [] }), /unsupported_tasks/);
});
test('failed connections and stale snapshots never count as currently running', () => {
  const now = Date.now();
  const state = emptyState();
  state.browser = { status: 'connected', data: { tasks: normalizeWebTasks(webTasks()).tasks, fetchedAt: new Date(now).toISOString() } };
  assert.equal(visibleTasks(state, true, now).length, 1);
  assert.equal(visibleTasks(state, true, now + 120001).length, 0);
  state.browser.status = 'service_unavailable';
  assert.equal(visibleTasks(state, true, now).length, 0);
});
test('server-provided titles are plain text, bounded, and not guessed', () => {
  const task = normalizeWebTasks({ items: [{ id: 'x', title: 'a'.repeat(500) }] }).tasks[0];
  assert.equal(task.title.length, 160);
  assert.equal(task.project, null);
  assert.equal(task.status, 'unknown');
});
