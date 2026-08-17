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
        // 종료 후 오디오는 현재 장치 유지가 기본이다.
        Assert.False(defaults.RestoreAudioOnExit);
    }

    [Theory]
    [InlineData(AutomationState.Idle, false, "대기 중")]
    [InlineData(AutomationState.Starting, false, "자동화 시작 중")]
    [InlineData(AutomationState.Active, false, "대상 프로세스 실행 중")]
    [InlineData(AutomationState.RestorePending, false, "복원 대기")]
    [InlineData(AutomationState.RestorePending, true, "Discord 통화 종료 대기")]
    [InlineData(AutomationState.Restoring, false, "복원 중")]
    [InlineData(AutomationState.Completed, false, "복원 완료")]
    [InlineData(AutomationState.Restoring, true, "복원 중")]
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

    /// <summary>
    /// Grid.Row 인덱스가 RowDefinition 개수를 넘으면 WPF가 예외 없이 마지막 행에
    /// 요소를 겹쳐 그린다. 행을 추가할 때 정의를 함께 늘리지 않는 실수를 잡는다.
    /// </summary>
    [Fact]
    public void EveryGridRowIndexHasAMatchingRowDefinition()
    {
        var xaml = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "Eslee.OneKey.App", "MainWindow.xaml"));

        var definitionCounts = System.Text.RegularExpressions.Regex
            .Matches(xaml, "<Grid.RowDefinitions>.*?</Grid.RowDefinitions>",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(match => System.Text.RegularExpressions.Regex
                .Matches(match.Value, "<RowDefinition").Count)
            .ToArray();
        var maxRowIndex = System.Text.RegularExpressions.Regex
            .Matches(xaml, "Grid\\.Row=\"(\\d+)\"")
            .Select(match => int.Parse(match.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

        Assert.NotEmpty(definitionCounts);
        // 가장 큰 Grid.Row는 어느 한 Grid의 RowDefinition 개수 안에 들어와야 한다.
        Assert.True(
            maxRowIndex < definitionCounts.Max(),
            $"Grid.Row 최대값 {maxRowIndex}가 RowDefinition 최대 개수 {definitionCounts.Max()}를 벗어납니다.");
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
