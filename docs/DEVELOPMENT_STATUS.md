# 개발 상태

기준 브랜치: `feat/valorant-automation-mvp`

## 완료

- .NET 10 LTS WPF 솔루션 및 계층 분리
- 전역 단축키와 게임 프로세스 감지
- Core Audio 출력 장치 열거/기본 장치 전환
- 게임/Discord 실행 및 중복 방지
- Discord voice-status Bearer API 클라이언트
- `RestorePending`과 지수 백오프 재확인
- 수동 오디오 변경 보호, 수동 복원, 현재 장치 유지
- 트레이 메뉴와 Windows 로그인 시작 옵션
- JSON 설정/세션, DPAPI 토큰, 마스킹 로그
- 중복 실행 방지와 보수적 비정상 종료 복구
- 12개 필수 시나리오를 포함한 22개 자동 테스트
- `win-x64` self-contained Release 게시와 앱 프로세스/UI 기동 스모크 테스트

## 검증 결과

```text
dotnet build eslee-onekey.slnx -c Debug --no-restore
경고 0, 오류 0

dotnet test tests/Eslee.OneKey.Tests/Eslee.OneKey.Tests.csproj -c Debug --no-build
총 22, 통과 22, 실패 0

dotnet publish ... -c Release -r win-x64 --self-contained true
성공

게시 앱 스모크 실행
프로세스 생존/응답/주 창 생성 확인
```

스모크 실행은 회사 PC의 기본 오디오를 실제 변경하지 않았고, 시작프로그램도 등록하지 않았습니다.

## 운영 전 남은 수동 작업

- 저장소가 비공개인지 GitHub 설정에서 확인/변경
- Discord 봇 배포 환경에 사용자 ID/토큰/포트 비밀값 주입
- OneKey UI에 실제 게임/Discord 실행 파일과 활성 헤드셋 선택
- Windows 10/11 실장비에서 실제 오디오 전환/복원 시험
- 장시간 Discord 통화, 네트워크 단절/복구, 재부팅 시험
- 배포 전 코드 서명과 설치 관리자 정책 결정
- 기능 브랜치의 Draft PR 생성 및 리뷰

## 알려진 제약

- 봇 저장소의 기능 브랜치는 푸시되었지만 현재 자동화 환경의 GitHub 앱 쓰기 권한 제한으로 Draft PR을 만들지 못할 수 있습니다.
- 새 OneKey 원격 저장소가 공개 상태라면 운영 값 입력 전에 반드시 비공개로 바꿔야 합니다.
- 실제 비밀값과 회사 PC 고유 경로는 의도적으로 기록하지 않았습니다.

