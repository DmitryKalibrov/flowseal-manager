using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlowsealManager.Core.Infrastructure;
using FlowsealManager.Core.Models;
using FlowsealManager.Core.Services;
using Forms = System.Windows.Forms;

namespace FlowsealManager.App;

public partial class MainWindow : Window
{
    private const int MaximumVisibleLogEntries = 300;
    private const int MaximumVisibleLogEntryLength = 4000;

    private readonly bool _startMinimized;
    private readonly AppPaths _paths = new();
    private readonly SettingsStore _settingsStore;
    private readonly FileLogger _logger;
    private readonly HttpClient _downloadClient;
    private readonly HttpClient _probeClient;
    private readonly ComponentUpdater _updater;
    private readonly ComponentProcessManager _processes;
    private readonly ConnectivityProbe _probe;
    private readonly StrategySelector _selector;
    private readonly ZapretCustomizationService _zapretCustomization;
    private readonly ZapretHostsService _zapretHosts;
    private readonly LegacyServiceManager _legacyServices;
    private readonly StartupTaskManager _startup;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _statusTimer;
    private readonly Queue<string> _visibleLogEntries = new();

    private AppSettings _settings = new();
    private IReadOnlyList<LegacyServiceInfo> _legacyServiceSnapshot = [];
    private int _externalWinwsProcessCount;
    private PeriodicTimer? _monitorTimer;
    private Task? _monitorTask;
    private bool _initializing = true;
    private bool _exitRequested;
    private bool _closeHintShown;
    private int _consecutiveHealthFailures;

