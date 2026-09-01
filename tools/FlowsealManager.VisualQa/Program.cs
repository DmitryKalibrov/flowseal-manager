using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowsealManager.App;
using FlowsealManager.Core.Models;
using FlowsealManager.Core.Services;

namespace FlowsealManager.VisualQa;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputPath = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "flowseal-manager-preview.png");
        const int width = 1100;
        const int height = 760;

        var application = new FlowsealManager.App.App();
        application.InitializeComponent();
        var window = new MainWindow(startMinimized: false, visualQa: true)
        {
            Width = width,
            Height = height
        };

        window.Show();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        window.WindowState = WindowState.Maximized;
        window.UpdateLayout();
        if (window.WindowState == WindowState.Maximized)
        {
            throw new InvalidOperationException("Главное окно всё ещё можно развернуть на полный экран.");
        }

        if (window.FindName("GlobalStatusText") is not null ||
            window.FindName("GlobalStatusDot") is not null)
        {
            throw new InvalidOperationException("Индикатор готовности остался в шапке.");
        }

        var heroPanel = window.FindName("HeroPanel") as FrameworkElement
            ?? throw new InvalidOperationException("Блок шапки не найден.");
        var logTextBox = window.FindName("LogTextBox") as TextBox
            ?? throw new InvalidOperationException("Журнал не найден.");
        logTextBox.Text = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 500).Select(index =>
                $"2026-08-23 14:{index % 60:00}:00  Проверка профиля {index}: Discord Gateway OK"));
        logTextBox.ScrollToEnd();
        window.UpdateLayout();
        if (Math.Abs(heroPanel.ActualHeight - 300) > 0.5)
        {
            throw new InvalidOperationException(
                $"Журнал растянул шапку до {heroPanel.ActualHeight:0.#} px вместо 300 px.");
        }

        var mainControlCount = ExerciseControls(window);

        Keyboard.ClearFocus();
        window.UpdateLayout();

        var mainScroll = FindVisualChild<ScrollViewer>(window);
        if (mainScroll is not null)
        {
            mainScroll.ScrollToHome();
            window.UpdateLayout();
        }

        var visibleScrollBarSize = FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(window)
            .Where(scrollBar => scrollBar.IsVisible)
            .Select(scrollBar => scrollBar.Orientation == Orientation.Vertical
                ? scrollBar.ActualWidth
                : scrollBar.ActualHeight)
            .DefaultIfEmpty(0)
            .Max();
        if (visibleScrollBarSize > 0.5)
        {
            throw new InvalidOperationException(
                $"Видимая полоса прокрутки занимает {visibleScrollBarSize:0.#} px.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        SaveWindow(window, outputPath, width, height);
        var telegramDrawer = window.FindName("TelegramDrawer") as Expander
            ?? throw new InvalidOperationException("Ящик Telegram не найден.");
        var zapretDrawer = window.FindName("ZapretDrawer") as Expander
            ?? throw new InvalidOperationException("Ящик Discord × YouTube не найден.");
        if (telegramDrawer.IsExpanded || zapretDrawer.IsExpanded)
        {
            throw new InvalidOperationException("Сервисные ящики должны быть закрыты при запуске.");
        }

        telegramDrawer.IsExpanded = true;
        window.UpdateLayout();
        if (!telegramDrawer.IsExpanded || zapretDrawer.IsExpanded)
        {
            throw new InvalidOperationException("Ящик Telegram раскрывается неправильно.");
        }
        SaveWindow(
            window,
            Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                Path.GetFileNameWithoutExtension(outputPath) + "-telegram.png"),
            width,
            height);

        zapretDrawer.IsExpanded = true;
        window.UpdateLayout();
        if (telegramDrawer.IsExpanded || !zapretDrawer.IsExpanded)
        {
            throw new InvalidOperationException("Сервисные ящики не работают как аккордеон.");
        }
        SaveWindow(
            window,
            Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                Path.GetFileNameWithoutExtension(outputPath) + "-zapret.png"),
            width,
            height);
        zapretDrawer.IsExpanded = false;
        window.UpdateLayout();
        if (mainScroll is not null)
        {
            var homeOffset = mainScroll.VerticalOffset;
            mainScroll.ScrollToEnd();
            window.UpdateLayout();
            if (mainScroll.ScrollableHeight > 0 && mainScroll.VerticalOffset <= homeOffset)
            {
                throw new InvalidOperationException("Скрытая прокрутка главного окна не работает.");
            }
            var bottomPath = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                Path.GetFileNameWithoutExtension(outputPath) + "-bottom.png");
            SaveWindow(window, bottomPath, width, height);
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var baseName = Path.GetFileNameWithoutExtension(outputPath);
        var customization = new ZapretCustomization(
            GameFilterMode.TcpOnly,
            IpSetMode.OfficialList,
            "example.org",
            "static.example.org",
            "198.51.100.0/24",
            "203.0.113.15/32",
            "stun.bin",
            "quic_initial_www_google_com.bin");
        var methodWindow = new MethodSettingsWindow(
            Path.GetTempPath(),
            customization,
            new ZapretFakeOptions(
                ["stun.bin", "quic_initial_www_google_com.bin"],
                "stun.bin",
                "quic_initial_www_google_com.bin"),
            new ZapretHostsService(
                new HttpClient(),
                Path.Combine(Path.GetTempPath(), "flowseal-hosts-qa-backups"),
                Path.Combine(Path.GetTempPath(), "flowseal-hosts-qa")),
            Path.Combine(Path.GetTempPath(), "flowseal-hosts-qa-backups"));
        methodWindow.Show();
        methodWindow.UpdateLayout();
        var methodControlCount = ExerciseControls(methodWindow);
        SaveWindow(
            methodWindow,
            Path.Combine(outputDirectory, baseName + "-method.png"),
            (int)methodWindow.Width,
            (int)methodWindow.Height);
        methodWindow.Close();

        var hostsRoot = Path.Combine(Path.GetTempPath(), $"flowseal-hosts-qa-{Guid.NewGuid():N}");
        var hostsPath = Path.Combine(hostsRoot, "etc", "hosts");
        var hostsBackups = Path.Combine(hostsRoot, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(hostsPath)!);
        File.WriteAllText(hostsPath, "127.0.0.1 localhost\r\n");
        var hostsWindow = new HostsSettingsWindow(
            new ZapretHostsService(new HttpClient(), hostsBackups, hostsPath),
            hostsBackups);
        hostsWindow.Show();
        hostsWindow.UpdateLayout();
        var hostsControlCount = ExerciseControls(hostsWindow);
        SaveWindow(
            hostsWindow,
            Path.Combine(outputDirectory, baseName + "-hosts.png"),
            (int)hostsWindow.Width,
            (int)hostsWindow.Height);
        hostsWindow.Close();
        Directory.Delete(hostsRoot, true);

        var automationWindow = new AutomationWindow(new AppSettings());
        automationWindow.Show();
        automationWindow.UpdateLayout();
        var automationControlCount = ExerciseControls(automationWindow);
        SaveWindow(
            automationWindow,
            Path.Combine(outputDirectory, baseName + "-automation.png"),
            (int)automationWindow.Width,
            (int)automationWindow.Height);
        automationWindow.Close();

        window.Close();
        Console.WriteLine(Path.GetFullPath(outputPath));
        Console.WriteLine($"Высота шапки с 500 строками журнала: {heroPanel.ActualHeight:0} px");
        Console.WriteLine("Полосы прокрутки скрыты; программная прокрутка работает.");
        Console.WriteLine(
            $"Проверено интерактивных контролов: главное окно — {mainControlCount}, " +
            $"параметры — {methodControlCount}, hosts — {hostsControlCount}, " +
            $"автоматизация — {automationControlCount}");
    }

    private static int ExerciseControls(Window window)
    {
        var controls = FindVisualChildren<Control>(window)
            .Where(control => control.Focusable && control.IsEnabled && control.IsVisible)
            .ToArray();
        foreach (var control in controls)
        {
            control.Focus();
            Keyboard.Focus(control);
            window.UpdateLayout();
        }

        Keyboard.ClearFocus();
        window.UpdateLayout();
        return controls.Length;
    }

    private static void SaveWindow(Window window, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
            {
                return result;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) yield return result;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
