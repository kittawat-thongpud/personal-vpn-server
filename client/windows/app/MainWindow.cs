using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PersonalVpn;

public sealed partial class MainWindow : Window
{
    private readonly Backend backend = new();
    private Settings settings;
    private readonly ContentControl content;
    private readonly TextBlock subtitle;
    private Button backButton = null!, homeNav = null!, settingsNav = null!;
    private Grid layoutRoot = null!;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string page = "Home";
    private bool dirty, exiting, lastCompactHome;
    private bool connectBusy;
    private readonly bool previewOnly;
    private TextBlock? status, detail, exit, logCount, captureDetail;
    private Button? connect;
    private TextBox? logText;
    private string lastLogText = "";
    private string lastState = "";
    private TextBlock? draftNotice;
    private TextBlock? uploadRate, downloadRate, uploadTotal, downloadTotal, trafficStatus;
    private TrafficChart? trafficChart;
    private static Brush Color(string hex) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));

    public MainWindow(Settings initial, bool autoConnect = false, bool preview = false)
    {
        previewOnly = preview;
        settings = initial;
        Policy.Upgrade(settings);
        settings.SystemProxy = true;
        Title = "Personal VPN · Windows preview 0.4.1";
        Width = Math.Min(438, SystemParameters.WorkArea.Width - 24); Height = Math.Min(800, SystemParameters.WorkArea.Height - 24);
        MinWidth = Math.Min(380, Width); MinHeight = Math.Min(640, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Color("#0D1627"); Foreground = Color("#EBEFF8"); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        SourceInitialized += (_, _) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); };
        if (!Application.Current.Resources.MergedDictionaries.Any(x => x.Source?.OriginalString.Contains("Theme.xaml") == true))
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PersonalVPN;component/Theme.xaml", UriKind.Relative) });
        var shell = new Grid { Margin = new Thickness(16, 10, 16, 12), MaxWidth = 460 };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { Height = 46, Margin = new Thickness(0, 0, 0, 8) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        backButton = Action("‹", () => Navigate(page == "Profiles" ? "Home" : "Settings")); backButton.FontSize = 28; backButton.Padding = new Thickness(0); backButton.Background = Brushes.Transparent; heading.Children.Add(backButton);
        subtitle = Text("Home", 21); subtitle.FontWeight = FontWeights.SemiBold; subtitle.VerticalAlignment = VerticalAlignment.Center; subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(subtitle, 1); heading.Children.Add(subtitle); shell.Children.Add(heading);
        content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(content, 1); shell.Children.Add(content);
        var navigation = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (string name in new[] { "Home", "Settings" })
        {
            int column = navigation.ColumnDefinitions.Count; navigation.ColumnDefinitions.Add(new ColumnDefinition());
            var button = Action(name, () => Navigate(name)); button.Tag = name;
            button.Height = 43;
            if (name == "Home") homeNav = button; else settingsNav = button;
            Grid.SetColumn(button, column); navigation.Children.Add(button);
        }
        Grid.SetRow(navigation, 2); shell.Children.Add(navigation);
        layoutRoot = new Grid { Background = Background }; layoutRoot.Children.Add(shell); Content = layoutRoot;
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true; HideToTray();
        };
        InitializeTray();
        SizeChanged += (_, _) => { if (page == "Home" && lastCompactHome != (Height < 680)) Navigate("Home"); };
        Navigate("Home");
        if (autoConnect) ContentRendered += async (_, _) => { try { await ConnectSelectedProxy(); } catch (Exception error) { ShowError(error); } };
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private async Task ConnectSelectedProxy()
    {
        if (previewOnly) throw new InvalidOperationException("Layout preview cannot connect or change system proxy settings.");
        CaptureCapabilities.EnsureAvailable(settings.CaptureMode);
        connectBusy = true;
        try
        {
            settings.SystemProxy = true; Store.Save(settings); Refresh();
            await backend.ConnectProxyAsync(settings.Snapshot()); dirty = false;
        }
        finally { connectBusy = false; Refresh(); }
    }
    private static TextBlock Text(string value, double size = 14, string color = "#EBEFF8") => new()
    {
        Text = value, FontSize = size, Foreground = Color(color), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 5)
    };
    private static Border Card(UIElement child) => new()
    {
        Background = new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#19263A"), (System.Windows.Media.Color)ColorConverter.ConvertFromString("#142034"), 90), BorderBrush = Color("#2D3A50"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Child = child
    };
    private Button Action(string name, Action action)
    {
        var button = new Button { Content = name };
        button.Click += (_, _) => { try { action(); } catch (Exception error) { ShowError(error); } };
        return button;
    }
    private Button AsyncAction(string name, Func<Task> action)
    {
        var button = new Button { Content = name };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); } catch (Exception error) { ShowError(error); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private void ShowError(Exception error)
    {
        if (quitInProgress) return;
        MessageBox.Show(this, error is InvalidOperationException or InvalidDataException ? error.Message : "Operation failed (" + error.GetType().Name + "). Check profile format and permissions.",
            "Personal VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Changed(bool affectsConnection = true)
    {
        if (previewOnly) return;
        Store.Save(settings); dirty = backend.Running && (dirty || affectsConnection); Refresh();
    }
    private StackPanel Stack() => new() { Orientation = Orientation.Vertical };
    private void Navigate(string target)
    {
        if (target == "AppTunnel" && settings.CaptureMode != "TUN" && !previewOnly) return;
        page = target; subtitle.Text = target switch { "Profiles" => "Server profiles", "VpnConfig" => "VPN configure", "AppTunnel" => "App Tunnel", "SiteTunnel" => "Site Tunnel", "Diagnostics" => "Backend logs", _ => target }; status = detail = exit = draftNotice = logCount = captureDetail = null; connect = null; logText = null; lastLogText = "";
        backButton.Visibility = target is "Home" or "Settings" ? Visibility.Hidden : Visibility.Visible;
        homeNav.Background = Color(target is "Home" or "Profiles" ? "#244C68" : "#18263A"); settingsNav.Background = Color(target is not ("Home" or "Profiles") ? "#244C68" : "#18263A");
        uploadRate = downloadRate = uploadTotal = downloadTotal = trafficStatus = null; trafficChart = null;
        if (target == "Diagnostics") { BuildLogs(); Refresh(); return; }
        content.Content = target switch { "Home" => MobileHome(), "Profiles" => Profiles(), "Settings" => SettingsPage(), "AppTunnel" => TunnelEditor(true), "SiteTunnel" => TunnelEditor(false), _ => ProtocolPage() };
        Refresh();
    }
    private UIElement TrafficPanel(bool compact = false)
    {
        var panel = Stack(); if (!compact) panel.Children.Add(Text("TUNNEL TRAFFIC", 10, "#9BAECD"));
        var values = new Grid(); values.ColumnDefinitions.Add(new ColumnDefinition()); values.ColumnDefinitions.Add(new ColumnDefinition());
        var tx = Stack(); uploadRate = Text("↑ 0 B/s", 19, "#16C8A0"); tx.Children.Add(uploadRate); uploadTotal = Text("0 B sent", 10, "#9BAECD"); tx.Children.Add(uploadTotal); values.Children.Add(tx);
        var rx = Stack(); downloadRate = Text("↓ 0 B/s", 19, "#64A8FF"); rx.Children.Add(downloadRate); downloadTotal = Text("0 B received", 10, "#9BAECD"); rx.Children.Add(downloadTotal); Grid.SetColumn(rx, 1); values.Children.Add(rx); panel.Children.Add(values);
        if (compact) { uploadRate.FontSize = downloadRate.FontSize = 17; uploadTotal.Margin = downloadTotal.Margin = new Thickness(0, 0, 0, 3); }
        trafficChart = new TrafficChart { Height = compact ? 24 : 36, HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "60 samples · tunnel payload only · includes health traffic" };
        foreach (var sample in backend.Traffic.History) trafficChart.Sample(sample);
        panel.Children.Add(trafficChart); trafficStatus = Text("Session totals", 10, "#8395B2"); trafficStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; panel.Children.Add(trafficStatus); var card = Card(panel); card.Margin = new Thickness(0); card.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(14, 9, 14, 9); return card;
    }
    private static string Country(string code) => code switch { "JP" => "◉ JP", "SG" => "◒ SG", "US" => "★ US", "KR" => "◉ KR", "TH" => "▰ TH", _ => "◎ " + code };
    private UIElement Profiles()
    {
        var body = PageRows(Auto, Star); var header = Stack();
        header.Children.Add(Action("+  Import profile", Import));
        header.Children.Add(Text("Import a device profile folder. Credentials stay encrypted on this Windows account.", 12, "#9BAECD")); Put(body, header, 0);
        var list = Stack();
        foreach (var profile in settings.Profiles.ToArray())
        {
            var row = Stack(); row.Children.Add(Text(Country(profile.Country) + "    " + profile.Name + (settings.SelectedId == profile.Id ? "   ✓" : ""), 17));
            row.Children.Add(Text(profile.Endpoint, 12, "#9BAECD")); var actions = new WrapPanel();
            actions.Children.Add(Action("Select", () => { settings.SelectedId = profile.Id; Changed(); Navigate("Home"); }));
            actions.Children.Add(Action("Edit", () => EditProfile(profile)));
            actions.Children.Add(AsyncAction("Delete", async () =>
            {
                bool active = backend.Active?.SelectedId == profile.Id;
                if (MessageBox.Show(this, active ? "Disconnect and delete this active profile?" : "Delete this profile?", "Server profile", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                if (active) await backend.StopAsync();
                settings.Profiles.Remove(profile);
                if (settings.SelectedId == profile.Id) settings.SelectedId = settings.Profiles.FirstOrDefault()?.Id;
                Changed(); Navigate("Profiles");
            })); row.Children.Add(actions); list.Children.Add(Card(row));
        }
        Put(body, Card(WidgetScroll(list)), 1);
        return body;
    }
    private void Import()
    {
        var picker = new OpenFolderDialog { Title = "Select exported Windows profile folder" };
        if (picker.ShowDialog(this) != true) return;
        var profile = Store.Import(picker.FolderName); settings.Profiles.Add(profile); settings.SelectedId = profile.Id;
        Changed(); EditProfile(profile); Navigate("Profiles");
    }
    private void EditProfile(ServerProfile profile)
    {
        var dialog = new Window { Owner = this, Title = "Edit server", Width = 360, Height = 305, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var panel = new StackPanel { Margin = new Thickness(22) }; panel.Children.Add(Text("Server name"));
        var name = new TextBox { Text = profile.Name, MaxLength = 80 }; panel.Children.Add(name); panel.Children.Add(Text("Country icon"));
        var country = Combo(new[] { "JP", "SG", "KR", "US", "HK", "TW", "TH", "GB", "DE", "FR", "NL", "AU", "Other" }, profile.Country, _ => { }); panel.Children.Add(country);
        panel.Children.Add(Action("Save", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            profile.Name = name.Text.Trim(); profile.Country = country.SelectedItem.ToString()!; Changed(); dialog.Close(); Navigate("Profiles");
        })); dialog.Content = panel; dialog.ShowDialog();
    }
    private ComboBox Combo(IEnumerable<string> values, string selected, Action<string> change)
    {
        var box = new ComboBox { ItemsSource = values, SelectedItem = selected, MinWidth = 125 };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string value) { try { change(value); } catch (Exception error) { ShowError(error); } } };
        return box;
    }
    private void BuildLogs()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(Text("VPN backend only · session memory · max 1,000 records / 1 MiB\nExit and reopen starts a fresh session; hiding to tray retains logs.", 13, "#9BAECD"));
        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var clear = Action("Clear", ClearSessionLogs); tools.Children.Add(clear);
        tools.Children.Add(Action("Export", ExportSessionLogs)); logCount = Text("0 / 1,000", 12, "#9BAECD"); tools.Children.Add(logCount); Grid.SetRow(tools, 1); grid.Children.Add(tools);
        logText = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, AcceptsReturn = true, MinHeight = 0 };
        Grid.SetRow(logText, 2); grid.Children.Add(logText); content.Content = grid; Refresh();
    }
    private void Refresh()
    {
        if (exiting || quitInProgress) return;
        if (showRequest?.WaitOne(0) == true) { RestoreFromTray(); return; }
        tray?.Update();
        if (status != null) { status.Text = backend.State; status.Foreground = Color(backend.State == "Connected" ? "#57DCB5" : "#D7E4F8"); }
        if (detail != null) detail.Text = backend.Detail;
        if (status != null) status.ToolTip = backend.Detail;
        if (exit != null) exit.Text = "Exit IP: " + backend.ExitIp;
        if (captureDetail != null) captureDetail.Text = backend.CaptureStatus;
        if (uploadRate != null)
        {
            var sample = backend.Traffic.Snapshot;
            uploadRate.Text = sample.Available ? "↑ " + TrafficMeter.Format(sample.UploadRate) + "/s" : "↑ —";
            downloadRate!.Text = sample.Available ? "↓ " + TrafficMeter.Format(sample.DownloadRate) + "/s" : "↓ —";
            uploadTotal!.Text = TrafficMeter.Format(sample.Upload) + " sent"; downloadTotal!.Text = TrafficMeter.Format(sample.Download) + " received";
            trafficStatus!.Text = sample.Available ? "Live · 1s · this connection" : backend.Running ? "Counters unavailable" : "Session totals · connect to see traffic";
            trafficChart?.Sample(sample);
        }
        if (connect != null) { connect.Content = connectBusy ? "Connecting…" : backend.Running ? dirty ? "Apply &\nreconnect" : backend.State == "Proxy conflict" ? "Use proxy" : "Disconnect" : "Connect"; connect.IsEnabled = !connectBusy && settings.Profiles.Count > 0; }
        if (draftNotice != null) { draftNotice.Text = dirty ? "Settings changed · reconnect to apply" : ""; draftNotice.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed; }
        if (page == "Diagnostics" && logText != null)
        {
            var records = backend.Log.Snapshot(); string next = string.Join(Environment.NewLine, records.Select(x => $"{x.Time:HH:mm:ss}  {x.Level}  {x.Event}\n{x.Message}"));
            if (next != lastLogText)
            {
                bool bottom = logText.VerticalOffset >= logText.ExtentHeight - logText.ViewportHeight - 20;
                logText.Text = next; lastLogText = next; if (bottom) logText.ScrollToEnd();
            }
            if (logCount != null) logCount.Text = records.Length + " / 1,000  •  " + (backend.Log.Bytes / 1024) + " KiB";
        }
        if (backend.State != lastState) { lastState = backend.State; Title = "Personal VPN · " + lastState; }
    }
}
