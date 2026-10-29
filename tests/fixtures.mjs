export function webUsage() {
  return { plan_type: 'plus', rate_limit: { primary_window: { used_percent: 28, limit_window_seconds: 18000, reset_at: Math.floor((Date.now() + 3600000) / 1000) }, secondary_window: { used_percent: 16, limit_window_seconds: 604800, reset_at: Math.floor((Date.now() + 86400000) / 1000) } } };
}
export function webTasks() {
  return { cursor: null, items: [
    { id: 'task_web_running', title: '로그인 화면 개선하기', updated_at: Date.now() / 1000, task_status_display: { environment_label: 'Codex-Pet-Plus', latest_turn_status_display: { turn_status: 'in_progress' } } },
    { id: 'task_web_done', title: '<img src=x onerror=alert(1)>', updated_at: Date.now() / 1000 - 60, task_status_display: { environment_label: 'pet-web', latest_turn_status_display: { turn_status: 'completed' } } },
  ] };
}
