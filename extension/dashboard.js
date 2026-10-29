import { emptyState, selectedUsage, visibleTasks, windowLabel } from './lib/model.js';

const icons = {
  refresh: '<path d="M13.5 6a5 5 0 1 0 .2 3M13.5 2.5V6H10"/>',
  settings: '<path d="m6 2-.5 1.5L4 4l-1.5-.2L1 6.5l1 1v1L1 9.5l1.5 2.7L4 12l1.5.5L6 14h3l.5-1.5L11 12l1.5.2L14 9.5l-1-1v-1l1-1-1.5-2.7L11 4l-1.5-.5L9 2Z"/><circle cx="7.5" cy="8" r="2"/>',
  panel: '<rect x="2" y="2.5" width="12" height="11" rx="2"/><path d="M9.5 3v10"/>',
};
const icon = name => `<svg viewBox="0 0 16 16" aria-hidden="true">${icons[name]}</svg>`;
document.querySelector('#app').innerHTML = `
<div class="dashboard">
  <header class="header"><div class="brand-mark"><img src="assets/pet.svg" alt=""></div><div><div class="brand-title">codex pet<span class="plus">PLUS</span></div><p class="brand-caption">코덱스 곁의 작은 작업 메이트</p></div><div class="header-actions"><button id="refresh" class="icon-button" aria-label="새로고침">${icon('refresh')}</button><button id="panel" class="icon-button" aria-label="사이드 패널 열기">${icon('panel')}</button><button id="settings" class="icon-button" aria-label="설정 열기">${icon('settings')}</button></div></header>
  <div class="section-heading"><span class="eyebrow">YOUR WORKSPACE</span><span class="connection-label"><i id="connection-dot" class="dot"></i><span id="connection-text">연결 전</span></span></div>
  <section class="hero" id="hero" aria-labelledby="project-name"><div class="hero-copy"><p class="eyebrow" id="project-label">작업 메이트를 만나보세요</p><h1 id="project-name">안녕, 코덱스!</h1><p class="hero-path" id="project-path">로그인하고 작업 현황을 확인하세요</p><span class="hero-status" id="project-status">연결을 기다리는 중</span></div><img class="pet" src="assets/pet.svg" alt="픽셀 고양이 작업 메이트"><span class="spark" aria-hidden="true">✦</span></section>
  <section class="quota-section" aria-labelledby="quota-heading"><div class="section-heading"><h2 id="quota-heading">남은 사용량</h2><span class="plan" id="plan">CODEX</span></div><div class="quotas"><article class="quota-card" id="quota-primary"><p class="quota-title">기본 한도</p><p class="quota-value">—<span>확인 전</span></p><div class="progress" role="progressbar" aria-label="기본 한도 남은 비율" aria-valuemin="0" aria-valuemax="100"><div class="progress-fill"></div></div><p class="quota-reset">로그인 후 확인할 수 있어요</p></article><article class="quota-card" id="quota-secondary"><p class="quota-title">추가 한도</p><p class="quota-value">—<span>확인 전</span></p><div class="progress" role="progressbar" aria-label="추가 한도 남은 비율" aria-valuemin="0" aria-valuemax="100"><div class="progress-fill"></div></div><p class="quota-reset">로그인 후 확인할 수 있어요</p></article></div><div class="source-note"><span id="usage-source">확인되지 않은 사용량은 표시하지 않아요</span><button class="text-button" id="usage-page">사용량 페이지 ↗</button></div><p id="quota-error" class="error-note" hidden></p></section>
  <section class="tasks-section" aria-labelledby="tasks-heading"><div class="section-heading"><h2 id="tasks-heading">웹 작업 현황<span id="task-count" class="count">0</span></h2><button id="active-only" class="filter-toggle" aria-pressed="false">진행 중만 보기</button></div><ul id="task-list" class="task-list"></ul><div id="empty-tasks" class="empty-tasks"><p id="empty-title">아직 연결된 작업이 없어요</p><p class="empty-detail" id="empty-detail">로그인하면 웹 작업을 가져올게요</p></div><p id="tasks-error" class="error-note" hidden></p></section>
  <section id="connect-card" class="connect-card"><h2 class="connect-title" id="connect-title">ChatGPT로 시작하기</h2><p class="connect-copy" id="connect-copy">공식 ChatGPT 페이지에서 로그인해 주세요.<br>비밀번호와 로그인 토큰은 저장하지 않아요.</p><button id="connect" class="primary-button">ChatGPT 로그인 · 연결</button></section>
  <p class="source-note">데스크톱 앱의 로컬 실행 상태는 웹 목록에 포함되지 않아요.</p>
  <p id="toast" class="toast" role="status" hidden></p><footer class="footer"><span><span class="leaf">⌁</span>내 작업, 한눈에 가볍게</span><span id="last-refresh">연결 대기 중</span></footer>
</div>`;