    public MainWindow(bool startMinimized, bool visualQa = false)
    {
        InitializeComponent();
        _startMinimized = startMinimized;
        _paths.EnsureCreated();
        _settingsStore = new SettingsStore(_paths.SettingsFile);
        _logger = new FileLogger(_paths.LogFile);
        _logger.MessageLogged += Logger_MessageLogged;

        _downloadClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _probeClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var releases = new GitHubReleaseClient(_downloadClient);
        _updater = new ComponentUpdater(_downloadClient, releases, _paths, _logger);
        _processes = new ComponentProcessManager(_paths, _logger);
        _probe = new ConnectivityProbe(_probeClient);
        _zapretCustomization = new ZapretCustomizationService();
        _zapretHosts = new ZapretHostsService(_downloadClient, _paths.HostsBackupsRoot);
        _selector = new StrategySelector(_processes, _probe, _zapretCustomization, _logger);
        _legacyServices = new LegacyServiceManager(_logger);
        _startup = new StartupTaskManager(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path."));

        _notifyIcon = CreateNotifyIcon(!visualQa);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshComponentStatus();
        if (!visualQa)
        {
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startMinimized)
        {
            Hide();
        }

        await ExecuteExclusiveAsync(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        var firstRun = !File.Exists(_paths.SettingsFile);
        _settings = await _settingsStore.LoadAsync(_lifetime.Token);
        try
        {
            if (firstRun)
            {
                await _startup.SetEnabledAsync(true, _lifetime.Token);
                _settings.StartAtLogon = true;
                await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            }
            else
            {
                _settings.StartAtLogon = await _startup.IsEnabledAsync(_lifetime.Token);
                if (_settings.StartAtLogon)
                {
                    await _startup.SetEnabledAsync(true, _lifetime.Token);
                }
            }
        }
        catch (Exception exception)
        {
            await _logger.InfoAsync($"Не удалось прочитать автозапуск: {exception.Message}");
        }

        ApplySettingsToControls();
        _initializing = false;
        await RefreshLegacyServicesAsync();

        if (_settings.CheckUpdatesOnStart)
        {
            await UpdateComponentsAsync();
        }

        RefreshStrategies();
        RefreshZapretCustomization();
        if (_settings.StartTelegramOnLaunch && HasTelegram())
        {
            await _processes.StartTelegramAsync(_settings.TelegramVersion!, _lifetime.Token);
        }

        if (_settings.StartZapretOnLaunch && HasZapret())
        {
            if (_settings.AutoSelectStrategy)
            {
                await SelectBestStrategyAsync();
            }
            else if (!string.IsNullOrWhiteSpace(_settings.SelectedStrategy))
            {
                await _processes.StartZapretAsync(
                    _settings.ZapretVersion!,
                    _settings.SelectedStrategy,
                    _lifetime.Token);
            }
        }

        RefreshComponentStatus();
        _statusTimer.Start();
        StartMonitor();
        SetGlobalStatus("Готово", true);
    }

    private async Task UpdateComponentsAsync()
    {
        SetGlobalStatus("Проверяю обновления…", null);
        await UpdateComponentAsync(ComponentKind.TelegramProxy);
        await UpdateComponentAsync(ComponentKind.Zapret);
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        RefreshStrategies();
        RefreshZapretCustomization();
        RefreshComponentStatus();
    }

    private async Task UpdateComponentAsync(ComponentKind component)
    {
        try
        {
            var current = component == ComponentKind.TelegramProxy
                ? _settings.TelegramVersion
                : _settings.ZapretVersion;
            var wasRunning = component == ComponentKind.TelegramProxy
                ? _processes.IsTelegramRunning(current)
                : _processes.IsZapretRunning(current);
            var result = await _updater.EnsureLatestAsync(component, current, _lifetime.Token);
            var versionChanged = !string.Equals(current, result.Version, StringComparison.OrdinalIgnoreCase);
            if (component == ComponentKind.TelegramProxy)
            {
                if (versionChanged && wasRunning)
                {
                    await _processes.StopTelegramAsync(current, _lifetime.Token);
                }

                _settings.TelegramVersion = result.Version;
                if (versionChanged && wasRunning)
                {
                    await _processes.StartTelegramAsync(result.Version, _lifetime.Token);
                }
            }
            else
            {
                if (versionChanged && wasRunning)
                {
                    await _processes.StopZapretAsync(_lifetime.Token);
                }

                _settings.ZapretVersion = result.Version;
                if (versionChanged && wasRunning && !string.IsNullOrWhiteSpace(_settings.SelectedStrategy))
                {
                    var strategies = _processes.GetStrategies(result.Version);
                    if (strategies.Contains(_settings.SelectedStrategy, StringComparer.OrdinalIgnoreCase))
                    {
                        await _processes.StartZapretAsync(
                            result.Version,
                            _settings.SelectedStrategy,
                            _lifetime.Token);
                    }
                }
            }

            await _logger.InfoAsync($"{component}: {result.Message}", _lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _logger.InfoAsync($"Обновление {component} пропущено: {exception.Message}");
        }
    }

    private async Task SelectBestStrategyAsync()
    {
        if (!HasZapret())
        {
            throw new InvalidOperationException("Сначала установите zapret через проверку обновлений.");
        }

        await RefreshLegacyServicesAsync();
        var blockingServices = _legacyServiceSnapshot
            .Where(service => !LegacyServiceManager.IsTransientWinDivert(service.Name))
            .ToArray();
        if (blockingServices.Length > 0)
        {
            throw new InvalidOperationException(
                "Сначала нажмите «Удалить старые службы»: " +
                string.Join(", ", blockingServices.Select(service => service.Name)));
        }

        var progress = new Progress<string>(message => SetGlobalStatus(message, null));
        var result = await _selector.SelectAsync(
            _settings.ZapretVersion!,
            _paths.ZapretDirectory(_settings.ZapretVersion!),
            _settings.SelectedStrategy,
            progress,
            _lifetime.Token);

        if (result.Strategy is null || result.Winner is null)
        {
            var best = result.Evaluations
                .OrderByDescending(item => item.BestReport.CoverageScore)
                .FirstOrDefault();
            UpdateHealth(best?.BestReport);
            throw new InvalidOperationException(
                "Все методы и параметры проверены, но устойчивых доступных контрольных соединений не найдено.");
        }

        _settings.SelectedStrategy = result.Strategy;
        _settings.SelectedCoverageScore = result.Winner.StableCoverageScore;
        _settings.LastSuccessfulCheckUtc = DateTimeOffset.UtcNow;
        _consecutiveHealthFailures = 0;
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        StrategyCombo.SelectedItem = result.Strategy;
        UpdateHealth(result.Winner.BestReport);
        await _logger.InfoAsync(
            $"Выбран лучший доступный профиль: {result.Strategy}; " +
            $"{ZapretCustomizationLabels.Summary(result.Winner.Customization)}; " +
            $"{result.Winner.StableSuccessfulChecks}/{result.Winner.BestReport.Results.Count}, " +
            $"оценка {result.Winner.StableCoverageScore}/{result.Winner.BestReport.MaximumCoverageScore}. " +
            "UDP-подмену Discord необходимо подтвердить звонком.",
            _lifetime.Token);
        RefreshZapretCustomization();
        RefreshComponentStatus();
    }

    private async Task CheckHealthAsync()
    {
        SetGlobalStatus("Проверяю доступность…", null);
        var report = await _probe.CheckAsync(_lifetime.Token);
        UpdateHealth(report);
        await _logger.InfoAsync(StrategySelector.FormatReport("Ручная проверка", report), _lifetime.Token);
        _settings.SelectedCoverageScore = report.CoverageScore;
        if (report.SuccessfulChecks > 0)
        {
            _settings.LastSuccessfulCheckUtc = DateTimeOffset.UtcNow;
        }
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        SetGlobalStatus(
            report.AllRequiredAvailable
                ? "Все контрольные соединения доступны"
                : $"Доступно {report.SuccessfulChecks} из {report.Results.Count}; сохранён текущий уровень",
            report.SuccessfulChecks > 0);
    }

    private void StartMonitor()
    {
        if (_monitorTask is not null)
        {
            return;
        }

        _monitorTimer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(_settings.MonitorIntervalMinutes, 1, 60)));
        _monitorTask = MonitorLoopAsync();
    }

    private async Task MonitorLoopAsync()
    {
        try
        {
            while (_monitorTimer is not null &&
                   await _monitorTimer.WaitForNextTickAsync(_lifetime.Token))
            {
                if (!_settings.AutoSelectStrategy ||
                    !_settings.StartZapretOnLaunch ||
                    !HasZapret() ||
                    string.IsNullOrWhiteSpace(_settings.SelectedStrategy) ||
                    !_processes.IsZapretRunning(_settings.ZapretVersion))
                {
                    continue;
                }

                var report = await _probe.CheckAsync(_lifetime.Token);
                Dispatcher.Invoke(() => UpdateHealth(report));
                var expectedScore = _settings.SelectedCoverageScore > 0
                    ? _settings.SelectedCoverageScore
                    : report.MaximumCoverageScore;
                if (report.AllRequiredAvailable || report.CoverageScore >= expectedScore)
                {
                    _consecutiveHealthFailures = 0;
                    _settings.LastSuccessfulCheckUtc = DateTimeOffset.UtcNow;
                    await _settingsStore.SaveAsync(_settings, _lifetime.Token);
                    continue;
                }

                _consecutiveHealthFailures++;
                await _logger.InfoAsync(
                    $"Мониторинг: сбой {_consecutiveHealthFailures}/{_settings.FailedChecksBeforeSwitch}.",
                    _lifetime.Token);
                if (_consecutiveHealthFailures < Math.Clamp(_settings.FailedChecksBeforeSwitch, 2, 10))
                {
                    continue;
                }

                var operation = Dispatcher.InvokeAsync(() => ExecuteExclusiveAsync(SelectBestStrategyAsync));
                await await operation;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void ApplySettingsToControls()
    {
        StartAtLogonCheck.IsChecked = _settings.StartAtLogon;
        CheckUpdatesCheck.IsChecked = _settings.CheckUpdatesOnStart;
        StartTelegramCheck.IsChecked = _settings.StartTelegramOnLaunch;
        StartZapretCheck.IsChecked = _settings.StartZapretOnLaunch;
        AutoStrategyCheck.IsChecked = _settings.AutoSelectStrategy;
        RefreshComponentStatus();
    }

    private void RefreshStrategies()
    {
        var selected = _settings.SelectedStrategy;
        StrategyCombo.ItemsSource = HasZapret()
            ? _processes.GetStrategies(_settings.ZapretVersion!)
            : [];
        if (!string.IsNullOrWhiteSpace(selected) && StrategyCombo.Items.Contains(selected))
        {
            StrategyCombo.SelectedItem = selected;
        }
        else if (StrategyCombo.Items.Count > 0)
        {
            StrategyCombo.SelectedIndex = 0;
        }
    }

    private void RefreshZapretCustomization()
    {
        GameFilterCombo.ItemsSource = new[]
        {
            "Выключен",
            "TCP и UDP",
            "Только TCP",
            "Только UDP"
        };
        IpSetModeCombo.ItemsSource = new[]
        {
            "Официальный список IP",
            "Не применять IP-диапазоны",
            "Любой IP"
        };

        if (!HasZapret())
        {
            CustomizationPanel.IsEnabled = false;
            CustomizationSummaryText.Text = "zapret не установлен";
            return;
        }

        CustomizationPanel.IsEnabled = true;
        var root = _paths.ZapretDirectory(_settings.ZapretVersion!);
        var customization = _zapretCustomization.Load(root);
        var fakeOptions = _zapretCustomization.GetFakeOptions(root);
        CustomizationSummaryText.Text = ZapretCustomizationLabels.Summary(customization);
        GameFilterCombo.SelectedIndex = (int)customization.GameFilterMode;
        IpSetModeCombo.SelectedIndex = (int)customization.IpSetMode;
        DiscordFakeCombo.ItemsSource = fakeOptions.AvailableFiles;
        GameFakeCombo.ItemsSource = fakeOptions.AvailableFiles;
        DiscordFakeCombo.SelectedItem = customization.DiscordFakeFile;
        GameFakeCombo.SelectedItem = customization.GameFakeFile;
        IncludedDomainsBox.Text = customization.IncludedDomains;
        ExcludedDomainsBox.Text = customization.ExcludedDomains;
        IncludedIpRangesBox.Text = customization.IncludedIpRanges;
        ExcludedIpRangesBox.Text = customization.ExcludedIpRanges;
    }

    private void RefreshComponentStatus()
    {
        TelegramVersionText.Text = HasTelegram() ? _settings.TelegramVersion : "не установлен";
        ZapretVersionText.Text = HasZapret() ? _settings.ZapretVersion : "не установлен";
        var telegramInstanceCount = _processes.TelegramInstanceCount();
        TelegramStatusText.Text = telegramInstanceCount > 0
            ? telegramInstanceCount == 1
                ? "● Работает в фоне · управление через Flowseal Manager"
                : $"● Найдено экземпляров: {telegramInstanceCount}; нажмите «Запустить» для нормализации"
            : "○ Остановлен";
        TelegramStatusText.Foreground = telegramInstanceCount == 1
            ? (System.Windows.Media.Brush)FindResource("SuccessOnLightBrush")
            : telegramInstanceCount > 1
                ? (System.Windows.Media.Brush)FindResource("WarningOnLightBrush")
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
        ZapretStatusText.Text = _processes.IsZapretRunning(_settings.ZapretVersion)
            ? $"● Активен: {_settings.SelectedStrategy ?? "профиль zapret"}"
            : "○ Остановлен";
        ZapretStatusText.Foreground = _processes.IsZapretRunning(_settings.ZapretVersion)
            ? (System.Windows.Media.Brush)FindResource("SuccessOnLightBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
        SyncTrayIconVisibility();
    }

    private async Task RefreshLegacyServicesAsync()
    {
        _legacyServiceSnapshot = await _legacyServices.DetectAsync(_lifetime.Token);
        _externalWinwsProcessCount = _processes.ExternalZapretProcessCount();
        if (_processes.IsZapretRunning())
        {
            _legacyServiceSnapshot = _legacyServiceSnapshot
                .Where(service => !service.Name.StartsWith("WinDivert", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var legacyItems = _legacyServiceSnapshot
            .Select(service => LegacyServiceManager.IsTransientWinDivert(service.Name)
                ? $"{service.Name} (будет перезапущен автоматически)"
                : service.IsRunning ? $"{service.Name} (работает)" : service.Name)
            .ToList();
        if (_externalWinwsProcessCount > 0)
        {
            legacyItems.Add($"посторонних winws.exe: {_externalWinwsProcessCount}");
        }

        LegacyServicesText.Text = legacyItems.Count == 0
            ? "Старые службы и посторонние winws.exe не найдены"
            : "Найдены: " + string.Join(", ", legacyItems);
        LegacyServicesText.Foreground = legacyItems.Count == 0
            ? (System.Windows.Media.Brush)FindResource("MutedBrush")
            : System.Windows.Media.Brushes.Orange;
    }

    private void SyncTrayIconVisibility()
    {
        if (_exitRequested)
        {
            _notifyIcon.Visible = false;
            return;
        }

        _notifyIcon.Visible = true;
    }

    private void ServiceDrawer_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender == TelegramDrawer)
        {
            ZapretDrawer.IsExpanded = false;
        }
        else if (sender == ZapretDrawer)
        {
            TelegramDrawer.IsExpanded = false;
        }

        UpdateServiceDrawerGlyphs();
    }

    private void ServiceDrawer_Collapsed(object sender, RoutedEventArgs e) => UpdateServiceDrawerGlyphs();

    private void UpdateServiceDrawerGlyphs()
    {
        TelegramDrawerGlyph.Text = TelegramDrawer.IsExpanded ? "−" : "＋";
        ZapretDrawerGlyph.Text = ZapretDrawer.IsExpanded ? "−" : "＋";
    }

    private void UpdateHealth(HealthReport? report)
    {
        if (report is null || report.Results.Count == 0)
        {
            HealthStatusText.Text = "Нет подтверждённой стратегии";
            HealthStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningOnLightBrush");
            HealthDetailText.Text = "Проверьте Secure DNS и журнал, затем повторите поиск.";
            return;
        }

        HealthStatusText.Text = report.AllRequiredAvailable
            ? "YouTube ✓   Discord ✓"
            : report.SuccessfulChecks > 0
                ? $"Доступно {report.SuccessfulChecks} из {report.Results.Count}"
                : "Контрольные соединения недоступны";
        HealthStatusText.Foreground = report.AllRequiredAvailable
            ? (System.Windows.Media.Brush)FindResource("SuccessOnLightBrush")
            : (System.Windows.Media.Brush)FindResource("WarningOnLightBrush");
        HealthDetailText.Text = string.Join(Environment.NewLine, report.Results.Select(result =>
            $"{(result.IsSuccess ? "✓" : "✕")} {result.Name}: {result.Detail} ({result.Duration.TotalMilliseconds:0} мс)"));
    }

    private void SetGlobalStatus(string text, bool? healthy)
    {
        if (string.Equals(text, "Готово", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        void Append() => AppendVisibleLog($"> {text}");
        if (Dispatcher.CheckAccess()) Append();
        else Dispatcher.InvokeAsync(Append);
    }

    private async Task ExecuteExclusiveAsync(Func<Task> operation)
    {
        if (!await _operationGate.WaitAsync(0))
        {
            SetGlobalStatus("Другая операция ещё выполняется", null);
            return;
        }

        SetActionsEnabled(false);
        try
        {
            await operation();
            RefreshComponentStatus();
            SetGlobalStatus("Готово", true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            await _logger.InfoAsync($"Ошибка: {exception.Message}");
            SetGlobalStatus(exception.Message, false);
        }
        finally
        {
            SetActionsEnabled(true);
            _operationGate.Release();
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        UpdateButton.IsEnabled = enabled;
        OpenCustomizationButton.IsEnabled = enabled;
        OpenAutomationButton.IsEnabled = enabled;
        FindStrategyButton.IsEnabled = enabled;
        StartStrategyButton.IsEnabled = enabled;
        CheckHealthButton.IsEnabled = enabled;
        StartTelegramButton.IsEnabled = enabled;
        StopTelegramButton.IsEnabled = enabled;
        ConnectTelegramButton.IsEnabled = enabled;
        StopZapretButton.IsEnabled = enabled;
        CleanupServicesButton.IsEnabled = enabled;
        SaveCustomizationButton.IsEnabled = enabled;
    }

    private bool HasTelegram() =>
        !string.IsNullOrWhiteSpace(_settings.TelegramVersion) &&
        ComponentUpdater.IsValidInstallation(
            ComponentKind.TelegramProxy,
            _paths.TelegramDirectory(_settings.TelegramVersion));

    private bool HasZapret() =>
        !string.IsNullOrWhiteSpace(_settings.ZapretVersion) &&
        ComponentUpdater.IsValidInstallation(
            ComponentKind.Zapret,
            _paths.ZapretDirectory(_settings.ZapretVersion));

    private async void UpdateButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(UpdateComponentsAsync);

    private async void OpenCustomizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasZapret())
        {
            SetGlobalStatus("Сначала установите zapret через проверку обновлений", false);
            return;
        }

        var root = _paths.ZapretDirectory(_settings.ZapretVersion!);
        var dialog = new MethodSettingsWindow(
            root,
            _zapretCustomization.Load(root),
            _zapretCustomization.GetFakeOptions(root),
            _zapretHosts,
            _paths.HostsBackupsRoot)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteExclusiveAsync(async () =>
        {
            var wasRunning = _processes.IsZapretRunning(_settings.ZapretVersion);
            await _zapretCustomization.SaveAsync(root, dialog.Customization, _lifetime.Token);
            _settings.SelectedCoverageScore = 0;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            await _logger.InfoAsync("Пользовательские параметры zapret сохранены.", _lifetime.Token);

            if (wasRunning)
            {
                var strategy = _settings.SelectedStrategy
                    ?? throw new InvalidOperationException("Неизвестна текущая стратегия zapret.");
                await _processes.StartZapretAsync(_settings.ZapretVersion!, strategy, _lifetime.Token);
                await _logger.InfoAsync($"Параметры применены к стратегии {strategy}.", _lifetime.Token);
            }

            RefreshZapretCustomization();
        });
    }

    private async void OpenAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        var draft = new AppSettings
        {
            StartAtLogon = _settings.StartAtLogon,
            CheckUpdatesOnStart = _settings.CheckUpdatesOnStart,
            StartTelegramOnLaunch = _settings.StartTelegramOnLaunch,
            StartZapretOnLaunch = _settings.StartZapretOnLaunch,
            AutoSelectStrategy = _settings.AutoSelectStrategy
        };
        var dialog = new AutomationWindow(draft) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExecuteExclusiveAsync(async () =>
        {
            if (draft.StartAtLogon != _settings.StartAtLogon)
            {
                await _startup.SetEnabledAsync(draft.StartAtLogon, _lifetime.Token);
            }

            _settings.StartAtLogon = draft.StartAtLogon;
            _settings.CheckUpdatesOnStart = draft.CheckUpdatesOnStart;
            _settings.StartTelegramOnLaunch = draft.StartTelegramOnLaunch;
            _settings.StartZapretOnLaunch = draft.StartZapretOnLaunch;
            _settings.AutoSelectStrategy = draft.AutoSelectStrategy;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            ApplySettingsToControls();
            SyncTrayIconVisibility();

            if (dialog.CheckUpdatesRequested)
            {
                await UpdateComponentsAsync();
            }
        });
    }

    private async void StartTelegramButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(async () =>
        {
            if (!HasTelegram()) throw new InvalidOperationException("TG WS Proxy не установлен.");
            await _processes.StartTelegramAsync(_settings.TelegramVersion!, _lifetime.Token);
        });

    private async void StopTelegramButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(() => _processes.StopTelegramAsync(_settings.TelegramVersion, _lifetime.Token));

    private async void StopZapretButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(() => _processes.StopZapretAsync(_lifetime.Token));

    private async void CleanupServicesButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(async () =>
        {
            await RefreshLegacyServicesAsync();
            if (_legacyServiceSnapshot.Count == 0 && _externalWinwsProcessCount == 0)
            {
                SetGlobalStatus("Старые службы и посторонние winws.exe уже отсутствуют", true);
                return;
            }

            var cleanupItems = _legacyServiceSnapshot.Select(service => "• служба " + service.Name).ToList();
            if (_externalWinwsProcessCount > 0)
            {
                cleanupItems.Add($"• посторонних процессов winws.exe: {_externalWinwsProcessCount}");
            }

            var names = string.Join(Environment.NewLine, cleanupItems);
            var confirmation = System.Windows.MessageBox.Show(
                "Будут остановлены все процессы winws.exe и удалены найденные известные службы:" +
                Environment.NewLine + Environment.NewLine + names + Environment.NewLine + Environment.NewLine +
                "Продолжить?",
                "Очистка старых служб",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await _legacyServices.CleanupAsync(
                _legacyServiceSnapshot.Select(service => service.Name).ToArray(),
                _lifetime.Token);
            if (result.StoppedWinwsProcesses > 0)
            {
                await _logger.InfoAsync($"Остановлено процессов winws.exe: {result.StoppedWinwsProcesses}.");
            }

            foreach (var error in result.Errors)
            {
                await _logger.InfoAsync("Не удалось удалить службу: " + error);
            }

            await RefreshLegacyServicesAsync();
            if (result.Errors.Count > 0)
            {
                throw new InvalidOperationException("Часть служб не удалена; подробности в журнале.");
            }
        });

    private async void FindStrategyButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(SelectBestStrategyAsync);

    private async void StartStrategyButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(async () =>
        {
            if (!HasZapret()) throw new InvalidOperationException("zapret не установлен.");
            var strategy = StrategyCombo.SelectedItem as string
                ?? throw new InvalidOperationException("Выберите стратегию.");
            await _processes.StartZapretAsync(_settings.ZapretVersion!, strategy, _lifetime.Token);
            _settings.SelectedStrategy = strategy;
            _settings.SelectedCoverageScore = 0;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        });

    private async void CheckHealthButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(CheckHealthAsync);

    private async void SaveCustomizationButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteExclusiveAsync(async () =>
        {
            if (!HasZapret()) throw new InvalidOperationException("zapret не установлен.");
            if (GameFilterCombo.SelectedIndex < 0 || IpSetModeCombo.SelectedIndex < 0)
            {
                throw new InvalidOperationException("Выберите режим игрового фильтра и область IP-set.");
            }

            var ipSetMode = (IpSetMode)IpSetModeCombo.SelectedIndex;
            if (ipSetMode == IpSetMode.AnyIp)
            {
                var confirmation = System.Windows.MessageBox.Show(
                    "Режим «Любой IP» расширяет обработку zapret на весь подходящий трафик выбранного метода. " +
                    "Продолжить?",
                    "Широкий режим IP-set",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes) return;
            }

            var customization = new ZapretCustomization(
                (GameFilterMode)GameFilterCombo.SelectedIndex,
                ipSetMode,
                IncludedDomainsBox.Text,
                ExcludedDomainsBox.Text,
                IncludedIpRangesBox.Text,
                ExcludedIpRangesBox.Text,
                DiscordFakeCombo.SelectedItem as string,
                GameFakeCombo.SelectedItem as string);
            var root = _paths.ZapretDirectory(_settings.ZapretVersion!);
            var wasRunning = _processes.IsZapretRunning(_settings.ZapretVersion);
            await _zapretCustomization.SaveAsync(root, customization, _lifetime.Token);
            _settings.SelectedCoverageScore = 0;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            await _logger.InfoAsync("Пользовательские параметры zapret сохранены.", _lifetime.Token);

            if (wasRunning)
            {
                var strategy = _settings.SelectedStrategy
                    ?? throw new InvalidOperationException("Неизвестна текущая стратегия zapret.");
                await _processes.StartZapretAsync(_settings.ZapretVersion!, strategy, _lifetime.Token);
                await _logger.InfoAsync($"Параметры применены к стратегии {strategy}.", _lifetime.Token);
            }

            RefreshZapretCustomization();
        });

    private void OpenZapretListsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasZapret())
        {
            SetGlobalStatus("zapret не установлен", false);
            return;
        }

        var lists = Path.Combine(_paths.ZapretDirectory(_settings.ZapretVersion!), "lists");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lists}\"") { UseShellExecute = true });
    }

    private async void SettingsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await ExecuteExclusiveAsync(async () =>
        {
            var requestedAutostart = StartAtLogonCheck.IsChecked == true;
            if (requestedAutostart != _settings.StartAtLogon)
            {
                try
                {
                    await _startup.SetEnabledAsync(requestedAutostart, _lifetime.Token);
                    _settings.StartAtLogon = requestedAutostart;
                }
                catch
                {
                    StartAtLogonCheck.IsChecked = _settings.StartAtLogon;
                    throw;
                }
            }

            _settings.CheckUpdatesOnStart = CheckUpdatesCheck.IsChecked == true;
            _settings.StartTelegramOnLaunch = StartTelegramCheck.IsChecked == true;
            _settings.StartZapretOnLaunch = StartZapretCheck.IsChecked == true;
            _settings.AutoSelectStrategy = AutoStrategyCheck.IsChecked == true;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            SyncTrayIconVisibility();
        });
    }

    private async void StrategyCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || StrategyCombo.SelectedItem is not string strategy) return;
        if (string.Equals(_settings.SelectedStrategy, strategy, StringComparison.OrdinalIgnoreCase)) return;
        _settings.SelectedStrategy = strategy;
        _settings.SelectedCoverageScore = 0;
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
    }

