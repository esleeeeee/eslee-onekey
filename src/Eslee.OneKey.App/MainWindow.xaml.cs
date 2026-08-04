using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private AutomationEngine? _engine;
    private AutomationCoordinator? _coordinator;
    private HttpClient? _httpClient;
    private TrayIconService? _tray;
    private bool _paused;
    private bool _allowClose;
    private bool _shuttingDown;
    private bool _initializationFailed;
    private bool _initializationErrorShown;

    public MainWindow(bool startMinimized)
    {
        _startMinimized = startMinimized;
        InitializeComponent();
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

            _appSettings = await _settingsStore.LoadAsync(CancellationToken.None);
            _automation = _appSettings.Automations.FirstOrDefault() ?? new AutomationSettings();
            ApplySettingsToControls();
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
        _httpClient?.Dispose();
        _httpClient = null;
        _engine = null;

        if (!_automation.Enabled ||
            _sessionStore is null ||
            _secretStore is null ||
            _logger is null)
        {
            return;
        }

        var apiToken = await _secretStore.LoadDiscordApiTokenAsync(CancellationToken.None) ?? string.Empty;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var voiceClient = new DiscordVoiceStatusClient(
            _httpClient,
            _automation.DiscordApiBaseUrl,
            apiToken);
        _engine = new AutomationEngine(
            _automation,
            _audio,
            _processes,
            voiceClient,
            _sessionStore,
            new SystemClock(),
            _logger);
        _engine.StateChanged += Engine_StateChanged;

        var windowHandle = new WindowInteropHelper(this).Handle;
        var hotkey = new WindowsGlobalHotkeyService(windowHandle);
        var monitor = new PollingProcessMonitor();
        _coordinator = new AutomationCoordinator(_automation, _engine, hotkey, monitor);
        await _coordinator.InitializeAsync();
        if (_coordinator.LastError is not null)
        {
            _tray?.ShowBalloon("전역 단축키 등록 실패", _coordinator.LastError);
        }
    }

    private void ApplySettingsToControls()
    {
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
        DeferRestoreCheck.IsChecked = _automation.DeferRestoreWhileDiscordInVoice;
        StartupCheck.IsChecked = _appSettings.StartWithWindows || _startup.IsEnabled();
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
        BringDiscordToFront = true,
        TargetAudioEndpointId = AudioEndpointCombo.SelectedValue as string ?? string.Empty,
        DiscordApiBaseUrl = ApiUrlText.Text.Trim(),
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
            var candidate = ReadAutomationFromControls();
            var existingToken = await _secretStore.LoadDiscordApiTokenAsync(CancellationToken.None);
            var suppliedToken = ApiTokenPassword.Password;
            ValidateSettings(candidate, suppliedToken, existingToken);

            if (!string.IsNullOrWhiteSpace(suppliedToken))
            {
                await _secretStore.SaveDiscordApiTokenAsync(suppliedToken, CancellationToken.None);
                ApiTokenPassword.Clear();
            }

            _automation = candidate;
            _appSettings = new AppSettings
            {
                SchemaVersion = SettingsMigration.CurrentSchemaVersion,
                StartWithWindows = StartupCheck.IsChecked == true,
                Automations = [candidate],
            };
            await _settingsStore.SaveAsync(_appSettings, CancellationToken.None);
            _startup.SetEnabled(
                _appSettings.StartWithWindows,
                Environment.ProcessPath ?? throw new InvalidOperationException("현재 실행 경로를 찾을 수 없습니다."));
            _logger?.Info("settings-saved", "설정을 저장하고 자동화를 다시 적용했습니다.");
            await StartRuntimeAsync();
            UpdateStatus();
            MessageBox.Show("설정을 저장했습니다.", "eslee OneKey");
        }
        catch (Exception exception)
        {
            _logger?.Error("settings-save-failed", exception, "설정 저장에 실패했습니다.");
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
        if (settings.UseDiscordIntegration && settings.DeferRestoreWhileDiscordInVoice)
        {
            if (!DiscordVoiceStatusClient.TryBuildEndpoint(settings.DiscordApiBaseUrl, out _))
            {
                throw new InvalidOperationException("Discord API URL을 올바르게 입력하세요.");
            }
            if (string.IsNullOrWhiteSpace(suppliedToken) && string.IsNullOrWhiteSpace(existingToken))
            {
                throw new InvalidOperationException("Discord API Token을 입력하세요.");
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
        StateStatus.Text = AutomationStatusText.ForState(state, WaitsForDiscordVoice);
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
        _automation.UseDiscordIntegration && _automation.DeferRestoreWhileDiscordInVoice;

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

    public void ShowCurrentStatus() => Dispatcher.Invoke(() =>
        MessageBox.Show(
            $"상태: {AutomationStatusText.ForState(_engine?.State ?? AutomationState.Idle, WaitsForDiscordVoice)}\n" +
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
        _httpClient?.Dispose();
        _httpClient = null;
        _logger?.Info("app-stop", "eslee OneKey를 종료했습니다.");
        _tray?.Dispose();
        _tray = null;
    }
}
