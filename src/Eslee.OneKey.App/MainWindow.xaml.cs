using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Eslee.OneKey.App;

public partial class MainWindow : Window
{
    private readonly bool _startMinimized;
    private readonly ApplicationPaths _paths = new();
    private readonly CoreAudioEndpointService _audio = new();
    private readonly WindowsProcessService _processes = new();
    private readonly StartupRegistrationService _startup = new();
    private JsonSettingsStore? _settingsStore;
    private JsonSessionStore? _sessionStore;
    private DpapiSecretStore? _secretStore;
    private FileAppLogger? _logger;
    private AppSettings _appSettings = new();
    private AutomationSettings _automation = new();

    /// <summary>왼쪽 목록에 보이는 줄입니다.</summary>
    private readonly ObservableCollection<AutomationRuleViewModel> _ruleItems = [];

    /// <summary>늦게 도착한 조회 결과가 새 선택을 덮지 않도록 세는 번호입니다.</summary>
    private int _discordLoadGeneration;


    /// <summary>봇도 함께 들어가 있는 서버만 담습니다. 앱이 도는 동안 재사용합니다.</summary>
    private readonly List<DiscordGuild> _guilds = [];
    private readonly Dictionary<string, IReadOnlyList<DiscordVoiceChannel>> _voiceChannels = [];

    /// <summary>자동화 규칙 전체입니다. 단축키는 규칙에만 있습니다.</summary>
    private readonly List<AutomationSettings> _rules = [];
    private int _editingRule = -1;
    private bool _loadingRule;
    private AutomationEngine? _engine;
    private AutomationCoordinator? _coordinator;
    private TrayIconService? _tray;
    private TrayFolderLink? _trayFolderLink;
    private DiscordRpcVoiceChannelClient? _rpcVoiceClient;
    private HttpClient? _updateHttpClient;
    private UpdateCheckService? _updateChecker;
    private DispatcherTimer? _updateTimer;
    private string? _latestReleaseUrl;
    private bool _paused;
    private bool _allowClose;
    private bool _shuttingDown;
    private bool _initializationFailed;
    private bool _initializationErrorShown;

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private static Version CurrentVersion =>
        typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public MainWindow(bool startMinimized)
    {
        _startMinimized = startMinimized;
        InitializeComponent();
        VersionText.Text = $"eslee OneKey {UpdateCheckService.FormatVersion(CurrentVersion)}";
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureDirectories();
            _settingsStore = new JsonSettingsStore(_paths);
            _sessionStore = new JsonSessionStore(_paths);
            _secretStore = new DpapiSecretStore(_paths);
            _logger = new FileAppLogger(_paths);
            _logger.Info("app-start", "eslee OneKey를 시작했습니다.");
            _tray = new TrayIconService(this);

            // Tray Folder 연동: Hosted 모드에서는 아이콘만 숨기고 자동화는 그대로
            // 유지됩니다. 연결이 끊어지면 링크가 아이콘을 자동 복구합니다.
            _trayFolderLink = new TrayFolderLink(
                TrayFolderLink.BuildDefaultPipeName(),
                "eslee.onekey",
                "eslee OneKey",
                Environment.ProcessId,
                visible => Dispatcher.InvokeAsync(() => _tray?.SetTrayIconVisible(visible)).Task,
                () => Dispatcher.InvokeAsync(OpenFromTray).Task,
                () => Dispatcher.InvokeAsync(
                    () => OneKeyTrayFolderMenu.Build(_tray?.IsPaused ?? _paused)).Task,
                actionId => Dispatcher.InvokeAsync(() => TryStartTrayFolderMenuAction(actionId)).Task,
                (eventName, message) => _logger?.Info(eventName, message),
                (eventName, exception) => _logger?.Error(eventName, exception, exception.Message));
            _trayFolderLink.Start();

            _appSettings = await _settingsStore.LoadAsync(CancellationToken.None);
            _rules.Clear();
            _rules.AddRange(_appSettings.Automations);
            if (_rules.Count == 0)
            {
                _rules.Add(new AutomationSettings());
            }
            _automation = ActiveRule();
            ApplySettingsToControls();
            await RefreshRpcStatusAsync();
            await RefreshAccountStatusesAsync();
            await RefreshAudioEndpointsAsync();
            await StartRuntimeAsync();
            UpdateStatus();

            if (_startMinimized)
            {
                Hide();
            }
        }
        catch (Exception exception)
        {
            _logger?.Error("app-initialize-failed", exception, "초기화에 실패했습니다.");
            EnterInitializationFailedState(exception);
        }

