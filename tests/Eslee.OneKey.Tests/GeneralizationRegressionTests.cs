using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

/// <summary>
/// OneKey가 특정 게임 전용 앱처럼 보이지 않도록 지키는 회귀 테스트.
/// 사용자가 자동화 이름 등에 게임 이름을 입력하는 것은 자유지만,
/// 앱 코드와 기본값이 특정 게임을 하드코딩하면 안 된다.
/// </summary>
public sealed class GeneralizationRegressionTests
{
    private static readonly string[] ForbiddenTerms = ["발로란트", "valorant", "라이엇", "riot"];

    [Fact]
    public void SourceAndUiContainNoGameSpecificWording()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var offending = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var separator = Path.DirectorySeparatorChar;
            if (file.Contains($"{separator}obj{separator}") || file.Contains($"{separator}bin{separator}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            offending.AddRange(ForbiddenTerms
                .Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(term => $"{Path.GetRelativePath(sourceRoot, file)}: \"{term}\""));
        }

        Assert.Empty(offending);
    }

    [Fact]
    public void DefaultAutomationSettingsAreGameNeutral()
    {
        var defaults = new AutomationSettings();

        Assert.Equal("새 자동화", defaults.Name);
        Assert.Equal(string.Empty, defaults.WatchProcessName);
        Assert.Equal(string.Empty, defaults.LaunchExecutablePath);
        Assert.False(defaults.UseDiscordIntegration);
    }

    [Theory]
    [InlineData(AutomationState.Idle, false, "대기 중")]
    [InlineData(AutomationState.Starting, false, "자동화 시작 중")]
    [InlineData(AutomationState.Active, false, "대상 프로세스 실행 중")]
    [InlineData(AutomationState.RestorePending, false, "복원 대기")]
    [InlineData(AutomationState.RestorePending, true, "Discord 통화 종료 대기")]
    [InlineData(AutomationState.Restoring, false, "복원 중")]
    [InlineData(AutomationState.Completed, false, "복원 완료")]
    [InlineData(AutomationState.Failed, false, "오류")]
    public void StatusTextIsGeneralized(
        AutomationState state,
        bool waitingForDiscordVoice,
        string expected)
    {
        var text = AutomationStatusText.ForState(state, waitingForDiscordVoice);

        Assert.Equal(expected, text);
        Assert.All(ForbiddenTerms, term =>
            Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "eslee-onekey.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("eslee-onekey.slnx 기준 저장소 루트를 찾을 수 없습니다.");
    }
}
