# Company PC master implementation brief

## 목표

새 회사 PC의 독립 작업공간에서 기존 Discord 봇 저장소를 새로 clone하고 실제 코드/이력/문서를 기준으로 OneKey 음성 상태 API를 추가한다. 이어 별도 Git 저장소에 Windows용 `eslee OneKey` MVP를 구현한다.

## 절대 조건

- 이전 로컬 작업공간이나 개인 PC 경로에 의존하지 않는다.
- Discord 봇과 OneKey는 독립 저장소/기능 브랜치로 관리한다.
- 실제 토큰, Discord 사용자/서버/채널 ID, 개인 경로를 코드·문서·로그·테스트에 기록하지 않는다.
- 운영 값은 환경 변수, Windows DPAPI, 자리표시자와 가짜 테스트 데이터로만 다룬다.
- 회사 PC에서 장치나 보안 정책을 임의로 변경하지 않는다.

## Discord 봇 산출물

- `GET /health`
- Bearer 인증 `GET /api/voice-status`
- Discord 준비 전 503, 인증 실패 401
- REST 호출 없이 캐시된 guild voice state 사용
- Northflank `PORT`, 같은 프로세스, 정상 종료
- 테스트, 배포/보안/수동 시험 문서

## OneKey 산출물

- WPF + 현재 .NET LTS
- 전역 단축키/프로세스 트리거
- 안전한 기본 오디오 전환/복원
- Discord-aware `RestorePending`
- API 실패 시 현재 헤드셋 유지/재시도
- 사용자의 수동 오디오 선택 우선
- DPAPI 토큰, JSON 설정/세션, 트레이, 로그인 시작
- 필수 경계 사례 자동 테스트와 Windows 수동 시험 계획

## 완료 보고

저장소/브랜치/커밋/푸시/PR 상태, 구현/검증 결과, 미완료 운영 설정, 보안 확인, Notion 기록 링크를 구분해 보고한다.