        // 업데이트 확인은 자동화 초기화 성공 여부와 무관하게 동작하며,
        // 실패해도 자동화에 영향을 주지 않는다.
        StartUpdateChecks();
    }

    private void StartUpdateChecks()
    {
        _updateHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _updateChecker = new UpdateCheckService(_updateHttpClient);
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => _ = RunUpdateCheckAsync();
        _updateTimer.Start();
        _ = RunUpdateCheckAsync();
    }

    private async Task RunUpdateCheckAsync()
    {
        if (_updateChecker is null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "업데이트를 확인하는 중...";
        try
        {
            var result = await _updateChecker.CheckAsync(CurrentVersion, CancellationToken.None);
            _latestReleaseUrl = result.ReleaseUrl;
            UpdateStatusText.Text = result.Status switch
            {
                UpdateCheckStatus.UpToDate =>
                    $"최신 버전입니다. (현재 {UpdateCheckService.FormatVersion(CurrentVersion)})",
                UpdateCheckStatus.UpdateAvailable =>
                    $"새 버전 {result.LatestVersion}을(를) 사용할 수 있습니다. " +
                    "Release 페이지에서 내려받으세요.",
                UpdateCheckStatus.NoReleaseFound =>
                    "확인 가능한 정식 릴리스가 없습니다. 저장소가 비공개이거나 아직 릴리스가 게시되지 않았습니다.",
                _ => "업데이트 확인에 실패했습니다. 잠시 후 다시 시도하세요.",
            };
        }
        catch (Exception exception)
        {
            // 어떤 예외도 자동화 동작에 영향을 주지 않도록 여기서 끝낸다.
            _logger?.Error("update-check-failed", exception, "업데이트 확인 중 오류가 발생했습니다.");
            UpdateStatusText.Text = "업데이트 확인에 실패했습니다. 잠시 후 다시 시도하세요.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunUpdateCheckAsync();

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_latestReleaseUrl ?? UpdateCheckService.ReleasesPageUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _logger?.Error("open-release-page-failed", exception, "Release 페이지를 여는 데 실패했습니다.");
            MessageBox.Show(
                $"브라우저를 열지 못했습니다. 직접 방문하세요:\n{UpdateCheckService.ReleasesPageUrl}",
                "eslee OneKey");
        }
    }

    private void EnterInitializationFailedState(Exception exception)
    {
        _initializationFailed = true;
        var summary = BuildInitializationErrorSummary(exception);
        GlobalStateText.Text = "초기화 실패";
        LastErrorStatus.Text = $"{summary} 로그: {_paths.LogFile}";
        LastErrorStatus.Visibility = Visibility.Visible;
        StartNowButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        ManualRestoreButton.IsEnabled = false;
        KeepCurrentButton.IsEnabled = false;
        // 설정을 읽지 못한 상태에서 저장하면 빈 컨트롤 값이 원본 파일을 덮어쓴다.
        SaveSettingsButton.IsEnabled = false;

        if (_initializationErrorShown)
        {
            return;
        }
        _initializationErrorShown = true;
        MessageBox.Show(
            $"{summary}\n\n자세한 내용은 로그 파일을 확인하세요.\n{_paths.LogFile}",
            "OneKey 초기화 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string BuildInitializationErrorSummary(Exception exception) => exception switch
    {
        COMException or InvalidCastException =>
            "Windows 오디오 장치 정보를 읽지 못해 자동화를 사용할 수 없습니다.",
        IOException or UnauthorizedAccessException or System.Text.Json.JsonException =>
            "설정 파일을 읽거나 쓰지 못해 자동화를 사용할 수 없습니다.",
        _ => "초기화 중 오류가 발생해 자동화를 사용할 수 없습니다.",
    };

    private async Task StartRuntimeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
            _coordinator = null;
        }
        if (_engine is not null)
        {
            // 이전 세션의 음성채널 재시도가 새 세션에 남지 않게 한다.
            _engine.StateChanged -= Engine_StateChanged;
            await _engine.DisposeAsync();
            _engine = null;
        }
        if (_rpcVoiceClient is not null)
        {
            await _rpcVoiceClient.DisposeAsync();
            _rpcVoiceClient = null;
        }

        if (_rules.TrueForAll(rule => !rule.Enabled) ||
            _sessionStore is null ||
            _secretStore is null ||
            _logger is null)
        {
            return;
        }

        // 통화 상태는 로컬 Discord에 직접 묻는다. 자동 입장과 같은 RPC 연결을 함께 쓴다.
        EnsureRpcVoiceClient(_automation);
        var voiceClient = new DiscordRpcVoiceStatusClient(
            () => _rpcVoiceClient,
            _processes,
            _automation.DiscordProcessName);
        _engine = new AutomationEngine(
            _automation,
            _audio,
            _processes,
            voiceClient,
            _sessionStore,
            new SystemClock(),
            _logger,
            CreateVoiceChannelAutoJoin(_automation),
            CreateAccountSessionService());
        // 규칙마다 Discord 설정이 다를 수 있으므로 규칙이 바뀌면 다시 만든다.
        _engine.VoiceChannelAutoJoinFactory = CreateVoiceChannelAutoJoin;
        _engine.StateChanged += Engine_StateChanged;

        var windowHandle = new WindowInteropHelper(this).Handle;
        var hotkey = new WindowsGlobalHotkeyService(windowHandle);
        var monitor = new PollingProcessMonitor();
        _coordinator = new AutomationCoordinator(
            _automation,
            _engine,
            hotkey,
            monitor,
            CreateRuleBindings(windowHandle));
        await _coordinator.InitializeAsync();
        if (_coordinator.LastError is not null)
        {
            _tray?.ShowBalloon("전역 단축키 등록 실패", _coordinator.LastError);
        }
    }

    /// <summary>
    /// 규칙마다 전역 단축키를 하나씩 답니다. 규칙이 계정 프로필을 지정했으면 그 계정으로
    /// 시작하고, 지정하지 않았으면 계정을 건드리지 않습니다. 꺼 둔 규칙은 단축키를
    /// 등록하지 않습니다.
    /// </summary>
    private IReadOnlyList<AutomationCoordinator.AutomationRuleBinding> CreateRuleBindings(nint windowHandle) =>
        _rules
            .Where(rule => rule.Enabled)
            .Select(rule => new AutomationCoordinator.AutomationRuleBinding(
                rule,
                new WindowsGlobalHotkeyService(windowHandle),
                rule.AccountProfileId is { } id
                    ? _appSettings.AccountProfiles.FirstOrDefault(profile => profile.Id == id)
                    : null))
            .ToArray();

    /// <summary>지금 편집 중인 규칙입니다. 없으면 첫 규칙을 씁니다.</summary>
    private AutomationSettings ActiveRule() =>
        _rules.FirstOrDefault(rule => rule.Enabled) ?? _rules.FirstOrDefault() ?? new AutomationSettings();

    private GameAccountSessionService? CreateAccountSessionService() =>
        _secretStore is null || _logger is null
            ? null
            : new GameAccountSessionService(_paths, _secretStore, _processes, _logger);

    /// <summary>
    /// 음성채널 자동 입장은 Discord 연동과 자동 입장이 모두 켜져 있을 때만 구성합니다.
    /// 실패해도 자동화 자체는 그대로 동작합니다.
    /// </summary>
    private VoiceChannelAutoJoin? CreateVoiceChannelAutoJoin(AutomationSettings rule)
    {
        if (!rule.UseDiscordIntegration ||
            !rule.AutoJoinVoiceChannel ||
            _logger is null)
        {
            return null;
        }

        var client = EnsureRpcVoiceClient(rule);
        return client is null ? null : new VoiceChannelAutoJoin(client, _logger);
    }

    /// <summary>
    /// 자동 입장과 통화 상태 조회가 함께 쓰는 RPC 연결입니다. 하나만 만들어 재사용합니다.
    /// Discord는 연결을 끊은 직후 한동안 새 연결을 거부하므로, 조회할 때마다 새로 만들면
    /// 안 됩니다. 자동화를 다시 적용할 때 통째로 정리하고 다시 만듭니다.
    /// </summary>
    private DiscordRpcVoiceChannelClient? EnsureRpcVoiceClient(AutomationSettings rule)
    {
        if (!rule.UseDiscordIntegration || _logger is null)
        {
            return null;
        }

        _rpcVoiceClient ??= new DiscordRpcVoiceChannelClient(
            AppDefaults.ResolveRpcClientId(_appSettings.DiscordRpcClientId),
            async cancellationToken => (await GetUsableRpcTokensAsync(cancellationToken))?.AccessToken,
            TimeSpan.FromSeconds(20));
        return _rpcVoiceClient;
    }

    private async Task<DiscordRpcTokens?> LoadRpcTokensAsync(CancellationToken cancellationToken)
    {
        if (_secretStore is null)
        {
            return null;
        }
        var payload = await _secretStore.LoadRpcSecretsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<DiscordRpcTokens>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 만료가 임박했으면 refresh_token으로 조용히 갱신합니다. 갱신에 실패하면 기존
    /// 토큰을 그대로 써 보고, 그것도 실패하면 사용자에게 재연결을 안내합니다.
    /// </summary>
    private async Task<DiscordRpcTokens?> GetUsableRpcTokensAsync(CancellationToken cancellationToken)
    {
        var tokens = await LoadRpcTokensAsync(cancellationToken);
        if (tokens is null || !DiscordRpcAuthorizer.NeedsRefresh(tokens) || _secretStore is null)
        {
            return tokens;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var refreshed = await new DiscordRpcAuthorizer(client).RefreshAsync(
                AppDefaults.ResolveRpcClientId(_appSettings.DiscordRpcClientId),
                tokens,
                cancellationToken);
            if (refreshed is null)
            {
                return tokens;
            }

            await _secretStore.SaveRpcSecretsAsync(
                System.Text.Json.JsonSerializer.Serialize(refreshed),
                cancellationToken);
            _logger?.Info("rpc-token-refreshed", "Discord RPC 토큰을 갱신했습니다.");
            return refreshed;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or System.Text.Json.JsonException
            or IOException)
        {
            _logger?.Warning("rpc-token-refresh-failed", "Discord RPC 토큰 갱신에 실패했습니다.");
            return tokens;
        }
    }

    private async Task RefreshRpcStatusAsync()
    {
        var tokens = await LoadRpcTokensAsync(CancellationToken.None);
        RpcStatusText.Text = tokens is null
            ? "미연결"
            : DiscordRpcAuthorizer.NeedsRefresh(tokens) ? "갱신 필요" : "연결됨";
    }

    private async void ConnectRpc_Click(object sender, RoutedEventArgs e)
    {
        if (_secretStore is null)
        {
            return;
        }

        ConnectRpcButton.IsEnabled = false;
        RpcStatusText.Text = "연결 중...";
        try
        {
            var storedSecret = (await LoadRpcTokensAsync(CancellationToken.None))?.ClientSecret;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var (result, tokens) = await new DiscordRpcAuthorizer(client).AuthorizeAsync(
                AppDefaults.ResolveRpcClientId(_appSettings.DiscordRpcClientId),
                storedSecret,
                CancellationToken.None);

            if (result.Succeeded && tokens is not null)
            {
                await _secretStore.SaveRpcSecretsAsync(
                    System.Text.Json.JsonSerializer.Serialize(tokens),
                    CancellationToken.None);
                _logger?.Info("rpc-connected", "Discord RPC 인증을 완료했습니다.");
                // 성공은 팝업 대신 화면의 연결 상태로 바로 알립니다.
                await RefreshDiscordConnectionAsync();
                await LoadDiscordListsAsync(force: true);
            }
            else
            {
                RpcStatusText.Text = "Discord 연결 필요";
                _logger?.Warning("rpc-connect-failed", "Discord RPC 인증에 실패했습니다.");
                MessageBox.Show(this, result.Message, "Discord 연결", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            RpcStatusText.Text = "연결할 수 없음";
            _logger?.Error("rpc-connect-failed", exception, "Discord RPC 인증 중 오류가 발생했습니다.");
            MessageBox.Show(this, exception.Message, "Discord 연결", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectRpcButton.IsEnabled = true;
        }
    }

    private void ApplySettingsToControls()
    {
        RefreshRuleList(_rules.IndexOf(_automation) is var found && found >= 0 ? found : 0);
        LoadGlobalSettingsIntoControls();
    }

    /// <summary>왼쪽 목록을 다시 그리고 지정한 자동화를 오른쪽에 올립니다.</summary>
    private void RefreshRuleList(int index)
    {
        _loadingRule = true;
        _ruleItems.Clear();
        foreach (var rule in _rules)
        {
            _ruleItems.Add(new AutomationRuleViewModel(rule));
        }
        AutomationList.ItemsSource = _ruleItems;
        _editingRule = Math.Clamp(index, 0, Math.Max(0, _rules.Count - 1));
        AutomationList.SelectedIndex = _rules.Count == 0 ? -1 : _editingRule;
        _loadingRule = false;
        if (_rules.Count > 0)
        {
            LoadRuleIntoControls(_rules[_editingRule]);
        }
        UpdateStatus();
    }

    private void AutomationRule_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingRule || AutomationList.SelectedIndex < 0)
        {
            return;
        }

        // 편집하던 값을 잃지 않도록 먼저 되받아 둔다. 저장은 저장 버튼이 한다.
        CommitEditingRule();
        _editingRule = AutomationList.SelectedIndex;
        LoadRuleIntoControls(_rules[_editingRule]);
    }

    /// <summary>목록의 ON/OFF입니다. 끈 자동화는 단축키를 등록하지 않습니다.</summary>
    private async void ToggleRuleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AutomationRuleViewModel item)
        {
            return;
        }

        var index = _rules.FindIndex(rule => rule.Id == item.Id);
        if (index < 0)
        {
            return;
        }

        if (index == _editingRule)
        {
            CommitEditingRule();
        }
        _rules[index] = _rules[index] with { Enabled = item.Enabled };
        if (index == _editingRule)
        {
            EnabledCheck.IsChecked = item.Enabled;
        }
        await ApplyRulesAsync("자동화 사용 여부를 바꿨습니다.");
    }

    /// <summary>규칙을 저장하고 단축키를 다시 등록합니다.</summary>
    private async Task ApplyRulesAsync(string logMessage)
    {
        if (_settingsStore is null)
        {
            return;
        }

        _appSettings = _appSettings with
        {
            SchemaVersion = SettingsMigration.CurrentSchemaVersion,
            Automations = [.. _rules],
        };
        await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
        _logger?.Info("automation-rules-applied", logMessage);
        await StartRuntimeAsync();
        UpdateStatus();
    }

    /// <summary>선택한 자동화를 오른쪽 상세 화면에 올립니다.</summary>
    private void LoadRuleIntoControls(AutomationSettings rule)
    {
        _automation = rule;
        _loadingRule = true;

        AutomationNameText.Text = rule.Name;
        EnabledCheck.IsChecked = rule.Enabled;
        CtrlCheck.IsChecked = rule.Hotkey.Control;
        AltCheck.IsChecked = rule.Hotkey.Alt;
        ShiftCheck.IsChecked = rule.Hotkey.Shift;
        WinCheck.IsChecked = rule.Hotkey.Windows;
        HotkeyText.Text = rule.Hotkey.Key;

        UseProgramCheck.IsChecked = !string.IsNullOrWhiteSpace(rule.LaunchExecutablePath);
        LaunchPathText.Text = rule.LaunchExecutablePath;
        WatchProcessText.Text = rule.WatchProcessName;

        UseAccountCheck.IsChecked = rule.AccountProfileId is not null;
        var profile = CurrentAccountProfile();
        SessionFilePathText.Text = profile?.SessionFilePath ?? string.Empty;
        LauncherProcessesText.Text = string.Join(", ", profile?.LauncherProcessNames ?? []);
        BlockingProcessesText.Text = string.Join(", ", profile?.BlockingProcessNames ?? []);

        UseAudioCheck.IsChecked = !string.IsNullOrWhiteSpace(rule.TargetAudioEndpointId);
        AudioEndpointCombo.SelectedValue = rule.TargetAudioEndpointId;
        KeepDeviceRadio.IsChecked = !rule.RestoreAudioOnExit;
        RestoreDeviceRadio.IsChecked = rule.RestoreAudioOnExit;
        DeferRestoreCheck.IsChecked = rule.DeferRestoreWhileDiscordInVoice;

        UseDiscordCheck.IsChecked = rule.UseDiscordIntegration;
        AutoJoinVoiceCheck.IsChecked = rule.AutoJoinVoiceChannel;
        // 수동 입력은 진짜 폴백입니다. 목록에서 풀리는 값은 여기 넣지 않습니다.
        VoiceChannelTargetText.Text = string.Empty;
        ShowSavedDiscordSelection(rule);

        _loadingRule = false;
        UpdateSectionVisibility();
        _ = RefreshAccountStatusesAsync();
        _ = RefreshDiscordConnectionAsync();
    }

    /// <summary>
    /// 설정이 저장돼 있다고 연결된 것은 아닙니다. 실제로 물어보고 나서 표시합니다.
    /// 화면을 옮기는 사이에 늦게 도착한 결과가 새 화면을 덮지 않도록 세대 번호로 거릅니다.
    /// </summary>
    private async Task RefreshDiscordConnectionAsync()
    {
        var generation = ++_discordLoadGeneration;
        RpcStatusText.Text = "확인 중...";

        if (UseDiscordCheck.IsChecked != true)
        {
            RpcStatusText.Text = "사용 안 함";
            return;
        }

        var processName = string.IsNullOrWhiteSpace(_appSettings.DiscordProcessName)
            ? "Discord"
            : _appSettings.DiscordProcessName;
        try
        {
            if (!await _processes.IsRunningAsync(processName, CancellationToken.None))
            {
                if (generation == _discordLoadGeneration)
                {
                    RpcStatusText.Text = "Discord를 실행해 주세요";
                }
                return;
            }

            var client = EnsureRpcVoiceClient(_automation);
            if (client is null)
            {
                if (generation == _discordLoadGeneration)
                {
                    RpcStatusText.Text = "Discord 연결 필요";
                }
                return;
            }

            var connection = await client.EnsureConnectedAsync(CancellationToken.None);
            if (generation != _discordLoadGeneration)
            {
                return;
            }
            RpcStatusText.Text = connection.Status switch
            {
                DiscordRpcStatus.Connected => "연결됨",
                DiscordRpcStatus.NotAuthorized => "Discord 연결 필요",
                _ => "연결할 수 없음",
            };
        }
        catch (Exception exception)
        {
            _logger?.Error("rpc-status-failed", exception, "Discord 연결 상태를 확인하지 못했습니다.");
            if (generation == _discordLoadGeneration)
            {
                RpcStatusText.Text = "연결할 수 없음";
            }
        }
    }

    /// <summary>
    /// 목록을 아직 불러오지 않았어도 지금 무엇이 선택돼 있는지는 보여 줍니다.
    /// 목록 새로고침을 누르면 실제 이름으로 바뀝니다.
    /// </summary>
    private void ShowSavedDiscordSelection(AutomationSettings rule)
    {
        if (_guilds.Count == 0 && !string.IsNullOrWhiteSpace(rule.VoiceChannelGuildId))
        {
            GuildCombo.ItemsSource = new[] { new DiscordGuild(rule.VoiceChannelGuildId, "저장된 서버") };
        }
        else
        {
            GuildCombo.ItemsSource = _guilds;
        }
        GuildCombo.SelectedValue = rule.VoiceChannelGuildId;

        var channels = ChannelsFor(rule.VoiceChannelGuildId);
        if (channels.Count == 0 && !string.IsNullOrWhiteSpace(rule.VoiceChannelTarget))
        {
            VoiceChannelCombo.ItemsSource = new[]
            {
                new DiscordVoiceChannel(rule.VoiceChannelTarget, "저장된 음성채널"),
            };
        }
        else
        {
            VoiceChannelCombo.ItemsSource = channels;
        }
        VoiceChannelCombo.SelectedValue = rule.VoiceChannelTarget;
    }

    private IReadOnlyList<DiscordVoiceChannel> ChannelsFor(string guildId) =>
        !string.IsNullOrWhiteSpace(guildId) && _voiceChannels.TryGetValue(guildId, out var channels)
            ? channels
            : [];

    /// <summary>끈 기능의 세부 설정은 감춥니다. 화면이 단순해집니다.</summary>
    private void UpdateSectionVisibility()
    {
        ProgramPanel.Visibility = Visible(UseProgramCheck.IsChecked == true);
        AccountPanel.Visibility = Visible(UseAccountCheck.IsChecked == true);
        AudioPanel.Visibility = Visible(UseAudioCheck.IsChecked == true);
        DiscordPanel.Visibility = Visible(UseDiscordCheck.IsChecked == true);
        AutoJoinPanel.Visibility = Visible(
            UseDiscordCheck.IsChecked == true && AutoJoinVoiceCheck.IsChecked == true);
        // 복원 보류는 실행 전 장치로 되돌릴 때만 의미가 있습니다.
        DeferRestorePanel.Visibility = Visible(
            UseAudioCheck.IsChecked == true && RestoreDeviceRadio.IsChecked == true);
    }

    private static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void FeatureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingRule)
        {
            UpdateSectionVisibility();
            if (ReferenceEquals(sender, UseAccountCheck))
            {
                _ = RefreshAccountStatusesAsync();
            }
            if (ReferenceEquals(sender, UseDiscordCheck) && UseDiscordCheck.IsChecked == true)
            {
                CommitEditingRule();
                _ = RefreshDiscordConnectionAsync();
            }
        }
    }

    /// <summary>
    /// 폼에 입력된 값을 편집 중이던 규칙에 되받습니다. 화면에서 방금 켠 값을 곧바로
    /// 쓰는 곳이 있어 현재 규칙도 함께 맞춰 둡니다. 그러지 않으면 Discord 연동을 켜자마자
    /// 목록을 불러올 때 아직 꺼진 것으로 보입니다.
    /// </summary>
    private void CommitEditingRule()
    {
        if (_editingRule >= 0 && _editingRule < _rules.Count)
        {
            _rules[_editingRule] = ReadAutomationFromControls();
            _automation = _rules[_editingRule];
        }
    }

    private void AddAutomationRule_Click(object sender, RoutedEventArgs e)
    {
        CommitEditingRule();
        // 같은 실행 환경을 공유하는 규칙을 만들기 쉽도록 현재 규칙을 본떠 만든다.
        var template = _rules.Count > 0 ? _rules[_editingRule] : new AutomationSettings();
        _rules.Add(template with
        {
            Id = Guid.NewGuid(),
            Name = "새 자동화",
            Hotkey = new HotkeyGesture(true, true, true, false, string.Empty),
            AccountProfileId = null,
        });
        RefreshRuleList(_rules.Count - 1);
    }

    private void DeleteAutomationRule_Click(object sender, RoutedEventArgs e)
    {
        if (_rules.Count <= 1)
        {
            MessageBox.Show("자동화 규칙은 최소 하나가 필요합니다.", "자동화");
            return;
        }

        if (MessageBox.Show(
                $"{_rules[_editingRule].Name} 규칙을 삭제할까요? 계정 프로필과 저장된 세션은 그대로 남습니다.",
                "자동화",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _rules.RemoveAt(_editingRule);
        RefreshRuleList(Math.Max(0, _editingRule - 1));
    }

    private async Task RefreshAudioEndpointsAsync()
    {
        var selected = AudioEndpointCombo.SelectedValue as string
            ?? _automation.TargetAudioEndpointId;
        var endpoints = await _audio.GetOutputEndpointsAsync(CancellationToken.None);
        AudioEndpointCombo.ItemsSource = endpoints;
        AudioEndpointCombo.SelectedValue = selected;
    }

    private AutomationSettings ReadAutomationFromControls() => new()
    {
        Id = _automation.Id,
        Name = AutomationNameText.Text.Trim(),
        AccountProfileId = UseAccountCheck.IsChecked == true
            ? _automation.AccountProfileId ?? EnsureAccountProfile().Id
            : null,
        Enabled = EnabledCheck.IsChecked == true,
        Hotkey = new HotkeyGesture(
            CtrlCheck.IsChecked == true,
            AltCheck.IsChecked == true,
            ShiftCheck.IsChecked == true,
            WinCheck.IsChecked == true,
            HotkeyText.Text.Trim()),
        LaunchExecutablePath = UseProgramCheck.IsChecked == true ? LaunchPathText.Text.Trim() : string.Empty,
        WatchProcessName = UseProgramCheck.IsChecked == true
            ? WindowsProcessService.NormalizeProcessName(WatchProcessText.Text)
            : string.Empty,
        UseDiscordIntegration = UseDiscordCheck.IsChecked == true,
        DiscordExecutablePath = _appSettings.DiscordExecutablePath,
        DiscordProcessName = _appSettings.DiscordProcessName,
        TargetAudioEndpointId = UseAudioCheck.IsChecked == true
            ? AudioEndpointCombo.SelectedValue as string ?? string.Empty
            : string.Empty,
        DiscordApiBaseUrl = _appSettings.DiscordApiBaseUrl,
        AutoJoinVoiceChannel = AutoJoinVoiceCheck.IsChecked == true,
        VoiceChannelTarget = VoiceChannelCombo.SelectedValue as string
            ?? VoiceChannelTargetText.Text.Trim(),
        VoiceChannelGuildId = GuildCombo.SelectedValue as string ?? _automation.VoiceChannelGuildId,
        DiscordRpcClientId = _appSettings.DiscordRpcClientId,
        RestoreAudioOnExit = RestoreDeviceRadio.IsChecked == true,
        DeferRestoreWhileDiscordInVoice = DeferRestoreCheck.IsChecked == true,
        ProcessPollInterval = TimeSpan.FromSeconds(1),
        RestorePollInterval = TimeSpan.FromSeconds(5),
    };

    /// <summary>선택한 자동화가 쓰는 계정 프로필입니다. 없으면 null입니다.</summary>
    private GameAccountProfile? CurrentAccountProfile() =>
        _automation.AccountProfileId is { } id
            ? _appSettings.AccountProfiles.FirstOrDefault(profile => profile.Id == id)
            : null;

    /// <summary>
    /// 계정 로그인을 켜면 프로필이 필요합니다. 이미 있으면 그대로 쓰고, 없으면 다른
    /// 자동화가 쓰는 프로필의 경로와 프로세스 이름을 본떠 새로 만듭니다. 특정 게임
    /// 경로를 코드에 넣지 않기 위해 값은 모두 기존 설정에서 가져옵니다.
    /// </summary>
    private GameAccountProfile EnsureAccountProfile()
    {
        if (CurrentAccountProfile() is { } existing)
        {
            return existing;
        }

        var template = _appSettings.AccountProfiles.FirstOrDefault();
        var created = new GameAccountProfile
        {
            Name = string.IsNullOrWhiteSpace(_automation.Name) ? "새 계정" : _automation.Name,
            SessionFilePath = template?.SessionFilePath ?? string.Empty,
            LauncherProcessNames = [.. template?.LauncherProcessNames ?? []],
            BlockingProcessNames = [.. template?.BlockingProcessNames ?? []],
        };
        _appSettings = _appSettings with
        {
            AccountProfiles = [.. _appSettings.AccountProfiles, created],
        };
        _automation = _automation with { AccountProfileId = created.Id };
        return created;
    }

    /// <summary>등록 상태를 확인해 계정 칸에 보여 줍니다.</summary>
    private async Task RefreshAccountStatusesAsync()
    {
        if (UseAccountCheck.IsChecked != true)
        {
            AccountStatusText.Text = "사용 안 함";
            return;
        }

        if (CurrentAccountProfile() is not { } profile ||
            CreateAccountSessionService() is not { } service)
        {
            AccountStatusText.Text = "미등록";
            return;
        }

        try
        {
            var status = await service.GetStatusAsync(profile, CancellationToken.None);
            AccountStatusText.Text = status switch
            {
                GameAccountProfileStatus.Enrolled => "등록됨",
                GameAccountProfileStatus.NeedsReenrollment => "재등록 필요",
                _ => "미등록",
            };
        }
        catch (Exception exception)
        {
            AccountStatusText.Text = "확인 실패";
            _logger?.Error("account-status-failed", exception, "등록 상태를 확인하지 못했습니다.");
        }
    }

    private async void CaptureAccountSession_Click(object sender, RoutedEventArgs e)
    {
        if (CreateAccountSessionService() is not { } service)
        {
            return;
        }

        try
        {
            var profile = ReadAccountProfileFromControls(EnsureAccountProfile());
            SaveAccountProfile(profile);
            var captured = await service.CaptureAsync(profile, CancellationToken.None);
            await PersistAccountChangesAsync();
            await RefreshAccountStatusesAsync();
            MessageBox.Show(
                captured
                    ? "지금 로그인된 계정을 이 자동화에 등록했습니다."
                    : "로그인된 계정을 찾지 못했습니다. 게임 런처에 로그인한 뒤 다시 시도하세요.",
                "계정 로그인");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-capture-failed", exception, "계정 등록에 실패했습니다.");
            MessageBox.Show(exception.Message, "계정 로그인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 자동화가 실행 중일 때 계정 단축키가 쓰는 전환 경로를 그대로 부릅니다.
    /// 키 입력을 흉내 내지 않고, 오디오와 Discord도 건드리지 않습니다.
    /// </summary>
    private async void SwitchToAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null || CurrentAccountProfile() is not { } profile)
        {
            MessageBox.Show(
                "먼저 이 자동화를 저장하고 현재 로그인 계정을 등록하세요.",
                "계정 로그인");
            return;
        }

        try
        {
            var result = await _engine.SwitchAccountAsync(profile, CancellationToken.None);
            await RefreshAccountStatusesAsync();
            MessageBox.Show(
                result.Started ? "이 계정으로 전환했습니다." : result.Reason ?? "계정을 전환하지 않았습니다.",
                "계정 로그인");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-switch-failed", exception, "계정 전환에 실패했습니다.");
            MessageBox.Show(exception.Message, "계정 로그인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 다른 계정을 등록할 수 있게 로그인 화면까지 열어 줍니다. 지금 로그인된 계정은
    /// 먼저 회수해 두고, 런처의 로그아웃 명령은 쓰지 않습니다.
    /// </summary>
    private async void OpenOtherAccountSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (CreateAccountSessionService() is not { } service)
        {
            return;
        }

        var profile = CurrentAccountProfile() ?? _appSettings.AccountProfiles.FirstOrDefault();
        if (profile is null)
        {
            MessageBox.Show("먼저 현재 로그인 계정을 한 번 등록하세요.", "계정 로그인");
            return;
        }

        try
        {
            var result = await service.PrepareForNewSignInAsync(profile, CancellationToken.None);
            await RefreshAccountStatusesAsync();
            if (!result.CanContinue)
            {
                MessageBox.Show(result.Message ?? "로그인 화면을 열지 못했습니다.", "계정 로그인");
                return;
            }

            if (string.IsNullOrWhiteSpace(_automation.LaunchExecutablePath))
            {
                MessageBox.Show(
                    "로그인되지 않은 상태로 만들었습니다. 실행 파일이 없어 런처는 직접 실행해 주세요.",
                    "계정 로그인");
                return;
            }

            await _processes.StartAsync(_automation.LaunchExecutablePath, CancellationToken.None);
            MessageBox.Show(
                "로그인 화면을 열었습니다. 다른 계정으로 로그인한 뒤 그 자동화에서 현재 로그인 계정 등록을 누르세요.",
                "계정 로그인");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-signin-open-failed", exception, "로그인 화면을 열지 못했습니다.");
            MessageBox.Show(exception.Message, "계정 로그인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>고급 칸에 입력된 경로와 프로세스 이름을 프로필에 반영합니다.</summary>
    private GameAccountProfile ReadAccountProfileFromControls(GameAccountProfile profile) => profile with
    {
        Name = string.IsNullOrWhiteSpace(_automation.Name) ? profile.Name : _automation.Name,
        SessionFilePath = SessionFilePathText.Text.Trim(),
        LauncherProcessNames = SplitNames(LauncherProcessesText.Text),
        BlockingProcessNames = SplitNames(BlockingProcessesText.Text),
    };

    private static List<string> SplitNames(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void SaveAccountProfile(GameAccountProfile profile) =>
        _appSettings = _appSettings with
        {
            AccountProfiles = [.. _appSettings.AccountProfiles
                .Where(existing => existing.Id != profile.Id)
                .Append(profile)],
        };

    /// <summary>계정 프로필 변경만 조용히 저장합니다. 자동화 저장과 별개입니다.</summary>
    private async Task PersistAccountChangesAsync()
    {
        if (_settingsStore is not null)
        {
            await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
        }
    }

    /// <summary>
    /// 현재 자동화를 본떠 새 자동화를 만듭니다. 실행 환경은 복사하되 단축키와 계정
    /// 로그인은 가져오지 않습니다. 같은 단축키가 둘이 되거나 한 계정을 두 자동화가
    /// 나눠 쓰는 일을 막기 위해 꺼진 상태로 만듭니다.
    /// </summary>
    private void DuplicateAutomationRule_Click(object sender, RoutedEventArgs e)
    {
        if (_rules.Count == 0)
        {
            return;
        }

        CommitEditingRule();
        var source = _rules[_editingRule];
        _rules.Add(source with
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name} 복사본",
            Enabled = false,
            Hotkey = new HotkeyGesture(true, true, true, false, string.Empty),
            AccountProfileId = null,
        });
        RefreshRuleList(_rules.Count - 1);
        MessageBox.Show(
            "복제했습니다. 단축키를 정하고 필요하면 계정 로그인을 등록한 뒤 사용을 켜세요.",
            "자동화");
    }

    /// <summary>
    /// 서버 목록은 로컬 Discord가 알려 주고, 그중 봇도 들어가 있는 것만 남깁니다.
    /// 봇은 이름을 주고받지 않으므로 이름은 로컬에서 가져온 것을 그대로 씁니다.
    /// </summary>
    private async void RefreshDiscordLists_Click(object sender, RoutedEventArgs e) =>
        await LoadDiscordListsAsync(force: true);

    private async void GuildCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingRule || GuildCombo.SelectedValue is not string guildId)
        {
            return;
        }
        await LoadVoiceChannelsAsync(guildId);
    }

    private async Task LoadDiscordListsAsync(bool force)
    {
        if (_secretStore is null)
        {
            return;
        }

        // 화면에서 방금 켠 값을 반영한 뒤 판단한다. 저장 전이라고 꺼진 것으로 보면
        // 연동이 켜져 있는데도 켜라는 안내가 뜬다.
        CommitEditingRule();
        if (UseDiscordCheck.IsChecked != true)
        {
            DiscordListStatusText.Text = "Discord 연동을 먼저 켜세요.";
            return;
        }

        var generation = ++_discordLoadGeneration;
        var client = EnsureRpcVoiceClient(_automation);
        if (client is null)
        {
            DiscordListStatusText.Text = "Discord 연결이 필요합니다. Discord 다시 연결을 눌러 주세요.";
            return;
        }

        DiscordListStatusText.Text = "불러오는 중...";
        try
        {
            var connection = await client.EnsureConnectedAsync(CancellationToken.None);
            if (connection.Status != DiscordRpcStatus.Connected)
            {
                DiscordListStatusText.Text = DescribeRpcFailure(connection);
                return;
            }

            if (force || _guilds.Count == 0)
            {
                var local = await client.GetGuildsAsync(CancellationToken.None);
                var token = await _secretStore.LoadDiscordApiTokenAsync(CancellationToken.None) ?? string.Empty;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var intersection = await new DiscordGuildIntersectionClient(
                        http,
                        _appSettings.DiscordApiBaseUrl,
                        token)
                    .IntersectAsync([.. local.Select(guild => guild.Id)], CancellationToken.None);

                if (intersection.Status != GuildIntersectionStatus.Ok)
                {
                    // 준비 중인 것을 "서버 없음"으로 확정하면 고를 것이 없다고 오해합니다.
                    DiscordListStatusText.Text = intersection.Error ?? "서버 목록을 불러오지 못했습니다.";
                    return;
                }

                var shared = intersection.GuildIds.ToHashSet(StringComparer.Ordinal);
                _guilds.Clear();
                _guilds.AddRange(local.Where(guild => shared.Contains(guild.Id)));
                _voiceChannels.Clear();
            }

            if (generation != _discordLoadGeneration)
            {
                return;
            }

            _loadingRule = true;
            GuildCombo.ItemsSource = null;
            GuildCombo.ItemsSource = _guilds;
            // 저장된 서버가 목록에 없으면 첫 서버를 골라 준다. 그러지 않으면 아무것도
            // 선택되지 않아 채널이 비어 보이고, 한 번 더 눌러야 나타난다.
            var saved = _automation.VoiceChannelGuildId;
            var target = _guilds.Any(guild => guild.Id == saved) ? saved : _guilds.FirstOrDefault()?.Id;
            GuildCombo.SelectedValue = target;
            _loadingRule = false;

            DiscordListStatusText.Text = _guilds.Count == 0
                ? "OneKey 봇과 함께 있는 서버가 없습니다. 봇을 서버에 초대한 뒤 다시 불러오세요."
                : $"서버 {_guilds.Count}개를 불러왔습니다.";

            if (target is not null)
            {
                await LoadVoiceChannelsAsync(target);
            }
        }
        catch (Exception exception)
        {
            _logger?.Error("discord-lists-failed", exception, "Discord 목록을 불러오지 못했습니다.");
            DiscordListStatusText.Text = "Discord 목록을 불러오지 못했습니다. 잠시 후 다시 불러오세요.";
        }
    }

    private async Task LoadVoiceChannelsAsync(string guildId)
    {
        var generation = _discordLoadGeneration;
        var client = EnsureRpcVoiceClient(_automation);
        if (client is null || string.IsNullOrWhiteSpace(guildId))
        {
            return;
        }

        try
        {
            if (!_voiceChannels.TryGetValue(guildId, out var channels))
            {
                channels = await client.GetVoiceChannelsAsync(guildId, CancellationToken.None);
                _voiceChannels[guildId] = channels;
            }

            if (generation != _discordLoadGeneration)
            {
                return;
            }

            var previous = _automation.VoiceChannelTarget;
            var resolved = channels.Any(channel => channel.Id == previous);
            _loadingRule = true;
            VoiceChannelCombo.ItemsSource = null;
            VoiceChannelCombo.ItemsSource = channels;
            // 못 푼 값을 두고 첫 채널을 골라 두면, 저장할 때 사용자가 정한 채널이
            // 조용히 바뀝니다. 그럴 때는 아무것도 고르지 않고 직접 입력값을 살립니다.
            VoiceChannelCombo.SelectedValue = resolved ? previous : null;
            _loadingRule = false;

            // 목록에서 풀리는 값은 드롭다운이 갖고, 풀리지 않는 값만 수동 입력에 남깁니다.
            VoiceChannelTargetText.Text = resolved || string.IsNullOrWhiteSpace(previous)
                ? string.Empty
                : previous;

            if (channels.Count == 0)
            {
                DiscordListStatusText.Text = "이 서버에서 볼 수 있는 음성채널이 없습니다.";
            }
            else if (!resolved && !string.IsNullOrWhiteSpace(previous))
            {
                DiscordListStatusText.Text =
                    "저장해 둔 음성채널을 목록에서 찾지 못해 직접 입력값으로 남겼습니다.";
            }
        }
        catch (Exception exception)
        {
            _logger?.Error("discord-channels-failed", exception, "음성채널 목록을 불러오지 못했습니다.");
            DiscordListStatusText.Text = "음성채널 목록을 불러오지 못했습니다. 잠시 후 다시 불러오세요.";
        }
    }

    /// <summary>
    /// 아직 테스트 사용자로 등록되지 않은 계정은 Discord가 승인을 거부합니다.
    /// 기술 오류 대신 무엇을 해야 하는지 알려 줍니다.
    /// </summary>
    private static string DescribeRpcFailure(DiscordRpcConnection connection) =>
        connection.Status == DiscordRpcStatus.NotAuthorized
            ? "이 Discord 계정은 아직 OneKey 테스트 사용자로 등록되지 않았습니다. 앱 관리자에게 테스트 사용자 등록을 요청하세요."
            : connection.Error ?? "Discord에 연결하지 못했습니다. Discord를 실행한 뒤 다시 시도하세요.";

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_initializationFailed || _settingsStore is null || _secretStore is null)
        {
            return;
        }

        try
        {
            CommitEditingRule();
            ApplyGlobalsToRules();
            var existingToken = await _secretStore.LoadDiscordApiTokenAsync(CancellationToken.None);
            var suppliedToken = ApiTokenPassword.Password;
            foreach (var rule in _rules)
            {
                ValidateSettings(rule, suppliedToken, existingToken);
            }
            EnsureHotkeysAreUnique();

            if (!string.IsNullOrWhiteSpace(suppliedToken))
            {
                await _secretStore.SaveDiscordApiTokenAsync(suppliedToken, CancellationToken.None);
                ApiTokenPassword.Clear();
            }

            _appSettings = _appSettings with
            {
                SchemaVersion = SettingsMigration.CurrentSchemaVersion,
                Automations = [.. _rules],
            };
            await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
            _logger?.Info("settings-saved", "자동화 규칙을 저장하고 다시 적용했습니다.");
            await StartRuntimeAsync();
            RefreshRuleList(_editingRule);
            UpdateStatus();
            MessageBox.Show("자동화 규칙을 저장했습니다.", "eslee OneKey");
        }
        catch (Exception exception)
        {
            _logger?.Error("settings-save-failed", exception, "설정 저장에 실패했습니다.");
            MessageBox.Show(exception.Message, "설정 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 같은 조합을 두 규칙이 쓰면 Windows가 두 번째 등록을 거부합니다. 저장 전에 막습니다.
    /// </summary>
    private void EnsureHotkeysAreUnique()
    {
        var seen = new List<HotkeyGesture>();
        foreach (var rule in _rules.Where(rule => rule.Enabled))
        {
            if (seen.Contains(rule.Hotkey))
            {
                throw new InvalidOperationException(
                    $"{rule.Name}이(가) 다른 자동화와 같은 단축키를 씁니다. 서로 다른 조합을 지정하세요.");
            }
            seen.Add(rule.Hotkey);
        }
    }

    /// <summary>앱 설정 칸을 채웁니다. 어느 자동화를 보고 있든 값이 같습니다.</summary>
    private void LoadGlobalSettingsIntoControls()
    {
        StartupCheck.IsChecked = _appSettings.StartWithWindows || _startup.IsEnabled();
        RpcClientIdText.Text = _appSettings.DiscordRpcClientId;
        ApiUrlText.Text = _appSettings.DiscordApiBaseUrl;
        DiscordPathText.Text = _appSettings.DiscordExecutablePath;
        DiscordProcessText.Text = _appSettings.DiscordProcessName;
    }

    /// <summary>
    /// 전역 Discord 값을 모든 자동화에 얹습니다. 엔진은 규칙 단위로만 설정을 읽으므로,
    /// 저장 직전에 한 번 맞춰 두면 엔진을 고치지 않고도 값이 하나로 유지됩니다.
    /// </summary>
    private void ApplyGlobalsToRules()
    {
        for (var index = 0; index < _rules.Count; index++)
        {
            _rules[index] = _rules[index] with
            {
                DiscordRpcClientId = _appSettings.DiscordRpcClientId,
                DiscordApiBaseUrl = _appSettings.DiscordApiBaseUrl,
                DiscordExecutablePath = _appSettings.DiscordExecutablePath,
                DiscordProcessName = _appSettings.DiscordProcessName,
            };
        }
        if (_editingRule >= 0 && _editingRule < _rules.Count)
        {
            _automation = _rules[_editingRule];
        }
    }

    private async void SaveAppSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_initializationFailed || _settingsStore is null)
        {
            return;
        }

        try
        {
            var suppliedToken = ApiTokenPassword.Password;
            if (!string.IsNullOrWhiteSpace(suppliedToken) && _secretStore is not null)
            {
                await _secretStore.SaveDiscordApiTokenAsync(suppliedToken, CancellationToken.None);
                ApiTokenPassword.Clear();
            }

            _appSettings = _appSettings with
            {
                SchemaVersion = SettingsMigration.CurrentSchemaVersion,
                StartWithWindows = StartupCheck.IsChecked == true,
                DiscordRpcClientId = RpcClientIdText.Text.Trim(),
                DiscordApiBaseUrl = ApiUrlText.Text.Trim(),
                DiscordExecutablePath = DiscordPathText.Text.Trim(),
                DiscordProcessName = WindowsProcessService.NormalizeProcessName(DiscordProcessText.Text),
            };
            // 엔진은 규칙 단위로만 설정을 읽으므로 전역 값을 규칙에 얹어 저장한다.
            ApplyGlobalsToRules();
            _appSettings = _appSettings with { Automations = [.. _rules] };
            await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
            _startup.SetEnabled(
                _appSettings.StartWithWindows,
                Environment.ProcessPath ?? throw new InvalidOperationException("현재 실행 경로를 찾을 수 없습니다."));
            _logger?.Info("app-settings-saved", "앱 설정을 저장했습니다.");
            await StartRuntimeAsync();
            UpdateStatus();
            MessageBox.Show("앱 설정을 저장했습니다.", "eslee OneKey");
        }
        catch (Exception exception)
        {
            _logger?.Error("app-settings-save-failed", exception, "앱 설정 저장에 실패했습니다.");
            MessageBox.Show(exception.Message, "설정 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ValidateSettings(
        AutomationSettings settings,
        string suppliedToken,
        string? existingToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            throw new InvalidOperationException("자동화 이름을 입력하세요.");
        }
        if (string.IsNullOrWhiteSpace(settings.Hotkey.Key))
        {
            throw new InvalidOperationException("단축키를 입력하세요.");
        }
        if (!settings.Enabled)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.WatchProcessName))
        {
            throw new InvalidOperationException("종료 감시 프로세스명을 입력하세요.");
        }
        if (string.IsNullOrWhiteSpace(settings.LaunchExecutablePath))
        {
            throw new InvalidOperationException("실행 파일을 선택하세요.");
        }
        if (string.IsNullOrWhiteSpace(settings.TargetAudioEndpointId))
        {
            throw new InvalidOperationException("전환할 헤드셋을 선택하세요.");
        }
        // 자동 입장은 Discord 연동을 켠 경우에만 동작하므로 그때만 값을 요구한다.
        if (settings.UseDiscordIntegration && settings.AutoJoinVoiceChannel)
        {
            if (!DiscordChannelTarget.TryParse(settings.VoiceChannelTarget, out _))
            {
                throw new InvalidOperationException(
                    "음성채널 링크 또는 Channel ID를 올바르게 입력하세요.");
            }
        }

    }

    private async void StartNow_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            MessageBox.Show("자동화가 비활성화되었거나 아직 준비되지 않았습니다.", "eslee OneKey");
            return;
        }
        await _engine.StartAsync(AutomationTrigger.Hotkey);
        UpdateStatus();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePaused();

    private async void ManualRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            await _engine.ManualRestoreAsync();
            UpdateStatus();
        }
    }

    private async void KeepCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            await _engine.KeepCurrentAndStopAsync();
            UpdateStatus();
        }
    }

    private async void RefreshAudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAudioEndpointsAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "오디오 장치 조회 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseLaunch_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutable(LaunchPathText, WatchProcessText);

    private void BrowseDiscord_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutable(DiscordPathText, DiscordProcessText);

    private static void BrowseExecutable(
        System.Windows.Controls.TextBox pathText,
        System.Windows.Controls.TextBox processText)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            pathText.Text = dialog.FileName;
            // 실행 파일과 감시 프로세스는 서로 다를 수 있다(예: 런처 실행 +
            // 게임 프로세스 감시). 사용자가 입력해 둔 값을 덮어쓰지 않는다.
            if (string.IsNullOrWhiteSpace(processText.Text))
            {
                processText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        if (_secretStore is null)
        {
            return;
        }
        var token = ApiTokenPassword.Password;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = await _secretStore.LoadDiscordApiTokenAsync(CancellationToken.None) ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show("먼저 API Token을 입력하세요.", "Discord API");
            return;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var result = await new DiscordVoiceStatusClient(client, _appSettings.DiscordApiBaseUrl, token)
            .CheckAsync(CancellationToken.None);
        MessageBox.Show(
            result.State switch
            {
                DiscordVoiceState.InVoice => "연결 성공: 대상 사용자가 음성채널에 있습니다.",
                DiscordVoiceState.NotInVoice => "연결 성공: 대상 사용자가 음성채널에 없습니다.",
                _ => result.Error ?? "연결 확인에 실패했습니다.",
            },
            "Discord API 연결 확인");
    }

    private void Engine_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateStatus);

    private void UpdateStatus()
    {
        if (_initializationFailed)
        {
            return;
        }
        var state = _engine?.State ?? AutomationState.Idle;
        var stateText = AutomationStatusText.ForState(
            state,
            WaitsForDiscordVoice,
            _engine?.KeptCurrentDevice ?? false);
        GlobalStateText.Text = _paused ? $"일시정지 · {stateText}" : stateText;
        LastRunStatus.Text = _engine?.LastRunAt is { } run
            ? $"마지막 실행 {run.ToLocalTime():HH:mm:ss}"
            : string.Empty;

        var error = _engine?.LastError ?? _coordinator?.LastError;
        LastErrorStatus.Text = error ?? string.Empty;
        LastErrorStatus.Visibility = Visible(!string.IsNullOrWhiteSpace(error));

        // 실행 중이거나 복원을 기다릴 때만 비상용 수동 제어를 보여 줍니다.
        var running = state is AutomationState.Active
            or AutomationState.Starting
            or AutomationState.RestorePending
            or AutomationState.Restoring;
        ManualRestoreButton.Visibility = Visible(running);
        KeepCurrentButton.Visibility = Visible(running);

        // 지금 돌고 있는 자동화만 목록에서 상태를 보여 줍니다.
        var activeId = _engine?.ActiveRule.Id;
        foreach (var item in _ruleItems)
        {
            item.StateText = !item.Enabled
                ? "사용 안 함"
                : item.Id == activeId && running ? stateText : "대기 중";
        }
        _tray?.SetRestorePending(state == AutomationState.RestorePending);
    }

    private void TogglePaused()
    {
        _paused = !_paused;
        _coordinator?.SetPaused(_paused);
        PauseButton.Content = _paused ? "자동화 다시 시작" : "자동화 일시정지";
        _tray?.SetPaused(_paused);
        UpdateStatus();
    }

    private bool WaitsForDiscordVoice =>
        _automation.RestoreAudioOnExit &&
        _automation.UseDiscordIntegration &&
        _automation.DeferRestoreWhileDiscordInVoice;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }
        // X 버튼은 알림 없이 조용히 트레이로 숨긴다. 완전 종료는 트레이 메뉴에서 한다.
        e.Cancel = true;
        Hide();
    }

    public void OpenFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    public void TogglePausedFromTray() => Dispatcher.Invoke(TogglePaused);

    /// <summary>
    /// Tray Folder 메뉴에서 클릭된 항목을 실행합니다. 대화 상자를 띄우는 항목이
    /// 파이프 응답을 막지 않도록 실행은 UI 큐에 넘기고, 알려진 항목인지 여부만
    /// 즉시 돌려줍니다.
    /// </summary>
    private bool TryStartTrayFolderMenuAction(string actionId)
    {
        switch (actionId)
        {
            case OneKeyTrayFolderMenu.OpenAppActionId:
                _ = Dispatcher.InvokeAsync(OpenFromTray);
                return true;
            case OneKeyTrayFolderMenu.TogglePauseActionId:
                _ = Dispatcher.InvokeAsync(TogglePaused);
                return true;
            case OneKeyTrayFolderMenu.ShowStatusActionId:
                _ = Dispatcher.InvokeAsync(ShowCurrentStatus);
                return true;
            case OneKeyTrayFolderMenu.ExitActionId:
                _ = Dispatcher.InvokeAsync(ExitApplication);
                return true;
            default:
                return false;
        }
    }

    public void ShowCurrentStatus() => Dispatcher.Invoke(() =>
        MessageBox.Show(
            $"상태: {AutomationStatusText.ForState(_engine?.State ?? AutomationState.Idle, WaitsForDiscordVoice, _engine?.KeptCurrentDevice ?? false)}\n" +
            $"복원 대기: {_engine?.RestorePending ?? false}\n" +
            $"오류: {_engine?.LastError ?? _coordinator?.LastError ?? "없음"}",
            "eslee OneKey 현재 상태"));

    public async void ExitApplication()
    {
        if (_shuttingDown)
        {
            return;
        }
        _shuttingDown = true;
        _allowClose = true;
        await ShutdownRuntimeAsync();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task ShutdownRuntimeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
            _coordinator = null;
        }
        if (_engine is not null)
        {
            _engine.StateChanged -= Engine_StateChanged;
            await _engine.DisposeAsync();
            _engine = null;
        }
        if (_rpcVoiceClient is not null)
        {
            await _rpcVoiceClient.DisposeAsync();
            _rpcVoiceClient = null;
        }
        _updateTimer?.Stop();
        _updateTimer = null;
        _updateHttpClient?.Dispose();
        _updateHttpClient = null;
        _updateChecker = null;
        _logger?.Info("app-stop", "eslee OneKey를 종료했습니다.");
        _trayFolderLink?.Dispose();
        _trayFolderLink = null;
        _tray?.Dispose();
        _tray = null;
    }
}
