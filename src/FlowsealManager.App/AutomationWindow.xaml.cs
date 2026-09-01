using System.Windows;
using System.Windows.Input;
using FlowsealManager.Core.Models;

namespace FlowsealManager.App;

public partial class AutomationWindow : Window
{
    public AutomationWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;
        StartAtLogonCheck.IsChecked = settings.StartAtLogon;
        UpdateManagerCheck.IsChecked = settings.UpdateManagerAutomatically;
        CheckUpdatesCheck.IsChecked = settings.CheckUpdatesOnStart;
        StartTelegramCheck.IsChecked = settings.StartTelegramOnLaunch;
        StartZapretCheck.IsChecked = settings.StartZapretOnLaunch;
        AutoStrategyCheck.IsChecked = settings.AutoSelectStrategy;
    }

    public AppSettings Settings { get; }

    public bool CheckUpdatesRequested { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.StartAtLogon = StartAtLogonCheck.IsChecked == true;
        Settings.UpdateManagerAutomatically = UpdateManagerCheck.IsChecked == true;
        Settings.CheckUpdatesOnStart = CheckUpdatesCheck.IsChecked == true;
        Settings.StartTelegramOnLaunch = StartTelegramCheck.IsChecked == true;
        Settings.StartZapretOnLaunch = StartZapretCheck.IsChecked == true;
        Settings.AutoSelectStrategy = AutoStrategyCheck.IsChecked == true;
        DialogResult = true;
    }

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesRequested = true;
        SaveButton_Click(sender, e);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
