using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FlowsealManager.App;

public partial class App : System.Windows.Application
{
    private const string ActivationEventName = "Local\\FlowsealManager-Activate-6C02E8B7";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private Task? _activationTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        var updatePlanIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--apply-update", StringComparison.OrdinalIgnoreCase));
        if (updatePlanIndex >= 0 && updatePlanIndex + 2 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            var planPath = e.Args[updatePlanIndex + 1];
            var planSha256 = e.Args[updatePlanIndex + 2];
            var exitCode = ApplicationUpdateRunner.RunAsync(planPath, planSha256).GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        _mutex = new Mutex(true, "Local\\FlowsealManager-6C02E8B7", out _ownsMutex);
        if (!_ownsMutex)
        {
            try
            {
                using var activation = EventWaitHandle.OpenExisting(ActivationEventName);
                activation.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                System.Windows.MessageBox.Show(
                    "Flowseal Manager уже запускается. Повторите через несколько секунд.",
                    "Flowseal Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
        string? updateMarker = null;
        var updateCompleteIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--update-complete", StringComparison.OrdinalIgnoreCase));
        if (updateCompleteIndex >= 0 && updateCompleteIndex + 1 < e.Args.Length)
        {
            try
            {
                var marker = Path.GetFullPath(e.Args[updateCompleteIndex + 1]);
                var expectedRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlowsealManager",
                    "updates") + Path.DirectorySeparatorChar;
                if (marker.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    updateMarker = marker;
                }
            }
            catch
            {
                // The updater will restore the previous build when no marker appears.
            }
        }

        var minimized = e.Args.Any(argument =>
            string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(minimized);
        MainWindow = window;
        window.Show();
        if (updateMarker is not null)
        {
            var marker = updateMarker;
            Dispatcher.InvokeAsync(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            }, DispatcherPriority.ContextIdle);
        }

        if (e.Args.Any(argument => string.Equals(
                argument,
                "--update-rollback",
                StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(
                "Новая версия не смогла запуститься. Flowseal Manager автоматически вернул предыдущую сборку. " +
                "Подробности сохранены в журнале update.log.",
                "Обновление отменено",
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
        }
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationCancellation = new CancellationTokenSource();
        _activationTask = Task.Run(() => ListenForActivation(_activationCancellation.Token));
    }

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlowsealManager",
                "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "crash.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // The fallback logger must never replace the original error.
        }

        System.Windows.MessageBox.Show(
            "Интерфейс столкнулся с ошибкой, но менеджер оставлен запущенным. " +
            "Подробности сохранены в журнале crash.log.",
            "Flowseal Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
        }

        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        _activationEvent?.Dispose();
        _activationCancellation?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            window.ExitForSystemShutdown();
        }

        base.OnSessionEnding(e);
    }

    private void ListenForActivation(CancellationToken cancellationToken)
    {
        if (_activationEvent is null)
        {
            return;
        }

        var handles = new[] { _activationEvent, cancellationToken.WaitHandle };
        while (!cancellationToken.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) != 0)
            {
                return;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow window)
                {
                    window.ShowFromExternalActivation();
                }
            });
        }
    }
}
