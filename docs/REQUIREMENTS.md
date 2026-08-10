# 요구사항과 추적성

## 기능 요구사항

| ID | 요구사항 | 구현 위치 | 검증 |
|---|---|---|---|
| FR-01 | 전역 단축키로 자동화 시작 | `WindowsGlobalHotkeyService`, `AutomationCoordinator` | 단축키 충돌 테스트, 수동 시험 MT-01 |
| FR-02 | 게임 프로세스 시작을 감지 | `PollingProcessMonitor` | 프로세스 트리거 중복 테스트, MT-02 |
| FR-03 | 기존 기본 출력 저장 후 지정 장치로 전환 | `AutomationEngine`, `CoreAudioEndpointService` | 상태 머신 테스트, MT-01 |
| FR-04 | 게임/Discord 중복 실행 금지 | `AutomationEngine`, `WindowsProcessService` | 자동 테스트 2건 |
| FR-05 | 게임 종료 시 Discord 음성 상태 확인 | `AutomationEngine`, `DiscordVoiceStatusClient` | HTTP 매핑 및 복원 테스트 |
| FR-06 | 통화 중이면 복원 보류 | `RestorePending` 상태 | 자동 테스트, MT-04 |
| FR-07 | 통화 종료 후 원래 장치 복원 | 복원 폴링 루프 | 자동 테스트, MT-05 |
| FR-08 | API 장애 시 헤드셋 유지 및 재시도 | 지수 백오프(최대 60초) | 자동 테스트, MT-06 |
| FR-09 | 사용자의 수동 장치 변경 우선 | `RestoreIfSafeAsync` | 자동 테스트, MT-07 |
| FR-10 | 트레이/일시정지/수동 복원/현재 유지 | WPF `MainWindow`, `TrayIconService` | MT-08 |
| FR-11 | Windows 로그인 시 선택 실행 | `StartupRegistrationService` | MT-09 |
| FR-12 | 비정상 종료 후 보수적 복구 | 영속 세션 + `RecoverStaleSessionAsync` | 자동 테스트, MT-10 |

## 비기능 요구사항

- Windows 네이티브 UI: WPF, .NET 10 LTS, `net10.0-windows10.0.19041.0`.
- 단일 프로세스/단일 인스턴스. 중복 실행은 안내 후 종료합니다.
- 비밀값은 코드, Git, JSON 설정, 정상 로그에 저장하지 않습니다.
- API 토큰은 DPAPI `CurrentUser`로 보호합니다.
- 상태 전이는 직렬화되어 중복 트리거가 동일 자동화를 재진입시키지 않습니다.
- 오디오 복원은 앱이 관리 중인 장치가 여전히 기본값일 때만 수행합니다.
- 로그에는 토큰 대신 오류 종류만 남기고 `Bearer`/`Authorization` 패턴을 방어적으로 마스킹합니다.

## 범위 밖

- Discord 사용자/서버/채널 관리 UI
- 오디오 장치 드라이버 설치 또는 회사 보안 정책 우회
- 자동 업데이트, 코드 서명, MSI/MSIX 설치 관리자
- 클라우드 동기화와 다중 PC 설정 공유
- 게임별 다중 프로필 UI(저장 모델은 확장 가능)

