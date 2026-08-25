# eslee OneKey

단축키 하나로 게임과 통화 환경을 한 번에 준비하는 Windows 앱입니다.

키를 누르면 게임을 켜고, 헤드셋으로 오디오를 바꾸고, Discord 음성채널에 들어가고,
게임 계정까지 원하는 쪽으로 바꿔 줍니다. 게임을 끄면 오디오를 원래 장치로 되돌리되,
아직 통화 중이면 통화가 끝날 때까지 기다립니다.

## 무엇을 하나요

**자동화 규칙**을 만들어 두면 그 규칙에 지정한 단축키로 아래를 한 번에 실행합니다.

- **프로그램 실행** — 게임이나 런처를 시작하고, 지정한 프로그램이 종료되면 자동화가 끝난 것으로 봅니다.
- **계정 로그인** — 런처에 저장된 로그인 세션을 계정별로 나눠 두었다가 바꿔 넣습니다. 아이디와 비밀번호는 다루지 않습니다.
- **오디오 장치 변경** — 시작할 때 지정한 출력 장치로 바꾸고, 끝나면 그대로 두거나 원래 장치로 되돌립니다.
- **Discord** — 지정한 음성채널에 자동으로 들어갑니다.

규칙은 여러 개 만들 수 있고, 각각 다른 단축키와 다른 계정을 씁니다.
예를 들어 `Ctrl+Alt+Shift+V`는 한국 계정, `Ctrl+Alt+Shift+A`는 아시아 계정으로 둘 수 있습니다.

## 설치

