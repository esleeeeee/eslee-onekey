using System.Diagnostics.CodeAnalysis;

namespace Eslee.OneKey.Core;

public enum VoiceJoinOutcome
{
    /// <summary>설정에서 꺼져 있어 아무것도 하지 않았습니다.</summary>
    Disabled,
    /// <summary>이미 대상 음성채널에 있어 아무것도 하지 않았습니다.</summary>
    AlreadyInTargetChannel,
    /// <summary>대상 음성채널에 입장했습니다.</summary>
    Joined,
    /// <summary>다른 음성채널에 있어 강제로 옮기지 않았습니다.</summary>
    SkippedBecauseInAnotherChannel,
    /// <summary>대상 채널 설정이 비었거나 형식이 올바르지 않습니다.</summary>
    InvalidTarget,
    /// <summary>Discord 클라이언트에 연결하지 못했습니다(미실행 또는 준비 시간 초과).</summary>
    DiscordUnavailable,
    /// <summary>RPC 인증이 없거나 만료되었습니다. 설정에서 다시 연결해야 합니다.</summary>
    NotAuthorized,
    /// <summary>그 밖의 실패입니다.</summary>
    Failed,
}

public sealed record VoiceJoinResult(VoiceJoinOutcome Outcome, string? Message = null)
{
    public bool IsSuccess =>
        Outcome is VoiceJoinOutcome.Joined
            or VoiceJoinOutcome.AlreadyInTargetChannel
            or VoiceJoinOutcome.Disabled
            or VoiceJoinOutcome.SkippedBecauseInAnotherChannel;
}

/// <summary>
/// Discord 음성채널 링크 또는 Channel ID에서 채널 ID를 뽑아냅니다.
/// 지원 형식: 순수 snowflake, https://discord.com/channels/&lt;guild&gt;/&lt;channel&gt;,
/// discord://-/channels/&lt;guild&gt;/&lt;channel&gt;.
/// </summary>
public static class DiscordChannelTarget
{
    public static bool TryParse(string? value, [NotNullWhen(true)] out string? channelId)
    {
        channelId = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (IsSnowflake(text))
        {
            channelId = text;
            return true;
        }

        var segments = text.Split(['/', '?', '#'], StringSplitOptions.RemoveEmptyEntries);
        var channelsIndex = Array.FindLastIndex(
            segments,
            segment => segment.Equals("channels", StringComparison.OrdinalIgnoreCase));
        if (channelsIndex < 0)
        {
            return false;
        }

        // .../channels/<guild>/<channel> 형태에서 마지막 snowflake를 채널로 본다.
        for (var index = segments.Length - 1; index > channelsIndex; index--)
        {
            if (IsSnowflake(segments[index]))
            {
                channelId = segments[index];
                return true;
            }
        }
        return false;
    }

    private static bool IsSnowflake(string text) =>
        text.Length is >= 17 and <= 20 && text.All(char.IsAsciiDigit);
}

/// <summary>
/// 자동 입장 판단만 담당합니다. 전송 계층(RPC)은 <see cref="IDiscordVoiceChannelClient"/>
/// 뒤에 있으므로 모든 시나리오를 Discord 없이 시험할 수 있습니다.
/// </summary>
public sealed class VoiceChannelAutoJoin(IDiscordVoiceChannelClient client, IAppLogger logger)
{
    public async Task<VoiceJoinResult> EnsureJoinedAsync(
        AutomationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseDiscordIntegration || !settings.AutoJoinVoiceChannel)
        {
            return new VoiceJoinResult(VoiceJoinOutcome.Disabled);
        }

        if (!DiscordChannelTarget.TryParse(settings.VoiceChannelTarget, out var targetChannelId))
        {
            const string message = "음성채널 링크 또는 Channel ID가 올바르지 않습니다.";
            logger.Warning("voice-join-invalid-target", message);
            return new VoiceJoinResult(VoiceJoinOutcome.InvalidTarget, message);
        }

        var connection = await client.ConnectAsync(cancellationToken);
        if (connection.Status != DiscordRpcStatus.Connected)
        {
            var outcome = connection.Status == DiscordRpcStatus.NotAuthorized
                ? VoiceJoinOutcome.NotAuthorized
                : VoiceJoinOutcome.DiscordUnavailable;
            logger.Warning("voice-join-unavailable", connection.Error ?? outcome.ToString());
            return new VoiceJoinResult(outcome, connection.Error);
        }

        var current = await client.GetSelectedVoiceChannelIdAsync(cancellationToken);
        if (current == targetChannelId)
        {
            logger.Info("voice-join-already", "이미 대상 음성채널에 있어 그대로 둡니다.");
            return new VoiceJoinResult(VoiceJoinOutcome.AlreadyInTargetChannel);
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            const string message = "다른 음성채널에 있어 자동 입장을 건너뜁니다.";
            logger.Info("voice-join-skipped-other-channel", message);
            return new VoiceJoinResult(VoiceJoinOutcome.SkippedBecauseInAnotherChannel, message);
        }

        await client.SelectVoiceChannelAsync(targetChannelId, cancellationToken);
        logger.Info("voice-joined", "대상 음성채널에 입장했습니다.");
        return new VoiceJoinResult(VoiceJoinOutcome.Joined);
    }
}
