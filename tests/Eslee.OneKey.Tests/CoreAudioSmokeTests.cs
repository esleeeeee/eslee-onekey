using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 실제 Windows 오디오 장치가 있는 PC에서만 의미가 있는 smoke test.
/// CI처럼 오디오 장치가 없을 수 있는 환경을 위해 ONEKEY_AUDIO_SMOKE=1을
/// 설정한 경우에만 실행되는 opt-in 방식이다. 조회만 수행하며
/// 기본 장치 전환(SetDefaultOutputAsync)은 호출하지 않는다.
/// </summary>
public sealed class CoreAudioSmokeTests
{
    [AudioSmokeFact]
    public async Task EnumeratesRenderEndpointsWithIdAndName()
    {
        var service = new CoreAudioEndpointService();

        var endpoints = await service.GetOutputEndpointsAsync(CancellationToken.None);

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Id));
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Name));
        });
    }

    [AudioSmokeFact]
    public async Task ReadsDefaultRenderEndpointAndFindsItInEnumeration()
    {
        var service = new CoreAudioEndpointService();

        var defaultId = await service.GetDefaultOutputIdAsync(CancellationToken.None);
        var endpoints = await service.GetOutputEndpointsAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(defaultId));
        Assert.Contains(endpoints, endpoint => endpoint.Id == defaultId);
    }
}

public sealed class AudioSmokeFactAttribute : FactAttribute
{
    public AudioSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ONEKEY_AUDIO_SMOKE") != "1")
        {
            Skip = "실제 오디오 장치 smoke test는 ONEKEY_AUDIO_SMOKE=1 설정 시에만 실행됩니다.";
        }
    }
}
