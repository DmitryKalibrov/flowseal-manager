using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlowsealManager.Core.Models;
using FlowsealManager.Core.Services;

namespace FlowsealManager.App;

public partial class HostsSettingsWindow : Window
{
    private readonly ZapretHostsService _hostsService;
    private readonly string _backupsRoot;
    private string? _officialContent;
    private bool _busy;
    private ZapretHostsStatus _lastStatus = new(false, null, 0, 0, null);

    public HostsSettingsWindow(ZapretHostsService hostsService, string backupsRoot)
    {
        InitializeComponent();
        _hostsService = hostsService;
        _backupsRoot = backupsRoot;
        try
        {
            RefreshStatus(_hostsService.Inspect());
        }
        catch (Exception exception)
        {
            HostsStatusText.Text = "Не удалось прочитать hosts";
            HostsDetailText.Text = exception.Message;
            HostsStatusGlyph.Text = "!";
            HostsStatusGlyph.Foreground =
                (System.Windows.Media.Brush)FindResource("WarningOnLightBrush");
            ValidationText.Text = exception.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            OperationText.Text = "Загружаю официальный .service/hosts из репозитория Flowseal…";
            _officialContent = await _hostsService.DownloadOfficialAsync();
            var status = _hostsService.Inspect(_officialContent);
            RefreshStatus(status);
            OperationText.Text = status.IsInstalled
                ? status.IsCurrent == true
                    ? $"Установленный блок совпадает с официальным списком: {status.OfficialEntries} записей."
                    : $"Найдено обновление: официальный список содержит {status.OfficialEntries} записей."
                : $"Официальный список загружен: {status.OfficialEntries} записей. Его можно установить.";
        });

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                this,
                "Менеджер добавит или обновит отдельный блок Flowseal в системном hosts. " +
                "Перед изменением будет создана резервная копия. Продолжить?",
                "Применить hosts Zapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            OperationText.Text = "Получаю и проверяю официальный список…";
            _officialContent ??= await _hostsService.DownloadOfficialAsync();
            var change = await _hostsService.InstallOrUpdateAsync(_officialContent);
            RefreshStatus(change.Status);
            FlushDnsCache();
            OperationText.Text =
                $"Список применён. Записей: {change.Status.InstalledEntries}. " +
                $"Резервная копия: {Path.GetFileName(change.BackupPath)}";
        });
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                this,
                "Удалить из системного hosts только блок, добавленный Flowseal Manager? " +
                "Остальные строки останутся без изменений.",
                "Удалить hosts Zapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var change = await _hostsService.RemoveAsync();
            RefreshStatus(_hostsService.Inspect(_officialContent));
            if (change is null)
            {
                OperationText.Text = "Управляемый блок Flowseal в hosts уже отсутствует.";
                return;
            }

            FlushDnsCache();
            OperationText.Text =
                "Блок Flowseal удалён; остальные записи сохранены. " +
                $"Резервная копия: {Path.GetFileName(change.BackupPath)}";
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        SetActionsEnabled(false);
        ValidationText.Visibility = Visibility.Collapsed;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
            ValidationText.Visibility = Visibility.Visible;
            OperationText.Text = "Операция не выполнена; системный hosts не был изменён.";
        }
        finally
        {
            _busy = false;
            SetActionsEnabled(true);
        }
    }

    private void RefreshStatus(ZapretHostsStatus status)
    {
        _lastStatus = status;
        RemoveButton.IsEnabled = status.IsInstalled;
        if (!status.IsInstalled)
        {
            HostsStatusText.Text = "Список не установлен";
            HostsDetailText.Text = "Системный hosts не содержит управляемого блока Flowseal Manager.";
            HostsStatusGlyph.Text = "−";
            HostsStatusGlyph.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            InstallButton.Content = "Установить";
            return;
        }

        HostsStatusText.Text = status.IsCurrent switch
        {
            true => "Установлен · актуален",
            false => "Доступно обновление",
            null => "Список установлен"
        };
        var installedAt = status.InstalledAt is null
            ? "время установки неизвестно"
            : $"установлен {status.InstalledAt:dd.MM.yyyy HH:mm}";
        HostsDetailText.Text = $"Записей: {status.InstalledEntries}; {installedAt}.";
        HostsStatusGlyph.Text = status.IsCurrent == false ? "!" : "✓";
        HostsStatusGlyph.Foreground = status.IsCurrent == false
            ? (System.Windows.Media.Brush)FindResource("WarningOnLightBrush")
            : (System.Windows.Media.Brush)FindResource("SuccessOnLightBrush");
        InstallButton.Content = status.IsCurrent == false ? "Обновить" : "Переустановить";
    }

    private void SetActionsEnabled(bool enabled)
    {
        CheckButton.IsEnabled = enabled;
        InstallButton.IsEnabled = enabled;
        OpenHostsButton.IsEnabled = enabled;
        OpenBackupsButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled && _lastStatus.IsInstalled;
    }

    private void OpenHostsButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_hostsService.HostsFile}\"")
        {
            UseShellExecute = true
        });
    }

    private void OpenBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_backupsRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backupsRoot}\"")
        {
            UseShellExecute = true
        });
    }

    private static void FlushDnsCache()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(5000);
        }
        catch
        {
            // Windows обновит DNS-кэш сам; очистка лишь ускоряет применение hosts.
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
