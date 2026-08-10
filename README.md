# eslee OneKey

`eslee OneKey`는 Windows에서 단축키 또는 게임 프로세스 시작을 감지해 오디오 출력 장치를 전환하고, 게임 종료 후 안전한 시점에 원래 장치로 복원하는 데스크톱 MVP입니다. Discord 음성 채널 사용 중에는 복원을 보류하며, 사용자가 세션 도중 직접 오디오 장치를 바꾸면 그 선택을 우선합니다.

## MVP 기능

- 시스템 전역 단축키와 게임 프로세스 시작 감지
- 현재 기본 출력 장치 저장 후 지정 헤드셋으로 전환
- 게임과 Discord 중복 실행 방지, 실행 중 Discord 창 활성화
- 게임 종료 시 Discord 음성 상태 API 확인
- 통화 중 또는 API 장애 시 `RestorePending` 유지 및 지수 백오프 재확인
- 사용자의 수동 오디오 변경을 감지해 강제 복원 취소
- 트레이 실행, 일시정지, 수동 복원, 현재 장치 유지
- 설정 JSON과 세션 파일의 원자적 저장
- Discord API 토큰의 Windows DPAPI(CurrentUser) 암호화 저장
- 선택형 Windows 로그인 시작(HKCU Run)

## 요구 환경

- Windows 10 2004 이상 또는 Windows 11, x64
- 개발: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 운영: `win-x64` self-contained 게시물을 사용하면 별도 .NET 설치 불필요

.NET 10은 현재 LTS이므로 신규 Windows MVP의 기준으로 선택했습니다.

## 개발 명령

```powershell
dotnet restore eslee-onekey.slnx --configfile NuGet.Config
dotnet build eslee-onekey.slnx -c Debug --no-restore
dotnet test tests/Eslee.OneKey.Tests/Eslee.OneKey.Tests.csproj -c Debug --no-build
dotnet publish src/Eslee.OneKey.App/Eslee.OneKey.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -o artifacts/publish/win-x64 --configfile NuGet.Config
```

실행 파일은 `artifacts/publish/win-x64/Eslee.OneKey.App.exe`에 생성됩니다. `artifacts/`는 Git에서 제외됩니다.

## 최초 설정

1. 앱을 열어 `설정` 탭으로 이동합니다.
2. 게임 실행 파일/프로세스명과 Discord 실행 파일/프로세스명을 지정합니다.
3. 전환할 활성 오디오 출력 장치를 선택합니다.
4. Discord 봇 API 기본 URL과 토큰을 입력합니다.
5. 필요하면 전역 단축키와 Windows 로그인 시 시작을 변경합니다.
6. `Discord API 연결 확인` 후 `설정 저장`을 누릅니다.

토큰은 설정 JSON에 기록되지 않습니다. `%LOCALAPPDATA%\eslee OneKey\secrets\discord-api-token.bin`에 현재 Windows 사용자만 복호화할 수 있는 DPAPI 데이터로 저장됩니다. 토큰, Discord 사용자 ID, 개인 PC 경로를 저장소나 로그에 넣지 마십시오.

## 안전 규칙

- 앱은 자동화 시작 직전의 기본 출력 장치를 세션에 보관합니다.
- 앱이 전환한 헤드셋이 여전히 기본 장치일 때만 자동 복원합니다.
- 사용자가 다른 장치를 선택하면 자동 복원을 취소합니다.
- Discord API가 실패하거나 인증에 실패하면 헤드셋을 유지하고 상태/로그에 오류를 표시합니다.
- 비정상 종료 세션을 발견해도 시작 시 오디오를 자동 변경하지 않습니다. 사용자가 상태를 확인한 뒤 수동 복원 또는 현재 장치 유지를 선택해야 합니다.

## 저장소 구조

- `src/Eslee.OneKey.Core`: 상태 머신과 플랫폼 독립 계약
- `src/Eslee.OneKey.Infrastructure.Windows`: Core Audio, 프로세스, 전역 단축키, DPAPI, HTTP, 저장소
- `src/Eslee.OneKey.App`: WPF UI와 트레이 통합
- `tests/Eslee.OneKey.Tests`: 상태 머신 및 보안/HTTP 어댑터 테스트
- `docs`: 요구사항, 설계, 결정, API 계약, 수동 시험 계획

## 현재 제약

- Windows 기본 오디오 변경은 Windows가 공개 API를 제공하지 않는 영역이라 `PolicyConfig` COM 인터페이스를 사용합니다. 주요 Windows 10/11 버전에서 수동 검증이 필요합니다.
- MVP는 자동화 항목 하나를 UI에서 편집합니다. 저장 형식은 향후 다중 자동화를 받을 수 있도록 배열입니다.
- Discord 봇 API 배포, 비밀값/사용자 ID 주입, 방화벽과 네트워크 정책은 운영 환경에서 별도로 설정해야 합니다.

상세 내용은 [아키텍처](docs/ARCHITECTURE.md), [API 계약](docs/API_CONTRACT.md), [수동 시험 계획](docs/MANUAL_TEST_PLAN.md)을 참고하십시오.

---

## English summary

eslee OneKey is a Windows WPF MVP that switches the default audio output when a configured game starts and restores the previous endpoint after the game exits. Restoration is deferred while the configured Discord user is in voice. API failures keep the headset active and retry; a manual audio change always wins. Settings are stored locally, while the API token is protected with per-user Windows DPAPI and is never written to JSON or logs.

