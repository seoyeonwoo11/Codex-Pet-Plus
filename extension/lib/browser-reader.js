// Runs in ChatGPT's page context. Never return, log, or persist the access token.
// Keep this function self-contained: Chrome serializes it for executeScript.
export async function readChatGPT() {
  const request = async (path, headers = {}) => {
    const response = await fetch(path, {
      credentials: 'same-origin', cache: 'no-store', headers,
      signal: AbortSignal.timeout(12000), redirect: 'error',
    });
    if (response.status === 401) throw new Error('login_required');
    if (response.status === 403) throw new Error('access_denied');
    if (response.status === 429) throw new Error('rate_limited');
    if (!response.ok) throw new Error('service_unavailable');
    try { return await response.json(); } catch { throw new Error('unexpected_response'); }
  };
  const failure = error => ({ ok: false, code: ['login_required', 'access_denied', 'rate_limited', 'unexpected_response'].includes(error.message) ? error.message : 'service_unavailable' });
  try {
    const session = await request('/api/auth/session');
    if (typeof session?.accessToken !== 'string' || !session.accessToken) return { ok: false, code: 'login_required' };
    // Scope quota alerts without returning account IDs, email, or credentials.
    let accountKey = null;
    const identity = [session.account?.id, session.user?.id].map(value => typeof value === 'string' && value ? value : null);
    if (identity.some(Boolean)) {
      const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify(identity)));
      accountKey = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
    }
    const headers = { Authorization: `Bearer ${session.accessToken}` };
    if (typeof session.account?.id === 'string') headers['ChatGPT-Account-Id'] = session.account.id;
    const results = await Promise.allSettled([
      request('/backend-api/wham/usage', headers),
      request('/backend-api/wham/tasks/list?limit=50&task_filter=current', headers),
    ]);
    const usage = results[0].status === 'fulfilled' ? results[0].value : null;
    const tasks = results[1].status === 'fulfilled' ? results[1].value : null;
    const usageValid = usage && typeof usage === 'object' && !Array.isArray(usage) && ('rate_limit' in usage || 'plan_type' in usage || 'credits' in usage);
    const tasksValid = tasks && Array.isArray(tasks.items);
    // Whitelist fields at the page boundary; account/session objects never cross it.
    const window = raw => raw ? { used_percent: raw.used_percent, limit_window_seconds: raw.limit_window_seconds, reset_at: raw.reset_at } : null;
    return {
      ok: true,
      accountKey,
      usage: usageValid ? { plan_type: usage.plan_type, rate_limit: usage.rate_limit ? { primary_window: window(usage.rate_limit.primary_window), secondary_window: window(usage.rate_limit.secondary_window) } : null } : null,
      tasks: tasksValid ? { cursor: tasks.cursor, items: tasks.items.slice(0, 50).filter(item => item && typeof item === 'object').map(item => ({ id: item.id, title: item.title, updated_at: item.updated_at, task_status_display: { environment_label: item.task_status_display?.environment_label, state: item.task_status_display?.state, latest_turn_status_display: { turn_status: item.task_status_display?.latest_turn_status_display?.turn_status } } })) } : null,
      usageError: results[0].status === 'rejected' ? failure(results[0].reason).code : (usageValid ? null : 'unexpected_response'),
      tasksError: results[1].status === 'rejected' ? failure(results[1].reason).code : (tasksValid ? null : 'unexpected_response'),
    };
  } catch (error) { return failure(error); }
}
