# Codex Pet Plus · Windows 앱 모드

**기본 펫과 커스텀 펫 아래에 남은 Codex 한도를 표시하는 비공식 앱 모드**입니다. 사용자 폴더에 앱 복사본을 만들고, 그 앱 안에서 펫 화면을 찾아 작은 한도 창을 연결합니다. 펫을 바꾸거나 움직이거나 다시 켜도 자동으로 연결합니다.

이 설치기는 요청하신 **앱 버전 26.1023.51421**, Windows 10/11 x64를 대상으로 합니다. 다른 버전이나 지원하지 않는 패키지 구조이면 설치를 중단합니다. 실제 해당 앱 패키지가 이 클라우드 환경에 없어, 사용자 PC에서 최종 호환성을 확인해야 하는 초기 버전입니다.

## 다운로드와 설치

1. 일반 크롬에서 GitHub에 로그인한 뒤 [Windows 빌드 페이지](https://github.com/seoyeonwoo11/Codex-Pet-Plus/actions/workflows/windows-desktop.yml)를 엽니다.
2. 최신 성공한 main 실행을 열고, 아래 **Artifacts → CodexPetPlus-Windows-x64**를 다운로드합니다.
3. ZIP을 풀고 **CodexPetPlus.exe**를 실행합니다. .NET, Node.js, Python을 별도로 설치할 필요가 없습니다.
4. 자동 검색된 앱 실행 파일을 확인하고 **모드 설치 / 다시 설치**를 누릅니다. 못 찾으면 설치된 앱 폴더의 **ChatGPT.exe** 또는 **Codex.exe**를 선택합니다.
5. 진행 중인 작업을 마친 뒤 앱을 트레이에서도 완전히 종료하고 **모드 앱 실행**을 누릅니다.
6. 앱에서 기본 펫이나 원하는 커스텀 펫을 켭니다. 해당 펫 아래에 한도가 자동으로 붙습니다.

이후에는 **시작 메뉴 → Codex Pet Plus**로 모드 앱을 열 수 있습니다. 설치기는 종료해도 됩니다. 설치기의 **상태 확인**에서 모듈 실행, 펫 연결 개수, 한도 조회 성공 여부를 확인할 수 있습니다.

모드는 복사본의 앱 시작 모듈에 로더를 추가하고, 복사본 실행 파일의 ASAR 헤더 해시를 갱신합니다. 공식 설치 폴더는 읽기만 사용합니다. 복사본은 LocalAppData/CodexPetPlus/mod 아래에 저장되므로 앱 한 벌을 복사할 디스크 공간이 필요합니다. 복사본 실행 파일의 서명은 달라지며 Windows 정책에 따라 실행되지 않을 수 있습니다. 보안 정책을 해제하는 절차는 제공하지 않습니다.

앱 업데이트 후에는 새 버전 구조에 맞는 모드가 필요합니다. 이 버전의 모드는 26.1023.51421에만 설치합니다. 공식 앱으로 돌아가려면 모드 앱을 종료하고 기존 공식 앱 바로가기를 실행하세요. 제거할 때는 모드 앱 종료 후 LocalAppData/CodexPetPlus/mod와 시작 메뉴의 Codex Pet Plus 바로가기를 삭제하면 됩니다.

## 표시되는 값

표시값은 **로그인한 계정의 Codex 사용 한도**입니다. 기본/추가 기간의 남은 비율과 막대를 표시합니다. 아래 공간이 부족하면 펫 위에 표시하고, 펫이 숨겨지면 표시줄도 숨깁니다. 키보드 포커스와 마우스 입력을 가져오지 않습니다.

번들에 포함된 Codex 연결 도구로 **account/rateLimits/read**를 60초마다 조회합니다. 초기화와 한도 읽기만 요청하며, 작업을 시작하거나 인증 파일을 직접 읽지 않습니다. 이 도구의 기존 로컬 로그인 기준이므로 앱과 Codex 홈/프로필이 다르면 계정이 다를 수 있습니다. 조회 실패, 값 누락, 초기화 시각 경과 시에는 **—**를 표시합니다.

같은 앱의 펫 화면을 사용하는 커스텀 외형에 적용됩니다. 외부에 따로 설치한 다른 펫 프로그램이나 지원하지 않는 앱 화면 구조에는 적용되지 않습니다. 커스텀 펫의 설치·선택은 앱의 기존 펫 기능을 사용하세요.

## 별도 표시줄 사용

설치기의 **별도 표시줄로 연결하기**에서 해당 옵션을 켜고 **설정 저장 · 새로고침**을 누르면 별도 Windows 프로그램 방식도 사용할 수 있습니다. Codex.exe와 ChatGPT.exe의 작은 펫 창을 이름·테두리·투명도·창 스타일로 찾고, 모호하면 직접 선택을 제공합니다. 모드와 동시에 켜면 표시줄이 중복됩니다.

이 방식에서만 최근 JSONL 기록 사용 옵션을 제공합니다. 최근 기록은 현재 계정과 다를 수 있으므로 출처·시각을 표시하고 끌 수 있습니다.

## 개발과 검증

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)를 설치하고 PowerShell에서 실행합니다.

~~~powershell
./desktop/Build-Windows.ps1
~~~

결과는 **dist/windows/CodexPetPlus.exe**와 **dist/CodexPetPlus-Windows-x64.zip**입니다. SDK는 global.json에 고정되어 있습니다.

~~~sh
npm run test:mod
dotnet run --project desktop/CodexPetPlus.Tests/CodexPetPlus.Tests.csproj -c Release
~~~

코어 테스트 25개, 모드 런타임 테스트 8개를 제공합니다. 공식 ASAR 도구가 만든 합성 앱에 패치를 적용하고 실제 Electron에서 자동 연결·이동·숨김·커스텀 펫 재연결·알 수 없는 한도 표시도 검증합니다. 합성 테스트에는 ChatGPT/Codex 소스나 계정이 들어 있지 않습니다. Windows CI는 빌드와 이 테스트를 실행한 뒤 ZIP 파일을 제공합니다.

실제 Electron 테스트를 로컬에서 재현하려면 Node.js 24와 임시 도구 폴더를 사용합니다.

~~~powershell
$env:PET_MOD_TOOLS = Join-Path $env:TEMP 'pet-plus-mod-tools'
npm install --prefix $env:PET_MOD_TOOLS @electron/asar@4.3.1 electron@45.1.0
node tests/run-mod-electron.mjs
~~~

먼저 Release 코어 테스트를 빌드해야 합니다. Linux에서는 가상 화면이 필요합니다. **PET_DOTNET_PATH**로 dotnet 실행 경로를 지정할 수 있습니다.

실제 26.1023.51421 앱의 펫 화면과 사용자 계정 한도 조회는 아직 검증하지 못했습니다. 설치 실패 시 오류 메시지, 실행 후 표시가 없으면 **상태 확인** 결과로 호환성을 확인해 주세요.

이전 Chrome 확장 사용법은 [extension/README.md](extension/README.md)에 있습니다. 개인정보 처리 범위는 [PRIVACY.md](PRIVACY.md)를 참고하세요. OpenAI의 공식 제품은 아닙니다.
