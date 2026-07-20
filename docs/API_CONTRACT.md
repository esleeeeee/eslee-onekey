# Discord 음성 상태 API 계약

## 호출

```http
GET /api/voice-status HTTP/1.1
Host: <DISCORD_BOT_API_HOST>
Authorization: Bearer <ONEKEY_API_TOKEN>
Accept: application/json
```

OneKey 설정에는 배포 호스트의 HTTPS 기본 URL을 넣습니다. 앱은 기본 URL에 `/api/voice-status`를 붙입니다. 토큰은 UI에서 입력하지만 DPAPI 저장소에만 기록됩니다.

## 응답

성공:

```json
{
  "in_voice": true,
  "guild_id": "<OPTIONAL_GUILD_ID>",
  "channel_id": "<OPTIONAL_CHANNEL_ID>"
}
```

`guild_id`와 `channel_id`는 진단용 선택 필드이며 OneKey 판단에는 사용하지 않습니다.

| HTTP | 의미 | OneKey 동작 |
|---|---|---|
| 200 + `in_voice=true` | 대상 사용자가 음성 채널에 있음 | `RestorePending`, 헤드셋 유지 |
| 200 + `in_voice=false` | 음성 채널에 없음 | 안전 조건 확인 후 복원 |
| 401/403 | 토큰 불일치 또는 권한 거부 | 오류 표시, 헤드셋 유지, 재시도 |
| 503 | Discord 클라이언트 캐시 준비 전 | 헤드셋 유지, 재시도 |
| 기타/타임아웃/잘못된 JSON | 일시적 API 실패 | 헤드셋 유지, 재시도 |

호출 타임아웃은 5초입니다. 응답 본문에 `in_voice` 불리언이 없으면 실패로 처리합니다.

## 봇 환경 변수

Discord 봇 배포 환경에서만 다음 값을 비밀/환경 변수로 설정합니다.

```text
ONEKEY_DISCORD_USER_ID=<TARGET_USER_ID>
ONEKEY_API_TOKEN=<LONG_RANDOM_SECRET>
PORT=<PLATFORM_ASSIGNED_PORT>
```

실제 값은 저장소, 문서, 스크린샷, 로그에 복사하지 않습니다. OneKey와 봇에 같은 토큰을 안전한 경로로 각각 주입합니다.

## 구현 기준

- 봇 저장소 브랜치: `feat/onekey-voice-status-api`
- 봇 API는 같은 프로세스의 `aiohttp` 서버로 실행됩니다.
- Discord REST 조회 없이 `guild.voice_states` 캐시를 사용합니다.
- 대상 사용자와 봇이 공유하는 길드만 검사하며 DM은 대상이 아닙니다.
- `/health`는 인증 없이 프로세스/Discord 준비 상태를 반환합니다.

