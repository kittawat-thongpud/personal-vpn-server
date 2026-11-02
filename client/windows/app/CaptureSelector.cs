using System;
using System.Windows;
using System.Windows.Controls;

namespace PersonalVpn;

public sealed partial class MainWindow
{
    private UIElement CaptureSelector()
    {
        var columns = new Grid { Name = "CaptureSelector", Height = 39, Margin = new Thickness(0, 0, 0, 5) };
        columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (string mode in new[] { "Proxy", "TUN" })
        {
            var choice = new RadioButton { Content = mode, GroupName = "CaptureMode", Name = "Capture" + mode,
                IsChecked = settings.CaptureMode == mode, IsEnabled = !connectBusy,
                ToolTip = mode == "Proxy" ? "Windows HTTP / HTTPS proxy capture" : "Whole-device capture · runtime in development" };
            System.Windows.Automation.AutomationProperties.SetName(choice, mode + " capture mode");
            choice.Click += (_, _) => RequestCaptureMode(mode);
            Grid.SetColumn(choice, mode == "Proxy" ? 0 : 1); columns.Children.Add(choice);
        }
        return new Border { Child = columns, CornerRadius = new CornerRadius(12), Background = Color("#101D30"), Padding = new Thickness(3, 2, 3, 0) };
    }
    private Window CaptureConfirmation(string requested)
    {
        bool available = requested == "Proxy" || CaptureCapabilities.TunAvailable;
        var dialog = new Window { Owner = this, Icon = Icon, Title = "Confirm capture mode", Width = 360, Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Background = Background, Foreground = Foreground, FontFamily = FontFamily };
        var body = PageRows(Auto, Star, Auto); body.Margin = new Thickness(20);
        Put(body, Text("Switch to " + requested + "?", 21), 0);
        var message = Stack();
        message.Children.Add(Text(requested == "TUN" ? "TUN captures apps and services and applies executable rules before site rules." : "Proxy captures apps that follow Windows HTTP / HTTPS proxy settings. App Tunnel will be disabled.", 13, "#B8C9DF"));
        if (!available) message.Children.Add(Text(CaptureCapabilities.TunNotice, 12, "#F1C77A"));
        else if (backend.Running) message.Children.Add(Text("Your Personal VPN connection will restart after confirmation.", 12, "#F1C77A"));
        Put(body, message, 1);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = Action("Cancel", () => dialog.DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        var confirm = Action("Confirm " + requested, () => dialog.DialogResult = true); confirm.Name = "ConfirmCaptureMode";
        confirm.IsEnabled = available; confirm.IsDefault = available; confirm.Background = Color("#207B70"); Grid.SetColumn(confirm, 1); actions.Children.Add(confirm);
        Put(body, actions, 2); dialog.Content = body; return dialog;
    }
    private async void RequestCaptureMode(string requested)
    {
        if (connectBusy || quitInProgress || requested == settings.CaptureMode) return;
        try
        {
            var dialog = CaptureConfirmation(requested);
            if (dialog.ShowDialog() != true) return;
            CaptureCapabilities.EnsureAvailable(requested);
            settings.CaptureMode = requested; Changed(); if (backend.Running) await ConnectSelectedProxy();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { Navigate("Home"); }
    }
    internal Window CaptureConfirmationForTest(string mode) => CaptureConfirmation(mode);
}