let current = emptyState();
let activeOnly = false;
let busy = false;
const $ = selector => document.querySelector(selector);
// Keep the first-login action above the fold in Chrome's 600px popup.
$('#hero').after($('#connect-card'));
const text = (selector, value) => { $(selector).textContent = value; };
const ago = value => {
  const seconds = (Date.now() - Date.parse(value)) / 1000;
  if (!Number.isFinite(seconds)) return '시각 미상';
  if (seconds < 60) return '방금';
  if (seconds < 3600) return `${Math.floor(seconds / 60)}분 전`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}시간 전`;
  return `${Math.floor(seconds / 86400)}일 전`;
};
const date = value => new Intl.DateTimeFormat('ko-KR', { month: 'numeric', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(new Date(value));
const statuses = { working: '실행 중', waiting: '확인 대기', queued: '대기 중', completed: '완료', failed: '실패', interrupted: '중단됨', unknown: '확인 필요' };
const errorCopy = {
  login_required: '로그인이 필요해요. 공식 ChatGPT 페이지에서 다시 로그인해 주세요.',
  access_denied: 'ChatGPT에서 접근을 허용하지 않았어요. 브라우저에서 Codex 페이지가 열리는지 확인해 주세요.',
  rate_limited: '요청이 잠시 제한됐어요. 잠시 후 새로고침해 주세요.',
  unexpected_response: '웹 응답 형식이 달라 정보를 읽지 못했어요. 사용량 페이지에서 직접 확인할 수 있어요.',
  service_unavailable: '지금은 ChatGPT에 연결하지 못했어요. 페이지가 열리는지 확인하고 다시 시도해 주세요.',
};

function renderQuota(key, window, usage) {
  const card = $(`#quota-${key}`);
  const label = windowLabel(window, key === 'primary' ? '기본 한도' : '추가 한도');
  card.querySelector('.quota-title').textContent = label;
  const expired = window?.resetsAt && Date.parse(window.resetsAt) <= Date.now();
  const value = card.querySelector('.quota-value');
  value.replaceChildren(document.createTextNode(window && !expired ? `${window.remainingPercent}%` : '—'));
  const caption = document.createElement('span');
  caption.textContent = window && !expired ? '남음' : '확인 불가';
  value.append(caption);
  const progress = card.querySelector('.progress');
  progress.setAttribute('aria-label', `${label} 남은 비율`);
  if (window && !expired) progress.setAttribute('aria-valuenow', String(window.remainingPercent));
  else progress.removeAttribute('aria-valuenow');
  card.querySelector('.progress-fill').style.width = window && !expired ? `${window.remainingPercent}%` : '0%';
  card.dataset.low = String(window && window.remainingPercent < 20);
  card.querySelector('.quota-reset').textContent = expired ? '초기화 시각 지남 · 새로고침 필요' : window?.resetsAt ? `${date(window.resetsAt)} 초기화` : usage ? '한도 정보가 제공되지 않았어요' : '로그인 후 확인할 수 있어요';
}

