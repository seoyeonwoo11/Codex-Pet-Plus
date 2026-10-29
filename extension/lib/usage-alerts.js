import { WEB_STALE_MS, windowLabel } from './model.js';

// Only keep a high-water mark for each quota period, scoped to the current account.
// The caller stores this in storage.session, never persistent local storage.
export function planUsageAlerts(usage, accountKey, previous, now = Date.now()) {
  const age = now - Date.parse(usage?.recordedAt);
  if (!/^[a-f0-9]{64}$/.test(accountKey ?? '') || !Number.isFinite(age) || age < -5000 || age > WEB_STALE_MS) return { watch: null, events: [] };
  const watch = { accountKey, windows: {} };
  const events = [];
  const prior = previous?.accountKey === accountKey ? previous.windows : null;
  for (const key of ['primary', 'secondary']) {
    const window = usage[key];
    const old = prior?.[key];
    if (!window) {
      if (Date.parse(old?.resetsAt) > now) watch.windows[key] = old;
      continue;
    }
    if (!Number.isFinite(window.usedPercent) || window.usedPercent < 0 || window.usedPercent > 100 || !(Date.parse(window.resetsAt) > now)) continue;
    const period = JSON.stringify([window.resetsAt, window.windowMinutes, usage.plan]);
    const bucket = Math.floor(window.usedPercent / 10);
    const samePeriod = old?.period === period && Number.isInteger(old.bucket) && old.bucket >= 0 && old.bucket <= 10;
    watch.windows[key] = { period, resetsAt: window.resetsAt, bucket: samePeriod ? Math.max(old.bucket, bucket) : bucket };
    if (!samePeriod || bucket <= old.bucket) continue;
    events.push({
      key, bucket, resetsAt: window.resetsAt,
      label: windowLabel(window, key === 'primary' ? '기본 한도' : '추가 한도'),
      remainingPercent: Math.round((100 - window.usedPercent) * 10) / 10,
      thresholds: Array.from({ length: bucket - old.bucket }, (_, index) => 100 - (old.bucket + index + 1) * 10),
    });
  }
  return { watch, events };
}

export function usageNotification(accountKey, events, iconUrl) {
  return {
    id: 'codex-usage-' + accountKey.slice(0, 12) + '-' + events.map(event => event.key + '-' + Date.parse(event.resetsAt) + '-' + event.bucket).join('-'),
    options: {
      type: 'basic', iconUrl, title: 'Codex 남은 사용량 알림',
      message: events.map(event => event.label + ': ' + event.thresholds.map(value => value + '%').join('·') + ' 구간 통과 · 현재 ' + event.remainingPercent + '% 남음').join('\n'),
      silent: true, requireInteraction: false,
    },
  };
}
