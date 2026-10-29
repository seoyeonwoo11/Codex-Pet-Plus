const $ = selector => document.querySelector(selector);
let savingNotifications = false;
function show(response) {
  if (!response?.state) return;
  const { browser } = response.state;
  $('#browser-state').textContent = browser.status === 'connected' ? '브라우저 연결됨 · 열린 ChatGPT 페이지에서 갱신합니다' : browser.status === 'login_required' ? '공식 ChatGPT 페이지에서 로그인이 필요합니다' : browser.status === 'no_tab' ? 'ChatGPT 페이지를 열어 주세요' : '브라우저 연결 전';
  if (response.settings) {
    const { usageNotificationsEnabled: enabled, notificationPermission: permission } = response.settings;
    if (!savingNotifications) $('#usage-notifications').checked = enabled;
    $('#test-notification').disabled = !enabled || permission !== 'granted';
    $('#notification-state').textContent = !enabled ? '사용량 알림 꺼짐' : permission !== 'granted' ? 'Chrome 알림이 차단되어 있어요. Chrome과 운영체제의 알림 설정을 확인해 주세요.' : browser.status === 'connected' && !browser.data?.accountKey ? '로그인 계정을 확인하지 못해 자동 알림을 기다리는 중이에요.' : '알림 켜짐 · 다음 10% 단계부터 조용히 알려드려요.';
  }
}
async function action(message) {
  const response = await chrome.runtime.sendMessage(message);
  if (response?.error) $('#feedback').textContent = '작업을 완료하지 못했습니다. 다시 시도해 주세요.';
  else show(response);
  return response;
}
for (const [selector, message] of [['#connect', { type: 'connect' }], ['#disconnect', { type: 'disconnect' }]]) {
  $(selector).addEventListener('click', async () => {
    $(selector).disabled = true;
    try { await action(message); }
    catch { $('#feedback').textContent = '연결을 확인하지 못했습니다. 다시 시도해 주세요.'; }
    finally { $(selector).disabled = false; }
  });
}
$('#usage-notifications').addEventListener('change', async event => {
  const toggle = event.currentTarget;
  const enabled = toggle.checked;
  savingNotifications = true;
  toggle.disabled = true;
  $('#feedback').textContent = '';
  try {
    if (enabled && !await chrome.permissions.request({ permissions: ['notifications'] })) {
      $('#feedback').textContent = '알림 권한이 허용되지 않아 설정을 켜지 않았습니다.';
      return;
    }
    await action({ type: 'usage-notifications', enabled });
  } catch { $('#feedback').textContent = '알림 설정을 저장하지 못했습니다. 다시 시도해 주세요.'; }
  finally {
    savingNotifications = false;
    toggle.disabled = false;
    show(await chrome.runtime.sendMessage({ type: 'state' }));
  }
});
$('#test-notification').addEventListener('click', async () => {
  const button = $('#test-notification');
  button.disabled = true;
  try {
    const response = await chrome.runtime.sendMessage({ type: 'test-notification' });
    $('#feedback').textContent = response?.ok ? '테스트 알림을 보냈어요. 화면 모서리의 Chrome 알림을 확인해 주세요.' : '알림을 표시하지 못했어요. Chrome과 운영체제의 알림 허용 상태를 확인해 주세요.';
  } catch { $('#feedback').textContent = '테스트 알림을 보내지 못했습니다. 다시 시도해 주세요.'; }
  finally { show(await chrome.runtime.sendMessage({ type: 'state' })); }
});
chrome.storage.onChanged.addListener((changes, area) => {
  if ((area === 'session' && changes.state?.newValue) || (area === 'local' && changes.usageNotificationsEnabled)) void chrome.runtime.sendMessage({ type: 'state' }).then(show);
});
show(await chrome.runtime.sendMessage({ type: 'state' }));