1. [최신 릴리즈](https://github.com/esleeeeee/eslee-onekey/releases/latest)에서
   `eslee-OneKey-Windows-x64-vX.Y.Z-Portable.zip`을 내려받습니다.
2. 원하는 폴더에 압축을 풉니다.
3. `Eslee.OneKey.App.exe`를 실행합니다.

self-contained 게시본이라 .NET을 따로 설치하지 않아도 됩니다.
Windows 10 2004 이상 또는 Windows 11 (x64)이 필요합니다.

## 처음 설정하기

앱을 열면 **자동화** 화면이 나옵니다. 왼쪽에서 자동화를 고르고 오른쪽에서 설정합니다.

1. **기본 설정** — 이름과 단축키를 정합니다.
2. **프로그램** — 실행할 파일과 종료를 지켜볼 프로그램을 지정합니다.
3. **오디오** — 쓸 출력 장치를 고르고, 끝난 뒤 그대로 둘지 되돌릴지 정합니다.
4. **Discord** — 연동을 켜고 `Discord 다시 연결`을 누른 뒤, 서버와 음성채널을 목록에서 고릅니다.
5. **이 자동화 저장**을 누릅니다.

설정 중 뜻이 바로 드러나지 않는 항목 옆에는 `?` 표시가 있습니다. 마우스를 올리면 설명이 나옵니다.

## 계정 전환

계정을 여러 개 쓰려면 자동화를 계정 수만큼 만들고 각각 다른 단축키를 줍니다.

**등록 순서**

1. 원하는 계정으로 게임 런처에 직접 로그인합니다 (로그인 상태 유지 켜기).
2. 그 자동화에서 **현재 로그인 계정 등록**을 누릅니다.
3. **다른 계정 로그인 화면 열기**를 누릅니다. 런처가 닫혔다가 로그인 화면으로 다시 열립니다.
4. 다음 계정으로 로그인합니다.
5. 다른 자동화에서 **현재 로그인 계정 등록**을 누릅니다.

> 런처에서 직접 **로그아웃하지 마세요.** 로그아웃은 서버에 저장된 로그인 정보까지 지워서,
> 이미 등록해 둔 다른 계정의 저장 세션도 무효가 됩니다.

**쓰는 법**

- 단축키를 누르면 그 계정으로 자동화가 시작됩니다.
- 자동화가 이미 돌고 있을 때 다른 계정 단축키를 누르면, **오디오와 통화는 그대로 둔 채 계정만 바뀝니다.**
- 같은 계정 단축키를 다시 누르면 아무 일도 하지 않습니다.
- 게임이 실행 중이면 계정을 바꾸지 않습니다.

저장된 로그인 세션은 Windows DPAPI로 암호화해 현재 사용자만 풀 수 있게 보관합니다.
설정 파일이나 로그에는 남지 않습니다.

## 종료 후 오디오

자동화 종료 후 동작을 **현재 장치 유지** 또는 **실행 전 장치로 복원** 중에 고릅니다.

복원을 고르면 **Discord 통화가 끝날 때까지 복원 기다리기**를 함께 켤 수 있습니다.
게임을 껐는데 아직 통화 중이면 헤드셋을 그대로 두었다가, 통화가 끝나면 자동으로 되돌립니다.
Discord를 아예 종료한 경우에도 통화 중이 아니라고 보고 되돌립니다.

통화 상태는 이 PC의 Discord에 직접 물어봅니다. 외부 서버에 사용자 정보를 넘기지 않습니다.

## Discord 서버와 채널 목록

서버 목록에는 **내가 가입한 서버 중 OneKey 봇도 함께 있는 서버만** 나옵니다.
내 서버 목록은 이 PC의 Discord가 알려 주고, 봇은 그중 자기도 속한 것만 확인해 줍니다.
봇은 자기 서버 목록을 알려 주지 않으므로, 내가 모르던 서버가 드러나지 않습니다.

음성채널 목록에는 내가 볼 수 있는 채널만 나옵니다.

> Discord 연결이 거부되면 이 계정이 아직 OneKey 테스트 사용자로 등록되지 않은 것입니다.
> 앱 관리자에게 등록을 요청하세요.

## 트레이

앱을 닫아도 트레이에 남아 단축키가 계속 동작합니다.
트레이 메뉴에서 열기, 일시정지, 현재 상태 확인, 종료를 할 수 있습니다.

같은 PC에 **Tray Folder** 호스트가 떠 있으면 OneKey는 자체 트레이 아이콘을 숨기고
호스트의 트레이에 묶여 동작합니다(Hosted 모드). 호스트가 없으면 자기 아이콘을 다시 띄웁니다.

## 안전하게 동작하는 방식

- 자동화 시작 직전의 기본 출력 장치를 기억해 두었다가 되돌립니다.
- 앱이 바꾼 장치가 아직 기본값일 때만 되돌립니다. 사용자가 도중에 직접 바꿨으면 그 선택을 우선합니다.
- 통화 상태를 확인할 수 없으면 헤드셋을 유지하고 화면에 이유를 표시합니다.
- 비정상 종료된 세션을 발견해도 시작할 때 오디오를 마음대로 바꾸지 않습니다. 사용자가 확인한 뒤 고릅니다.
- 계정 전환은 런처가 이미 보관하는 로그인 세션 파일만 다룹니다. 게임 설치 파일이나 보호 드라이버는 건드리지 않습니다.

## 개발

```powershell
dotnet restore eslee-onekey.slnx --configfile NuGet.Config
dotnet build eslee-onekey.slnx -c Release --no-restore
dotnet test tests/Eslee.OneKey.Tests/Eslee.OneKey.Tests.csproj -c Release --no-build
dotnet publish src/Eslee.OneKey.App/Eslee.OneKey.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -o artifacts/publish/win-x64 --configfile NuGet.Config
```

실행 파일은 `artifacts/publish/win-x64/Eslee.OneKey.App.exe`에 생성됩니다. `artifacts/`는 Git에서 제외됩니다.

### 저장소 구조

- `src/Eslee.OneKey.Core` — 자동화 상태 머신과 플랫폼 독립 계약
- `src/Eslee.OneKey.Infrastructure.Windows` — Core Audio, 프로세스, 전역 단축키, DPAPI, Discord RPC, 설정 저장소
- `src/Eslee.OneKey.App` — WPF 화면과 트레이
- `tests/Eslee.OneKey.Tests` — 상태 머신, 계정 전환, Discord, 설정 마이그레이션 테스트
- `docs` — 요구사항, 설계, 결정, API 계약, 수동 시험 계획

## 알려진 제약

- Windows 기본 오디오 장치 변경은 공개 API가 없어 `PolicyConfig` COM 인터페이스를 씁니다.
- Discord 음성채널 목록은 "볼 수 있는 채널"까지만 알려 줍니다. 입장 권한은 알려 주지 않으므로, 권한이 없는 채널은 입장 시점에 거부됩니다.
- 서버 목록 기능을 쓰려면 OneKey 봇이 그 서버에 들어가 있어야 합니다.

---

## English summary

**eslee OneKey** prepares your game and voice setup with a single hotkey on Windows.

Each automation rule can launch a program, switch the default audio output, join a Discord
voice channel, and swap the launcher's saved sign-in session so a chosen game account is used —
no passwords involved. Different rules use different hotkeys and accounts, so pressing another
rule's hotkey while one is running switches only the account, leaving audio and the ongoing call
untouched.

When the watched program exits, OneKey either keeps the current device or restores the previous
one. Restoration waits while you are still in a Discord call and happens automatically once the
call ends. Call state is read from the local Discord client, so no user identity is sent anywhere.

Saved sign-in sessions and secrets are encrypted with per-user Windows DPAPI and never written to
settings files or logs. Download the latest portable build from
[Releases](https://github.com/esleeeeee/eslee-onekey/releases/latest).
