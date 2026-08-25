using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 계정 프로필마다 단축키 서비스를 하나씩 만들고 모두 같은 창에 등록합니다.
/// ID가 겹치면 WM_HOTKEY의 wParam으로 어떤 조합이 눌렸는지 구분할 수 없어,
/// 한 단축키를 눌러도 모든 프로필이 함께 반응합니다.
/// </summary>
public sealed class GlobalHotkeyIdTests
{
    private const int WmHotkey = 0x0312;

    [Fact]
    public void EveryHotkeyGetsItsOwnIdentifier()
    {
        var ids = Enumerable.Range(0, 50)
            .Select(_ => WindowsGlobalHotkeyService.NextHotkeyId())
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void IdentifiersStayInTheRangeWindowsReservesForApplications()
    {
        var id = WindowsGlobalHotkeyService.NextHotkeyId();

        Assert.InRange(id, 0x0000, 0xBFFF);
    }

    [Fact]
    public void AHotkeyOnlyAnswersToItsOwnIdentifier()
    {
        var mine = WindowsGlobalHotkeyService.NextHotkeyId();
        var other = WindowsGlobalHotkeyService.NextHotkeyId();

        Assert.True(WindowsGlobalHotkeyService.IsOwnHotkeyMessage(WmHotkey, mine, mine));
        Assert.False(WindowsGlobalHotkeyService.IsOwnHotkeyMessage(WmHotkey, other, mine));
    }

    [Fact]
    public void OtherWindowMessagesAreIgnored()
    {
        var mine = WindowsGlobalHotkeyService.NextHotkeyId();

        Assert.False(WindowsGlobalHotkeyService.IsOwnHotkeyMessage(0x0100, mine, mine));
    }
}
