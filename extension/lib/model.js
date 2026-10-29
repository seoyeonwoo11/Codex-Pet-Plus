export const WEB_STALE_MS = 2 * 60 * 1000;

export function cleanText(value, max = 160) {
  return typeof value === 'string' ? value.replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, max) : '';
}

export function timestamp(value) {
  const ms = typeof value === 'number' ? (value < 1e12 ? value * 1000 : value) : Date.parse(value);
  const date = new Date(ms);
  return Number.isFinite(ms) && ms > 0 && Number.isFinite(date.valueOf()) ? date.toISOString() : null;
}

export function normalizeWindow(raw) {
  if (!raw || typeof raw.used_percent !== 'number' || !Number.isFinite(raw.used_percent) || raw.used_percent < 0 || raw.used_percent > 100) return null;
  const minutes = raw.limit_window_seconds / 60;
  return {
    usedPercent: raw.used_percent,
    remainingPercent: Math.round((100 - raw.used_percent) * 10) / 10,
    windowMinutes: Number.isFinite(minutes) && minutes > 0 ? minutes : null,
    resetsAt: timestamp(raw.reset_at),
  };
}

export function normalizeWebUsage(raw, now = new Date().toISOString()) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw) || !('rate_limit' in raw || 'plan_type' in raw || 'credits' in raw)) {
    throw new Error('unsupported_usage');
  }
  return {
    source: 'web', recordedAt: now, limitId: 'codex',
    plan: cleanText(raw.plan_type, 40),
    primary: normalizeWindow(raw.rate_limit?.primary_window),
    secondary: normalizeWindow(raw.rate_limit?.secondary_window),
  };
}

export function normalizeWebTasks(raw) {
  if (!raw || !Array.isArray(raw.items)) throw new Error('unsupported_tasks');
  return {
    hasMore: typeof raw.cursor === 'string' && raw.cursor.length > 0,
    tasks: raw.items.slice(0, 50).filter(item => typeof item?.id === 'string').map(item => {
      const display = item.task_status_display;
      const value = display?.latest_turn_status_display?.turn_status ?? display?.state;
      const states = { in_progress: 'working', pending: 'queued', completed: 'completed', ready: 'completed', applied: 'completed', failed: 'failed', error: 'failed', cancelled: 'interrupted' };
      const status = Object.hasOwn(states, value) ? states[value] : 'unknown';
      return {
        id: cleanText(item.id, 200), title: cleanText(item.title) || '이름 없는 작업',
        source: 'web', status, stage: '',
        project: cleanText(display?.environment_label, 120) || null,
        cwd: null, updatedAt: timestamp(item.updated_at),
      };
    }),
  };
}

export function visibleTasks(state, activeOnly = false, now = Date.now()) {
  const browser = state.browser?.data?.tasks ?? [];
  return browser.map(task => {
    const fetchedAt = state.browser?.data?.fetchedAt;
    const connected = state.browser.status === 'connected';
    const fresh = fetchedAt && now - Date.parse(fetchedAt) <= WEB_STALE_MS && now - Date.parse(fetchedAt) >= -5000;
    return ['working', 'waiting', 'queued'].includes(task.status) && (!fresh || !connected) ? { ...task, status: 'unknown' } : task;
  }).filter(task => !activeOnly || ['working', 'waiting', 'queued'].includes(task.status))
    .sort((a, b) => {
      const rank = t => ['working', 'waiting', 'queued'].includes(t.status) ? 0 : 1;
      return rank(a) - rank(b) || (Date.parse(b.updatedAt) || 0) - (Date.parse(a.updatedAt) || 0);
    });
}

export function selectedUsage(state) {
  return state.browser?.data?.usage ?? null;
}

export function windowLabel(window, fallback) {
  const m = window?.windowMinutes;
  if (!m) return fallback;
  if (m >= 1440 && m % 1440 === 0) return m === 10080 ? '주간 한도' : `${m / 1440}일 한도`;
  return m >= 60 && m % 60 === 0 ? `${m / 60}시간 한도` : `${Math.round(m)}분 한도`;
}

export function emptyState() {
  return { browser: { status: 'disconnected', data: null }, refreshedAt: null };
}
