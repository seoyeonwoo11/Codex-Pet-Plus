# Codex Pet Plus · Windows

**Codex 앱의 기본 펫 아래에 남은 한도를 표시하는 Windows 프로그램**입니다. 펫의 위치를 따라가는 작은 표시줄을 띄웁니다. Codex 설치 파일을 수정하는 방식이 아니라 별도 표시창을 연결합니다.

## 실행 파일 받기

1. [Windows 빌드 페이지](https://github.com/seoyeonwoo11/Codex-Pet-Plus/actions/workflows/windows-desktop.yml)를 엽니다.
2. 최신 main 실행에서 빌드가 성공했는지 확인합니다.
3. 실행 결과 아래 **Artifacts → CodexPetPlus-Windows-x64**를 다운로드합니다. GitHub 로그인이 필요합니다.
4. ZIP의 압축을 풀고 **CodexPetPlus.exe**를 실행합니다.

실행 파일에는 .NET 런타임이 포함됩니다. 사용자 PC에 Node.js, Python, .NET SDK를 설치할 필요가 없습니다. 대상은 **Windows 10/11 x64**입니다.

## 펫 아래에 표시하기

1. Codex 앱에서 기본 펫을 켭니다.
2. Codex Pet Plus 설정 창의 **펫 선택 (3초 후)**을 누릅니다.
3. 마우스를 **기본 펫의 몸 위**에 올려둡니다. 3초 뒤 해당 창을 연결합니다.
4. 펫 아래에 **기본 한도와 주간/추가 한도의 남은 비율**이 표시됩니다.

펫을 옮기면 표시줄도 따라갑니다. 아래 공간이 부족하면 위에 표시하고, 펫이 숨겨지면 표시줄도 숨깁니다. 표시줄은 키보드 포커스를 가져오지 않습니다. 표시줄 우클릭 또는 작업 표시줄 트레이의 **Codex Pet Plus**에서 설정·새로고침·종료를 선택할 수 있습니다.

Codex 프로세스의 독립된 작은 펫 창을 연결합니다. 이름으로 펫을 명확히 식별할 수 있으면 자동 연결하고, 여러 창이 있으면 사용자가 선택하도록 합니다. Codex가 펫을 본창 내부에만 그리거나 별도 창 경계를 제공하지 않는 버전은 자동 위치 추적을 지원하지 않습니다.

## 한도의 출처

- **앱 연결**: Codex 설치 폴더에서 찾은 로컬 연결 도구로 **account/rateLimits/read**를 조회합니다. 초기화와 한도 읽기만 요청하며, 작업을 만들거나 실행 중인 작업을 변경하지 않습니다. 기본 갱신 간격은 60초입니다.
- **최근 기록**: 로컬 연결이 안 되면 선택한 Codex 폴더의 최근 JSONL 기록을 읽습니다. 실제 기록 시각을 표시하고, 현재 계정의 실시간 한도와 구분합니다. 설정에서 끌 수 있습니다.
- **—**: 값이 없거나 해당 초기화 시각이 지나 다시 조회해야 하는 상태입니다. 모르는 한도를 0%나 100%로 표시하지 않습니다.

로컬 연결 도구가 사용하는 로그인 기준입니다. 앱과 별도의 Codex 홈/프로필을 사용하면 계정이 다를 수 있습니다. 최근 기록 역시 다른 계정에서 생성한 값일 수 있으므로 출처와 기록 시각을 확인해야 합니다.

보통 Codex 설치 폴더의 **resources/codex.exe**, **resources/bin/codex.exe** 또는 하위 번들에서 연결 도구를 찾습니다. 자동 검색이 안 되면 설정에서 직접 선택하세요. Codex 본창을 실행하는 파일은 연결 도구와 구분됩니다. 선택한 도구는 **--version** 결과가 codex-cli일 때만 연결합니다.

기록 폴더 기본값은 기존 CODEX_HOME 경로, 없으면 사용자 폴더의 **.codex**입니다. 최근 수정 파일 12개의 마지막 512 KiB 범위에서 한도 이벤트만 추출하고, 24시간보다 오래된 값은 표시하지 않습니다. 인증 파일이나 소스 코드 파일은 열지 않으며, JSONL 안의 프롬프트·모델 추론·도구 출력은 표시 데이터로 추출하지 않습니다.

## 소스에서 빌드

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)를 설치하고 저장소 폴더에서 PowerShell로 실행합니다.

~~~powershell
./desktop/Build-Windows.ps1
~~~

결과는 **dist/windows/CodexPetPlus.exe**와 **dist/CodexPetPlus-Windows-x64.zip**입니다. SDK 버전은 global.json에 고정되어 있습니다. WPF 창이 프로세스 DPI를 관리하며, WinForms는 트레이 아이콘에만 사용합니다.

다른 OS에서 교차 빌드할 때는 Node.js 개발 의존성을 설치한 뒤 사용할 수 있습니다.

~~~sh
npm ci
npm run package
~~~

DOTNET이 PATH에 없으면 **PET_DOTNET_PATH**에 실행 파일 경로를 지정할 수 있습니다. 이 변수는 빌드에만 사용합니다.

## 검증

~~~sh
dotnet run --project desktop/CodexPetPlus.Tests/CodexPetPlus.Tests.csproj
dotnet build desktop/CodexPetPlus.Windows/CodexPetPlus.Windows.csproj
~~~

코어 테스트는 실제 자식 프로세스/표준 입출력 JSON-RPC, 초기화 순서, 서버 작업 요청 거절, 타임아웃, 뒤바뀐 응답 순서, 로컬 JSONL 기록, 초기화 시각, 다중 모니터와 DPI 위치 계산을 검증합니다. Windows WPF 대상으로 교차 빌드할 수 있습니다.

이 클라우드 머신은 Linux이므로 **실제 Windows Codex 펫에 연결한 화면과 사용자 계정의 실시간 한도 조회는 검증하지 못했습니다**. GitHub Windows 빌드도 별도 실행 결과로 확인해야 합니다. 프로그램을 실행한 뒤 펫 선택과 표시 위치를 실제 PC에서 확인해 주세요.

이전 Chrome 확장 소스와 사용법은 [extension/README.md](extension/README.md)에 보관되어 있습니다. 해당 버전의 패키지는 **npm run package:extension**으로 생성합니다.

## 개인정보

설정은 현재 사용자의 **LocalAppData/CodexPetPlus/settings.json**에 저장합니다. 비밀번호나 토큰을 직접 읽거나 저장하지 않고, 별도의 서버나 분석 도구를 사용하지 않습니다. 자세한 범위는 [PRIVACY.md](PRIVACY.md)를 참고하세요. OpenAI의 공식 제품은 아닙니다.
