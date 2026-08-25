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
    private readonly ObservableCollection<AccountProfileViewModel> _accountProfiles = [];
    private JsonSettingsStore? _settingsStore;
    private JsonSessionStore? _sessionStore;
    private DpapiSecretStore? _secretStore;
    private FileAppLogger? _logger;
    private AppSettings _appSettings = new();
    private AutomationSettings _automation = new();

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
        EnabledStatus.Text = "초기화 실패";
        StateStatus.Text = "초기화 실패";
        LastErrorStatus.Text = $"{summary} 로그: {_paths.LogFile}";
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

    private void LoadAccountProfilesIntoControls()
    {
        _accountProfiles.Clear();
        foreach (var profile in _appSettings.AccountProfiles)
        {
            _accountProfiles.Add(new AccountProfileViewModel(profile));
        }
        AccountProfileList.ItemsSource = _accountProfiles;
    }

    private static AccountProfileViewModel? ProfileOf(object sender) =>
        (sender as FrameworkElement)?.Tag as AccountProfileViewModel;

    /// <summary>등록 상태는 저장된 세션 유무와 런처의 거부 여부로 결정됩니다.</summary>
    private async Task RefreshAccountStatusesAsync()
    {
        var service = CreateAccountSessionService();
        if (service is null)
        {
            return;
        }

        foreach (var viewModel in _accountProfiles)
        {
            try
            {
                var status = await service.GetStatusAsync(viewModel.ToProfile(), CancellationToken.None);
                viewModel.Status = AccountProfileViewModel.Describe(status);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                viewModel.Status = "확인 실패";
            }
        }
    }

    private async void RefreshAccountStatus_Click(object sender, RoutedEventArgs e) =>
        await RefreshAccountStatusesAsync();

    private void AddAccountProfile_Click(object sender, RoutedEventArgs e)
    {
        var template = _accountProfiles.LastOrDefault();
        _accountProfiles.Add(new AccountProfileViewModel
        {
            Name = "새 계정",
            SessionFilePath = template?.SessionFilePath ?? string.Empty,
            LauncherProcesses = template?.LauncherProcesses ?? string.Empty,
            BlockingProcesses = template?.BlockingProcesses ?? string.Empty,
            Status = "미등록",
        });
    }

    private void BrowseSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "런처 로그인 세션 파일 선택",
            Filter = "설정 파일 (*.yaml;*.yml;*.json;*.dat)|*.yaml;*.yml;*.json;*.dat|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SessionFilePath = dialog.FileName;
        }
    }

    private async void CaptureAccountSession_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } viewModel ||
            CreateAccountSessionService() is not { } service)
        {
            return;
        }

        try
        {
            var captured = await service.CaptureAsync(viewModel.ToProfile(), CancellationToken.None);
            await RefreshAccountStatusesAsync();
            MessageBox.Show(
                captured
                    ? $"지금 로그인된 계정을 {viewModel.Name} 프로필에 등록했습니다."
                    : "로그인된 계정을 찾지 못했습니다. 런처에 로그인한 뒤 다시 시도하세요.",
                "계정 프로필");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-capture-failed", exception, "세션 저장에 실패했습니다.");
            MessageBox.Show(exception.Message, "계정 프로필", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 자동화가 실행 중일 때 계정 단축키가 쓰는 전환 경로를 그대로 부릅니다.
    /// 키 입력을 흉내 내지 않고, 오디오와 Discord도 건드리지 않습니다.
    /// </summary>
    private async void SwitchToAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } viewModel)
        {
            return;
        }

        if (_engine is null || SavedProfile(viewModel.Id) is not { } profile)
        {
            MessageBox.Show(
                "먼저 계정 설정 저장을 눌러 이 프로필을 저장하세요.",
                "계정 프로필");
            return;
        }

        try
        {
            // 오디오나 Discord는 건드리지 않는다. 계정만 바꾼다.
            var result = await _engine.SwitchAccountAsync(profile, CancellationToken.None);
            await RefreshAccountStatusesAsync();
            MessageBox.Show(
                result.Started
                    ? $"{profile.Name} 계정으로 전환했습니다."
                    : result.Reason ?? "계정을 전환하지 않았습니다.",
                "계정 프로필");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-switch-failed", exception, "계정 전환에 실패했습니다.");
            MessageBox.Show(exception.Message, "계정 프로필", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 다른 계정을 등록할 수 있게 로그인 화면까지 열어 줍니다. 지금 로그인된 계정은
    /// 먼저 회수해 두고, 런처의 로그아웃 명령은 쓰지 않습니다. 서버가 세션을 폐기하면
    /// 이미 등록해 둔 다른 계정의 저장본까지 무효가 되기 때문입니다.
    /// </summary>
    private async void OpenOtherAccountSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (CreateAccountSessionService() is not { } service)
        {
            return;
        }

        if (_appSettings.AccountProfiles.FirstOrDefault() is not { } profile)
        {
            MessageBox.Show(
                "먼저 계정 프로필을 하나 만들고 계정 설정 저장을 누르세요.",
                "계정 프로필");
            return;
        }

        try
        {
            var result = await service.PrepareForNewSignInAsync(profile, CancellationToken.None);
            await RefreshAccountStatusesAsync();
            if (!result.CanContinue)
            {
                MessageBox.Show(result.Message ?? "로그인 화면을 열지 못했습니다.", "계정 프로필");
                return;
            }

            if (string.IsNullOrWhiteSpace(_automation.LaunchExecutablePath))
            {
                MessageBox.Show(
                    "로그인되지 않은 상태로 만들었습니다. 설정 탭에 실행 파일이 없어 런처는 직접 실행해 주세요.",
                    "계정 프로필");
                return;
            }

            await _processes.StartAsync(_automation.LaunchExecutablePath, CancellationToken.None);
            MessageBox.Show(
                "로그인 화면을 열었습니다. 다음 계정으로 로그인한 뒤 그 프로필에서 현재 로그인 계정 등록을 누르세요.",
                "계정 프로필");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-signin-open-failed", exception, "로그인 화면을 열지 못했습니다.");
            MessageBox.Show(exception.Message, "계정 프로필", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>저장된 프로필을 씁니다. 편집 중인 값이 아니라 실제로 저장된 설정입니다.</summary>
    private GameAccountProfile? SavedProfile(Guid id) =>
        _appSettings.AccountProfiles.FirstOrDefault(profile => profile.Id == id);

    private async void DeleteAccountProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } viewModel)
        {
            return;
        }

        if (MessageBox.Show(
                $"{viewModel.Name} 프로필과 저장된 로그인 세션을 삭제할까요?",
                "계정 프로필",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        if (CreateAccountSessionService() is { } service)
        {
            await service.ForgetAsync(viewModel.Id, CancellationToken.None);
        }
        _accountProfiles.Remove(viewModel);
    }

    private async void SaveAccountProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_initializationFailed || _settingsStore is null)
        {
            return;
        }

        try
        {
            var profiles = _accountProfiles.Select(item => item.ToProfile()).ToList();
            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    throw new InvalidOperationException("계정 프로필 이름을 입력하세요.");
                }
                if (string.IsNullOrWhiteSpace(profile.SessionFilePath))
                {
                    throw new InvalidOperationException($"{profile.Name}의 세션 파일을 지정하세요.");
                }
            }

            _appSettings = _appSettings with { AccountProfiles = profiles };
            await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
            _logger?.Info("account-profiles-saved", "계정 프로필을 저장했습니다.");
            await StartRuntimeAsync();
            AccountProfileCombo.ItemsSource = _appSettings.AccountProfiles;
            AccountProfileCombo.SelectedValue = _automation.AccountProfileId;
            await RefreshAccountStatusesAsync();
            MessageBox.Show("계정 프로필을 저장했습니다.", "계정 프로필");
        }
        catch (Exception exception)
        {
            _logger?.Error("account-profiles-save-failed", exception, "계정 프로필 저장에 실패했습니다.");
            MessageBox.Show(exception.Message, "계정 프로필", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 규칙마다 전역 단축키를 하나씩 답니다. 규칙이 계정 프로필을 지정했으면 그 계정으로
    /// 시작하고, 지정하지 않았으면 계정을 건드리지 않습니다.
    /// </summary>
    private IReadOnlyList<AutomationCoordinator.AutomationRuleBinding> CreateRuleBindings(nint windowHandle) =>
        _rules
            .Where(rule => rule.Enabled)
            .Select(rule => new AutomationCoordinator.AutomationRuleBinding(
                rule,
                new WindowsGlobalHotkeyService(windowHandle),
                rule.AccountProfileId is { } id ? SavedProfile(id) : null))
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
        if (!rule.UseDiscordIntegration ||
            string.IsNullOrWhiteSpace(rule.DiscordRpcClientId) ||
            _logger is null)
        {
            return null;
        }

        _rpcVoiceClient ??= new DiscordRpcVoiceChannelClient(
            rule.DiscordRpcClientId,
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
                _automation.DiscordRpcClientId,
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
            var suppliedSecret = RpcClientSecretPassword.Password;
            var storedSecret = (await LoadRpcTokensAsync(CancellationToken.None))?.ClientSecret;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var (result, tokens) = await new DiscordRpcAuthorizer(client).AuthorizeAsync(
                RpcClientIdText.Text.Trim(),
                string.IsNullOrWhiteSpace(suppliedSecret) ? storedSecret : suppliedSecret,
                CancellationToken.None);

            if (result.Succeeded && tokens is not null)
            {
                await _secretStore.SaveRpcSecretsAsync(
                    System.Text.Json.JsonSerializer.Serialize(tokens),
                    CancellationToken.None);
                RpcClientSecretPassword.Clear();
                RpcStatusText.Text = "연결됨";
                _logger?.Info("rpc-connected", "Discord RPC 인증을 완료했습니다.");
            }
            else
            {
                RpcStatusText.Text = "연결 실패";
                _logger?.Warning("rpc-connect-failed", "Discord RPC 인증에 실패했습니다.");
            }
            MessageBox.Show(result.Message, "Discord 연결");
        }
        catch (Exception exception)
        {
            RpcStatusText.Text = "연결 실패";
            _logger?.Error("rpc-connect-failed", exception, "Discord RPC 인증 중 오류가 발생했습니다.");
            MessageBox.Show(exception.Message, "Discord 연결", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectRpcButton.IsEnabled = true;
        }
    }

    private void ApplySettingsToControls()
    {
        LoadAccountProfilesIntoControls();
        RefreshRuleCombo(_rules.IndexOf(_automation) is var found && found >= 0 ? found : 0);
        StartupCheck.IsChecked = _appSettings.StartWithWindows || _startup.IsEnabled();
    }

    /// <summary>규칙 목록을 다시 그리고 지정한 규칙을 폼에 올립니다.</summary>
    private void RefreshRuleCombo(int index)
    {
        _loadingRule = true;
        AutomationRuleCombo.ItemsSource = null;
        AutomationRuleCombo.ItemsSource = _rules;
        _editingRule = Math.Clamp(index, 0, Math.Max(0, _rules.Count - 1));
        AutomationRuleCombo.SelectedIndex = _rules.Count == 0 ? -1 : _editingRule;
        _loadingRule = false;
        if (_rules.Count > 0)
        {
            LoadRuleIntoControls(_rules[_editingRule]);
        }
    }

    private void AutomationRule_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingRule || AutomationRuleCombo.SelectedIndex < 0)
        {
            return;
        }

        // 편집하던 값을 잃지 않도록 먼저 되받아 둔다. 저장은 저장 버튼이 한다.
        CommitEditingRule();
        _editingRule = AutomationRuleCombo.SelectedIndex;
        LoadRuleIntoControls(_rules[_editingRule]);
    }

    /// <summary>폼에 입력된 값을 편집 중이던 규칙에 되받습니다.</summary>
    private void CommitEditingRule()
    {
        if (_editingRule >= 0 && _editingRule < _rules.Count)
        {
            _rules[_editingRule] = ReadAutomationFromControls();
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
        RefreshRuleCombo(_rules.Count - 1);
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
        RefreshRuleCombo(Math.Max(0, _editingRule - 1));
    }

    private void ClearAccountProfile_Click(object sender, RoutedEventArgs e) =>
        AccountProfileCombo.SelectedValue = null;

    private void LoadRuleIntoControls(AutomationSettings rule)
    {
        _automation = rule;
        AutomationNameText.Text = _automation.Name;
        EnabledCheck.IsChecked = _automation.Enabled;
        CtrlCheck.IsChecked = _automation.Hotkey.Control;
        AltCheck.IsChecked = _automation.Hotkey.Alt;
        ShiftCheck.IsChecked = _automation.Hotkey.Shift;
        WinCheck.IsChecked = _automation.Hotkey.Windows;
        HotkeyText.Text = _automation.Hotkey.Key;
        LaunchPathText.Text = _automation.LaunchExecutablePath;
        WatchProcessText.Text = _automation.WatchProcessName;
        UseDiscordCheck.IsChecked = _automation.UseDiscordIntegration;
        DiscordPathText.Text = _automation.DiscordExecutablePath;
        DiscordProcessText.Text = _automation.DiscordProcessName;
        ApiUrlText.Text = _automation.DiscordApiBaseUrl;
        AccountProfileCombo.ItemsSource = _appSettings.AccountProfiles;
        AccountProfileCombo.SelectedValue = _automation.AccountProfileId;
        AutoJoinVoiceCheck.IsChecked = _automation.AutoJoinVoiceChannel;
        VoiceChannelTargetText.Text = _automation.VoiceChannelTarget;
        RpcClientIdText.Text = _automation.DiscordRpcClientId;
        RestoreAudioCheck.IsChecked = _automation.RestoreAudioOnExit;
        DeferRestoreCheck.IsChecked = _automation.DeferRestoreWhileDiscordInVoice;
        UpdateRestorePolicyEnabled();
    }

    /// <summary>복원 보류는 자동 복원과 Discord 연동을 모두 켠 경우에만 의미가 있습니다.</summary>
    private void UpdateRestorePolicyEnabled() =>
        DeferRestoreCheck.IsEnabled =
            RestoreAudioCheck.IsChecked == true && UseDiscordCheck.IsChecked == true;

    private void RestoreAudioCheck_Changed(object sender, RoutedEventArgs e) =>
        UpdateRestorePolicyEnabled();

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
        AccountProfileId = AccountProfileCombo.SelectedValue as Guid?,
        Enabled = EnabledCheck.IsChecked == true,
        Hotkey = new HotkeyGesture(
            CtrlCheck.IsChecked == true,
            AltCheck.IsChecked == true,
            ShiftCheck.IsChecked == true,
            WinCheck.IsChecked == true,
            HotkeyText.Text.Trim()),
        LaunchExecutablePath = LaunchPathText.Text.Trim(),
        WatchProcessName = WindowsProcessService.NormalizeProcessName(WatchProcessText.Text),
        UseDiscordIntegration = UseDiscordCheck.IsChecked == true,
        DiscordExecutablePath = DiscordPathText.Text.Trim(),
        DiscordProcessName = WindowsProcessService.NormalizeProcessName(DiscordProcessText.Text),
        TargetAudioEndpointId = AudioEndpointCombo.SelectedValue as string ?? string.Empty,
        DiscordApiBaseUrl = ApiUrlText.Text.Trim(),
        AutoJoinVoiceChannel = AutoJoinVoiceCheck.IsChecked == true,
        VoiceChannelTarget = VoiceChannelTargetText.Text.Trim(),
        DiscordRpcClientId = RpcClientIdText.Text.Trim(),
        RestoreAudioOnExit = RestoreAudioCheck.IsChecked == true,
        DeferRestoreWhileDiscordInVoice = DeferRestoreCheck.IsChecked == true,
        ProcessPollInterval = TimeSpan.FromSeconds(1),
        RestorePollInterval = TimeSpan.FromSeconds(5),
    };

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_initializationFailed || _settingsStore is null || _secretStore is null)
        {
            return;
        }

        try
        {
            CommitEditingRule();
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
            RefreshRuleCombo(_editingRule);
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

    private async void SaveAppSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_initializationFailed || _settingsStore is null)
        {
            return;
        }

        try
        {
            _appSettings = _appSettings with { StartWithWindows = StartupCheck.IsChecked == true };
            await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
            _startup.SetEnabled(
                _appSettings.StartWithWindows,
                Environment.ProcessPath ?? throw new InvalidOperationException("현재 실행 경로를 찾을 수 없습니다."));
            _logger?.Info("app-settings-saved", "앱 설정을 저장했습니다.");
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
            if (string.IsNullOrWhiteSpace(settings.DiscordRpcClientId))
            {
                throw new InvalidOperationException("RPC Client ID를 입력하세요.");
            }
        }

        // 복원 보류는 이제 로컬 Discord에 직접 묻는다. 그래서 API URL이나 Token이 아니라
        // RPC Client ID가 있어야 한다. 없으면 매번 확인에 실패해 오디오가 되돌아오지
        // 않는다. 복원 대기에는 시한이 없기 때문이다.
        if (settings.UseDiscordIntegration &&
            settings.RestoreAudioOnExit &&
            settings.DeferRestoreWhileDiscordInVoice &&
            string.IsNullOrWhiteSpace(settings.DiscordRpcClientId))
        {
            throw new InvalidOperationException(
                "통화 중 복원 보류를 쓰려면 RPC Client ID를 입력하고 Discord 연결을 수행하세요.");
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
        var result = await new DiscordVoiceStatusClient(client, ApiUrlText.Text.Trim(), token)
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
        AutomationNameStatus.Text = _automation.Name;
        EnabledStatus.Text = _automation.Enabled ? "활성" : "비활성";
        var state = _engine?.State ?? AutomationState.Idle;
        StateStatus.Text = AutomationStatusText.ForState(
            state,
            WaitsForDiscordVoice,
            _engine?.KeptCurrentDevice ?? false);
        LastRunStatus.Text = _engine?.LastRunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "없음";
        LastErrorStatus.Text = _engine?.LastError ?? _coordinator?.LastError ?? "없음";
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
