using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using FlowsealManager.Core.Models;
using FlowsealManager.Core.Services;

namespace FlowsealManager.App;

public partial class MethodSettingsWindow : Window
{
    private readonly string _zapretRoot;
    private readonly ZapretHostsService _hostsService;
    private readonly string _hostsBackupsRoot;

    public MethodSettingsWindow(
        string zapretRoot,
        ZapretCustomization customization,
        ZapretFakeOptions fakeOptions,
        ZapretHostsService hostsService,
        string hostsBackupsRoot)
    {
        InitializeComponent();
        _zapretRoot = zapretRoot;
        _hostsService = hostsService;
        _hostsBackupsRoot = hostsBackupsRoot;
        Customization = customization;

        GameFilterCombo.ItemsSource = new[] { "Выключен", "TCP и UDP", "Только TCP", "Только UDP" };
        IpSetModeCombo.ItemsSource = new[] { "Официальный список IP", "Не применять IP-диапазоны", "Любой IP" };
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

    public ZapretCustomization Customization { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GameFilterCombo.SelectedIndex < 0 || IpSetModeCombo.SelectedIndex < 0)
            {
                throw new InvalidOperationException("Выберите игровой фильтр и область IP-set.");
            }

            var ipSetMode = (IpSetMode)IpSetModeCombo.SelectedIndex;
            if (ipSetMode == IpSetMode.AnyIp &&
                System.Windows.MessageBox.Show(
                    this,
                    "Режим «Любой IP» расширяет обработку zapret на весь подходящий трафик. Продолжить?",
                    "Широкий режим IP-set",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            ZapretCustomizationService.NormalizeDomains(IncludedDomainsBox.Text);
            ZapretCustomizationService.NormalizeDomains(ExcludedDomainsBox.Text);
            ZapretCustomizationService.NormalizeIpRanges(IncludedIpRangesBox.Text);
            ZapretCustomizationService.NormalizeIpRanges(ExcludedIpRangesBox.Text);

            Customization = new ZapretCustomization(
                (GameFilterMode)GameFilterCombo.SelectedIndex,
                ipSetMode,
                IncludedDomainsBox.Text,
                ExcludedDomainsBox.Text,
                IncludedIpRangesBox.Text,
                ExcludedIpRangesBox.Text,
                DiscordFakeCombo.SelectedItem as string,
                GameFakeCombo.SelectedItem as string);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private void OpenListsButton_Click(object sender, RoutedEventArgs e)
    {
        var lists = Path.Combine(_zapretRoot, "lists");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{lists}\"") { UseShellExecute = true });
    }

    private void OpenHostsSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HostsSettingsWindow(_hostsService, _hostsBackupsRoot) { Owner = this };
        dialog.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