function render() {
  const tasks = visibleTasks(current, activeOnly);
  const allTasks = visibleTasks(current);
  const activeTasks = allTasks.filter(task => ['working', 'waiting', 'queued'].includes(task.status));
  const project = activeTasks.find(task => task.project) ?? allTasks.find(task => task.project);
  const connected = current.browser.status === 'connected';
  text('#connection-text', connected ? '연결됨' : '연결 전');
  $('#connection-dot').classList.toggle('on', connected);
  text('#project-name', project?.project ?? (connected ? '함께할 준비 완료' : '안녕, 코덱스!'));
  text('#project-label', project ? (['working', 'waiting', 'queued'].includes(project.status) ? '실행 중인 웹 프로젝트' : '최근 웹 프로젝트') : 'YOUR CODEX COMPANION');
  text('#project-path', project?.cwd ?? (project ? 'Codex 웹 작업의 프로젝트' : connected ? '작업을 시작하면 여기에 표시돼요' : '로그인하고 작업 현황을 확인하세요'));
  $('#project-path').title = project?.cwd ?? '';
  text('#project-status', project ? `웹 · ${statuses[project.status]}` : connected ? '새 작업을 기다리는 중' : '연결을 기다리는 중');
  $('#hero').dataset.working = String(activeTasks.length > 0);
  const usage = selectedUsage(current);
  renderQuota('primary', usage?.primary, usage);
  renderQuota('secondary', usage?.secondary, usage);
  text('#plan', usage?.plan || 'CODEX');
  text('#usage-source', usage ? `Chrome 로그인 계정 · ${ago(usage.recordedAt)}` : '확인되지 않은 사용량은 표시하지 않아요');
  $('#quota-error').hidden = !current.browser.usageError;
  text('#quota-error', errorCopy[current.browser.usageError] ?? '사용량 정보를 확인하지 못했어요.');
  $('#tasks-error').hidden = !current.browser.tasksError;
  text('#tasks-error', errorCopy[current.browser.tasksError] ?? '웹 작업 목록을 확인하지 못했어요.');
  $('#task-list').replaceChildren();
  text('#task-count', tasks.length);
  for (const task of tasks) {
    const li = document.createElement('li');
    li.className = 'task'; li.dataset.status = task.status;
    const badge = document.createElement('span'); badge.className = 'task-icon';
    badge.textContent = ({ working: '↗', waiting: '…', queued: '◷', completed: '✓', failed: '!', interrupted: '−', unknown: '?' })[task.status];
    const copy = document.createElement('button'); copy.className = 'task-copy task-link';
    copy.addEventListener('click', () => void chrome.runtime.sendMessage({ type: 'open-task', id: task.id }));
    const title = document.createElement('p'); title.className = 'task-title'; title.textContent = task.title; title.title = task.title;
    const meta = document.createElement('p'); meta.className = 'task-meta';
    for (const value of ['웹', task.project, ago(task.updatedAt)]) {
      if (!value) continue;
      const span = document.createElement('span'); span.textContent = value; meta.append(span);
    }
    copy.append(title, meta);
    const status = document.createElement('span'); status.className = 'task-status'; status.textContent = statuses[task.status];
    li.append(badge, copy, status); $('#task-list').append(li);
  }
  $('#empty-tasks').hidden = tasks.length > 0;
  text('#empty-title', activeOnly ? '확인된 진행 중 작업이 없어요' : connected ? '표시할 작업이 없어요' : '아직 연결된 작업이 없어요');
  text('#empty-detail', current.browser.data?.hasMore ? '웹의 최근 작업 50개 범위에서 확인했어요' : connected ? '웹의 최근 작업을 최대 50개까지 표시해요' : '로그인하면 웹 작업을 가져올게요');
  $('#connect-card').hidden = current.browser.status === 'connected';
  text('#connect-title', current.browser.status === 'no_tab' ? 'ChatGPT 페이지를 열어 주세요' : 'ChatGPT로 시작하기');
  const copy = $('#connect-copy');
  copy.textContent = errorCopy[current.browser.status] ?? (current.browser.status === 'no_tab' ? '브라우저에서 Codex 페이지를 열어 두면 자동으로 갱신돼요.' : '공식 ChatGPT 페이지에서 로그인해 주세요. 비밀번호와 로그인 토큰은 저장하지 않아요.');
  text('#last-refresh', current.refreshedAt ? `${ago(current.refreshedAt)} 확인` : '연결 대기 중');
}

async function update(refresh = false) {
  if (busy) return;
  busy = true; $('#refresh').disabled = true; $('#refresh').classList.add('spin');
  try {
    const response = await chrome.runtime.sendMessage({ type: refresh ? 'refresh' : 'state' });
    if (!response?.state) throw new Error('no_state');
    current = response.state;
    render();
  } catch { toast('연결 상태를 읽지 못했어요. 확장 프로그램을 다시 열어 주세요.'); }
  finally { busy = false; $('#refresh').disabled = false; $('#refresh').classList.remove('spin'); }
}

function toast(value) { text('#toast', value); $('#toast').hidden = false; }
$('#refresh').addEventListener('click', () => void update(true));
$('#connect').addEventListener('click', () => void chrome.runtime.sendMessage({ type: 'connect' }));
$('#settings').addEventListener('click', () => void chrome.runtime.openOptionsPage());
$('#usage-page').addEventListener('click', () => void chrome.tabs.create({ url: 'https://chatgpt.com/codex/settings/usage' }));
$('#panel').hidden = document.body.classList.contains('panel');
$('#panel').addEventListener('click', async () => {
  try { const window = await chrome.windows.getCurrent(); await chrome.sidePanel.open({ windowId: window.id }); }
  catch { toast('사이드 패널을 열지 못했어요. 일반 Chrome 창에서 다시 시도해 주세요.'); }
});
$('#active-only').addEventListener('click', () => { activeOnly = !activeOnly; $('#active-only').setAttribute('aria-pressed', String(activeOnly)); render(); });
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'session' && changes.state?.newValue) { current = changes.state.newValue; render(); } });
await update();
void update(true);
setInterval(() => { if (!document.hidden) void update(true); }, 15000);
