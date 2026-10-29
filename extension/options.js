const $ = selector => document.querySelector(selector);
function show(response) {
  if (!response?.state) return;
  const { browser } = response.state;
  $('#browser-state').textContent = browser.status === 'connected' ? '브라우저 연결됨 · 열린 ChatGPT 페이지에서 갱신합니다' : browser.status === 'login_required' ? '공식 ChatGPT 페이지에서 로그인이 필요합니다' : browser.status === 'no_tab' ? 'ChatGPT 페이지를 열어 주세요' : '브라우저 연결 전';
}
async function action(message) {
  const response = await chrome.runtime.sendMessage(message);
  if (response?.error) $('#feedback').textContent = '작업을 완료하지 못했습니다. 다시 시도해 주세요.';
  else show(response);
}
for (const [selector, message] of [['#connect', { type: 'connect' }], ['#disconnect', { type: 'disconnect' }]]) {
  $(selector).addEventListener('click', async () => {
    $(selector).disabled = true;
    try { await action(message); }
    catch { $('#feedback').textContent = '연결을 확인하지 못했습니다. 다시 시도해 주세요.'; }
    finally { $(selector).disabled = false; }
  });
}
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'session' && changes.state?.newValue) show({ state: changes.state.newValue }); });
show(await chrome.runtime.sendMessage({ type: 'state' }));
