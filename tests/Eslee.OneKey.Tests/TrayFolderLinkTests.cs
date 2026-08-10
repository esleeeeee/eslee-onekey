using System.IO.Pipes;
using System.Text;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

public sealed class TrayFolderLinkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void BuildPipeNameMatchesTrayFolderConvention()
    {
        // Tray Folder 저장소의 TrayPipeProtocolTests와 같은 기대값을 사용해
        // 저장소 간 파이프 이름 규약이 일치하는지 확인합니다.
        Assert.Equal(
            "eslee.trayfolder.tray-host.v1.user-1_a--",
            TrayFolderLink.BuildPipeName("user 1_a!한"));
    }

    [Fact]
    public void MenuMirrorsTrayIconServiceLayoutAndPauseCheckmark()
    {
        var pausedItems = OneKeyTrayFolderMenu.Build(isPaused: true);
        var idleItems = OneKeyTrayFolderMenu.Build(isPaused: false);

        Assert.Equal(5, pausedItems.Count);
        Assert.Equal(
            new[]
            {
                OneKeyTrayFolderMenu.OpenAppActionId,
                OneKeyTrayFolderMenu.TogglePauseActionId,
                OneKeyTrayFolderMenu.ShowStatusActionId,
                null,
                OneKeyTrayFolderMenu.ExitActionId,
            },
            pausedItems.Select(item => item.Id).ToArray());
        Assert.True(pausedItems[3].IsSeparator);
        Assert.True(pausedItems.Single(item => item.Id == OneKeyTrayFolderMenu.TogglePauseActionId).Checked);
        Assert.False(idleItems.Single(item => item.Id == OneKeyTrayFolderMenu.TogglePauseActionId).Checked);
    }

    [Fact]
    public async Task RegistersAppliesHostedModeAnswersMenuAndRestoresOnDisconnect()
    {
        var pipeName = "eslee.onekey.link-test." + Guid.NewGuid().ToString("N");
        var hiddenSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visibleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<string>();
        using var link = new TrayFolderLink(
            pipeName,
            "eslee.onekey",
            "eslee OneKey",
            processId: 4321,
            visible =>
            {
                if (visible)
                {
                    visibleSignal.TrySetResult(true);
                }
                else
                {
                    hiddenSignal.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            () => Task.FromResult(OneKeyTrayFolderMenu.Build(isPaused: true)),
            actionId =>
            {
                lock (executed)
                {
                    executed.Add(actionId);
                }

                return Task.FromResult(actionId == OneKeyTrayFolderMenu.TogglePauseActionId);
            },
            (_, _) => { },
            (_, _) => { },
            reconnectDelay: TimeSpan.FromMilliseconds(100),
            connectTimeout: TimeSpan.FromMilliseconds(500));
        link.Start();

        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096);
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = new StreamReader(
                server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var writer = new StreamWriter(
                server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            await using (writer.ConfigureAwait(false))
            {
                var registerLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.NotNull(registerLine);
                Assert.Contains("\"type\":\"register\"", registerLine, StringComparison.Ordinal);
                Assert.Contains("\"protocolVersion\":1", registerLine, StringComparison.Ordinal);
                Assert.Contains("\"appId\":\"eslee.onekey\"", registerLine, StringComparison.Ordinal);

                await writer.WriteLineAsync("""{"type":"set-tray-mode","mode":"hosted"}""");
                Assert.True(await hiddenSignal.Task.WaitAsync(TestTimeout));

                await writer.WriteLineAsync("""{"type":"get-menu","id":3}""");
                var menuLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.NotNull(menuLine);
                Assert.Contains("\"type\":\"menu\"", menuLine, StringComparison.Ordinal);
                Assert.Contains("\"id\":3", menuLine, StringComparison.Ordinal);
                Assert.Contains("\"checked\":true", menuLine, StringComparison.Ordinal);
                Assert.Contains("toggle-pause", menuLine, StringComparison.Ordinal);

                await writer.WriteLineAsync(
                    """{"type":"command","id":4,"command":"menu-action","actionId":"toggle-pause"}""");
                var resultLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.NotNull(resultLine);
                Assert.Contains("\"id\":4", resultLine, StringComparison.Ordinal);
                Assert.Contains("\"succeeded\":true", resultLine, StringComparison.Ordinal);
                lock (executed)
                {
                    Assert.Equal(new[] { "toggle-pause" }, executed);
                }
            }
        }

        // 호스트가 종료되면 클라이언트는 자체 트레이 아이콘을 다시 표시해야 합니다.
        Assert.True(await visibleSignal.Task.WaitAsync(TestTimeout));
    }
}
