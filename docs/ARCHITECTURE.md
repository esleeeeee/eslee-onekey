# 아키텍처

## 구성

```text
WPF UI / Tray
      |
AutomationCoordinator ---- Global Hotkey / Process Monitor
      |
AutomationEngine (serialized state machine)
      |---- AudioEndpointService ---- Windows Core Audio / PolicyConfig
      |---- ProcessService ---------- Process + foreground window
      |---- DiscordVoiceClient ------ HTTPS Bearer API
      |---- SessionStore ------------ atomic JSON session file
      `---- AppLogger --------------- redacted local log

SettingsStore ---- atomic JSON (no token)
SecretStore ------ Windows DPAPI CurrentUser binary
StartupService --- HKCU Run (opt-in)
```

Core 프로젝트는 Windows API를 알지 못하며 인터페이스만 사용합니다. Windows 어댑터는 Infrastructure 프로젝트에 격리되고, WPF 프로젝트는 수명주기와 사용자 입력을 연결합니다.

## 상태 전이

```text
Idle/Completed/Failed
        |
        | hotkey or process start
        v
     Starting -- failure --> Failed
        |
        v
      Active
        |
        | game exits
        v
  voice/API check
     |       |
 not voice  voice/error
     |       v
     |  RestorePending -- retry --> RestorePending
     |       |
     |       | later not in voice
     v       v
    Restoring
        |
        v
    Completed
```

`Starting`, `Active`, `RestorePending`, `Restoring`은 busy 상태입니다. 이 동안 같은 자동화에 들어오는 단축키/프로세스 트리거는 무시됩니다.

## 시작 순서

1. 현재 기본 출력 ID를 읽고 세션에 기억합니다.
2. 대상 출력이 활성 상태인지 확인합니다.
3. 대상을 통신/멀티미디어/콘솔 기본 출력으로 지정합니다.
4. 세션을 디스크에 원자적으로 저장합니다.
5. 단축키 시작이고 게임이 없으면 게임을 실행합니다.
6. Discord 연동이 켜져 있고 Discord가 실행 중이 아니면 설정된 실행 파일을 실행합니다. 이미 실행 중이면 창을 전면으로 가져오지 않고 그대로 둡니다.

## 복원 안전성

종료 후 오디오 자동 복원은 `restoreAudioOnExit` 옵션이 켜져 있을 때만 동작합니다. 기본값은 현재 장치 유지이며, 이때는 감시 프로세스가 종료해도 오디오를 변경하지 않고 Discord 통화 확인이나 `RestorePending` 상태로도 들어가지 않습니다. 사용자가 트레이/창에서 수동 복원을 선택하는 경로는 그대로 남습니다.

옵션이 켜진 경우, 게임 종료 시 API가 `in_voice=false`이면 현재 기본 출력 ID를 다시 읽습니다. 현재 값이 OneKey가 전환했던 대상 ID와 일치할 때만 원래 ID로 복원합니다. 다르면 사용자가 변경한 것으로 보고 세션만 종료합니다.

`in_voice=true`, 인증 실패, 준비되지 않음, 네트워크/JSON 오류에서는 자동 복원하지 않습니다. `RestorePending`에서 5초부터 최대 60초까지 지수 백오프로 다시 확인합니다. 성공 응답이 올 때까지 현재 헤드셋을 유지합니다.

## 충돌과 복구

- 전역 단축키 등록 실패는 상태에 표시하지만 프로세스 감시는 계속됩니다.
- 비정상 종료 뒤 busy 세션을 발견하면 `Failed`로 표시하고 어떤 오디오 변경도 자동 수행하지 않습니다.
- 세션/설정은 임시 파일에 쓴 뒤 같은 볼륨에서 교체해 중간 파일 손상을 줄입니다.
- 앱 종료은 코디네이터의 폴링과 전역 단축키를 먼저 해제합니다.

## 배포 경계

OneKey와 Discord 봇은 독립 저장소/프로세스입니다. OneKey는 공개 인터넷에 직접 Discord 자격증명을 사용하지 않고, 별도 봇의 최소 계약만 Bearer 토큰으로 호출합니다. 봇은 캐시된 Discord voice state만 읽습니다.

