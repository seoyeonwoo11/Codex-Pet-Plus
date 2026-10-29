# Codex Pet Plus

Codex의 **남은 사용량·웹 프로젝트·진행 중인 웹 작업**을 보여주는 Chrome 확장 프로그램입니다. 공식 ChatGPT 브라우저 로그인으로 시작하며 별도 Windows 프로그램이 필요하지 않습니다.

## 설치하고 시작하기

1. 프로젝트 또는 **dist/codex-pet-plus.zip**의 압축을 풉니다.
2. Chrome 120 이상에서 **chrome://extensions**를 열고 **개발자 모드**를 켭니다.
3. **압축해제된 확장 프로그램을 로드합니다**를 누르고 **extension 폴더**를 선택합니다.
4. 툴바의 Codex Pet Plus에서 **ChatGPT 로그인 · 연결**을 누릅니다.
5. 열린 **공식 ChatGPT 페이지에서 로그인**하고, Codex 페이지를 한 탭에 열어 둡니다. 이미 로그인했다면 해당 세션을 사용합니다.

확장은 빌드나 npm 설치 없이 로드할 수 있습니다. 팝업과 사이드 패널을 지원하며, 화면이 열려 있으면 15초마다, 백그라운드에서는 약 1분마다 갱신합니다. Chrome이 절전 상태일 때는 갱신이 지연될 수 있습니다. 확장 프로그램 설정에서 연결을 해제할 수 있습니다.

## 보여주는 정보와 범위

- Chrome 로그인 계정의 Codex 기본/추가 한도 잔여 비율과 초기화 시각. 토큰/문맥 사용량과 계정 한도를 혼동하지 않습니다.
- 실행 중이거나 최근 활동한 **웹 작업 프로젝트**. 응답에 프로젝트가 없으면 이름을 만들어 내지 않습니다.
- Codex **웹**의 최근 작업 최대 50개와 실행·대기·완료·실패·중단 상태. 작업 제목을 누르면 공식 Codex 작업 페이지로 이동합니다.
- 로그인 만료, 접근 거절, 요청 제한, API 형식 변경을 구분합니다. 실패를 잔여량 0%로 표시하지 않습니다. 초기화 시각이 지났으면 새 값을 받기 전까지 비율을 표시하지 않습니다.
- 2분 이상 갱신하지 못한 작업은 실행 중으로 세지 않습니다. 가져온 데이터는 메모리에서만 유지하며, 연결 해제와 브라우저 재시작 시 삭제합니다.

**데스크톱 앱의 로컬 실행 프로젝트와 작업은 웹 작업 목록만으로 정확히 확인할 수 없습니다.** 이 버전에는 로컬 프로그램 설치나 Native Messaging이 없으며, 데스크톱 전용 상태를 확인했다고 표시하지 않습니다. Chrome 계정과 데스크톱 앱 계정이 다를 수도 있습니다.

### 브라우저 연결 방식

공식 ChatGPT 페이지의 세션을 페이지 문맥에서 일시적으로 사용해 **GET /backend-api/wham/usage**와 **GET /backend-api/wham/tasks/list**를 요청합니다. OpenAI Codex 오픈소스 클라이언트가 사용하는 응답 형식을 참고했습니다. 토큰·전체 계정 객체를 확장으로 반환하지 않고, 필요한 상태 필드만 반환합니다.

이는 제3자 앱을 위한 안정적인 공개 OAuth/상태 API가 아닙니다. 내부 응답, 인증 정책, 계정 종류에 따라 변경되거나 동작하지 않을 수 있습니다. 접근 거절과 추가 인증을 우회하지 않습니다. 실패하면 공식 Codex 사용량 페이지에서 직접 확인할 수 있습니다.

## 개발 / 검증

아래 명령은 저장소를 복제한 개발 폴더에서 실행합니다. Node.js 20 이상이 필요합니다. 런타임 의존성은 없고 npm 의존성은 테스트/배포 ZIP 생성에만 사용합니다.

~~~sh
npm ci
npm run check
npm test
npm run test:browser
npm run package
~~~

브라우저 테스트는 **CHROMIUM_PATH**가 지정되면 해당 실행 파일, Linux의 **/usr/bin/chromium**이 있으면 그것을, 아니면 Playwright Chromium을 사용합니다. 브라우저가 없으면 **npx playwright install chromium**으로 설치하세요.

기본 브라우저 테스트는 실제 Chromium에서 제품 화면과 원본 백그라운드 모듈을 실행하며 Chrome API 어댑터와 ChatGPT 응답 fixture를 사용합니다. 이 클라우드 머신은 관리 정책으로 새 확장 설치를 차단하므로 정책을 변경하지 않고 이 방식으로 검증했습니다. 테스트 데이터는 제품 화면의 기본 데이터가 아닙니다.

확장 설치를 허용하는 별도 개발 환경에서는 **PET_TEST_REAL_EXTENSION=1** 환경 변수를 설정해 같은 테스트를 실제 확장으로 실행할 수 있습니다. Playwright의 Chromium을 권장합니다. 실제 계정 로그인, 권한 승인, 사이트 접근 정책은 실제 Chrome에서 별도로 확인해야 합니다.

~~~text
extension/               Chrome Manifest V3 확장
extension/lib/           브라우저 연결과 데이터 정규화
scripts/                 문법 검사, 아이콘, 배포 압축 생성
tests/                  데이터 처리와 Chromium UI/백그라운드 통합 테스트
~~~

## 개인정보

로그인은 **chatgpt.com**에서 처리합니다. 토큰·쿠키·비밀번호를 저장하지 않습니다. 작업 제목과 사용량은 **chrome.storage.session** 메모리에만, 연결 설정은 **chrome.storage.local**에 저장합니다. 별도 서버·분석 도구·원격 코드는 없습니다. 자세한 범위는 [PRIVACY.md](PRIVACY.md)를 참고하세요.

프로토콜 참고: [OpenAI Codex backend client](https://github.com/openai/codex/tree/main/codex-rs/backend-client), [Codex cloud tasks client](https://github.com/openai/codex/tree/main/codex-rs/cloud-tasks-client).