    private void Logger_MessageLogged(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => AppendVisibleLog(message));
    }

    private void AppendVisibleLog(string message)
    {
        var visibleMessage = message.Length <= MaximumVisibleLogEntryLength
            ? message
            : "…" + message[^MaximumVisibleLogEntryLength..];
        _visibleLogEntries.Enqueue(visibleMessage);
        while (_visibleLogEntries.Count > MaximumVisibleLogEntries)
        {
            _visibleLogEntries.Dequeue();
        }

        LogTextBox.Text = string.Join(Environment.NewLine, _visibleLogEntries) + Environment.NewLine;
        LogTextBox.ScrollToEnd();
    }

    private Forms.NotifyIcon CreateNotifyIcon(bool visible)
    {
        var icon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "Flowseal Manager",
            Visible = visible
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Проверить доступность", null, (_, _) =>
            Dispatcher.Invoke(async () => await ExecuteExclusiveAsync(CheckHealthAsync)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        icon.ContextMenuStrip = menu;
        return icon;
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/FlowsealManager;component/Assets/FlowsealManager.ico"));
        return resource is null
            ? System.Drawing.SystemIcons.Application
            : new System.Drawing.Icon(resource.Stream);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    public void ShowFromExternalActivation() => ShowFromTray();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            SyncTrayIconVisibility();
            if (!_closeHintShown)
            {
                _closeHintShown = true;
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Flowseal Manager работает в фоне",
                    "Чтобы открыть менеджер, дважды щёлкните его значок в системном трее.",
                    Forms.ToolTipIcon.Info);
            }
        }
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _lifetime.Cancel();
        _monitorTimer?.Dispose();
        _statusTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _downloadClient.Dispose();
        _probeClient.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    public void ExitForSystemShutdown()
    {
        _exitRequested = true;
        _lifetime.Cancel();
        _monitorTimer?.Dispose();
        _statusTimer.Stop();
        _notifyIcon.Visible = false;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void ConnectTelegramButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TgWsProxy",
                "config.json");
            if (!File.Exists(configPath))
            {
                throw new InvalidOperationException(
                    "Сначала запустите TG WS Proxy и завершите его первоначальную настройку.");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var host = root.TryGetProperty("host", out var hostProperty)
                ? hostProperty.GetString() ?? "127.0.0.1"
                : "127.0.0.1";
            if (host == "0.0.0.0") host = "127.0.0.1";
            var port = root.TryGetProperty("port", out var portProperty)
                ? portProperty.GetInt32()
                : 1443;
            var secret = root.TryGetProperty("secret", out var secretProperty)
                ? secretProperty.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidDataException("В конфигурации TG WS Proxy отсутствует secret.");
            }

            OpenUrl($"tg://proxy?server={Uri.EscapeDataString(host)}&port={port}&secret=dd{secret}");
        }
        catch (Exception exception)
        {
            SetGlobalStatus(exception.Message, false);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_paths.LogFile}\"")
        {
            UseShellExecute = true
        });
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void TelegramLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");

    private void ZapretLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
}
