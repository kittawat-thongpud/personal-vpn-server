using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PersonalVpn;

public sealed class MainWindow : Window
{
    private readonly Backend backend = new();
    private Settings settings;
    private readonly ContentControl content;
    private readonly TextBlock subtitle;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string page = "Home";
    private bool dirty, exiting;
    private TextBlock? status, detail, exit, logCount, captureDetail;
    private Button? connect;
    private TextBox? logText;
    private string lastLogText = "";
    private string lastState = "";
    private TextBlock? draftNotice;
    private static Brush Color(string hex) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));

    public MainWindow(Settings initial, bool autoConnect = false)
    {
        settings = initial;
        Title = "Personal VPN · Windows preview 0.2.4";
        Width = 560; Height = 850; MinWidth = 510; MinHeight = 710;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Color("#0D1627"); Foreground = Color("#EBEFF8"); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        Resources = (ResourceDictionary)XamlReader.Parse("""
          <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Style TargetType="Button">
              <Setter Property="Background" Value="#243553"/><Setter Property="Foreground" Value="#F0F4FF"/>
              <Setter Property="Padding" Value="15,10"/><Setter Property="Margin" Value="3"/>
              <Setter Property="Cursor" Value="Hand"/><Setter Property="BorderThickness" Value="0"/>
              <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="frame" Background="{TemplateBinding Background}" CornerRadius="12" Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border><ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="frame" Property="Opacity" Value="0.85"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="frame" Property="Opacity" Value="0.4"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            <Style TargetType="TextBox"><Setter Property="Background" Value="#101E34"/><Setter Property="Foreground" Value="#ECF2FF"/>
              <Setter Property="BorderBrush" Value="#415272"/><Setter Property="Padding" Value="9"/><Setter Property="Margin" Value="0,5,0,10"/>
              <Setter Property="CaretBrush" Value="White"/></Style>
            <Style TargetType="ComboBox"><Setter Property="Margin" Value="0,5,0,10"/><Setter Property="Padding" Value="8"/>
              <Setter Property="Foreground" Value="#101827"/></Style>
            <Style TargetType="CheckBox"><Setter Property="Foreground" Value="#ECF2FF"/><Setter Property="Margin" Value="2,9"/></Style>
          </ResourceDictionary>
          """);
        var shell = new Grid { Margin = new Thickness(23, 16, 23, 16) };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(Text("PERSONAL VPN", 13, "#91A5C5"));
        subtitle = Text("Home", 25); subtitle.FontWeight = FontWeights.SemiBold;
        heading.Children.Add(subtitle); shell.Children.Add(heading);
        content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(content, 1); shell.Children.Add(content);
        var navigation = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (string name in new[] { "Home", "Rules", "VPN", "Logs" })
        {
            int column = navigation.ColumnDefinitions.Count; navigation.ColumnDefinitions.Add(new ColumnDefinition());
            var button = Action(name, () => Navigate(name)); button.Tag = name;
            Grid.SetColumn(button, column); navigation.Children.Add(button);
        }
        Grid.SetRow(navigation, 2); shell.Children.Add(navigation); Content = shell;
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Closing += async (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true; IsEnabled = false; timer.Stop();
            await backend.DisposeAsync(); exiting = true;
            Dispatcher.BeginInvoke(new System.Action(Close));
        };
        Navigate("Home");
        if (autoConnect) ContentRendered += async (_, _) => { try { await backend.StartAsync(settings.Snapshot()); } catch (Exception error) { ShowError(error); } };
    }
    private static TextBlock Text(string value, double size = 14, string color = "#EBEFF8") => new()
    {
        Text = value, FontSize = size, Foreground = Color(color), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 5)
    };
    private static Border Card(UIElement child) => new()
    {
        Background = Color("#182438"), BorderBrush = Color("#2D3A50"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(19), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 13), Child = child
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
    private void ShowError(Exception error) => MessageBox.Show(this,
        error is InvalidOperationException or InvalidDataException ? error.Message : "Operation failed (" + error.GetType().Name + "). Check profile format and permissions.",
        "Personal VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void Changed()
    {
        Store.Save(settings); dirty = backend.Running; Refresh();
    }
    private StackPanel Stack() => new() { Orientation = Orientation.Vertical };
    private void Navigate(string target)
    {
        page = target; subtitle.Text = target; status = detail = exit = draftNotice = logCount = captureDetail = null; connect = null; logText = null;
        if (target == "Logs") { BuildLogs(); return; }
        var body = target switch { "Home" => Home(), "Profiles" => Profiles(), "Rules" => Rules(), _ => Vpn() };
        content.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Refresh();
    }
    private UIElement Home()
    {
        var body = Stack();
        var current = settings.Profiles.FirstOrDefault(x => x.Id == settings.SelectedId);
        var server = Action(current == null ? "+  Import server profile" : Country(current.Country) + "    " + current.Name + "     ›", () => Navigate("Profiles"));
        server.MinHeight = 77; server.FontSize = 17; server.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(server);
        var modes = new Grid(); modes.ColumnDefinitions.Add(new ColumnDefinition()); modes.ColumnDefinitions.Add(new ColumnDefinition());
        var capture = Stack(); capture.Children.Add(Text("CAPTURE", 12, "#9BAECD")); capture.Children.Add(Text(settings.SystemProxy ? "System proxy" : "App proxy", 18));
        capture.Children.Add(Text(settings.SystemProxy ? "Browser HTTP / HTTPS" : "Only configured apps", 12, "#9BAECD")); var left = Card(capture); left.Margin = new Thickness(0, 0, 7, 13); modes.Children.Add(left);
        var traffic = Stack(); traffic.Children.Add(Text("TRAFFIC", 12, "#9BAECD"));
        var trafficChoice = Combo(new[] { "Global", "Rules" }, settings.Traffic, value => { settings.Traffic = value; Changed(); });
        traffic.Children.Add(trafficChoice); traffic.Children.Add(Text("Default: " + (settings.Traffic == "Global" ? "Tunnel" : "Direct"), 12, "#9BAECD"));
        var right = Card(traffic); right.Margin = new Thickness(7, 0, 0, 13); Grid.SetColumn(right, 1); modes.Children.Add(right); body.Children.Add(modes);
        var center = Stack(); center.HorizontalAlignment = HorizontalAlignment.Center;
        status = Text(backend.State, 22); status.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(status);
        connect = AsyncAction("Connect", async () =>
        {
            if (backend.Running && !dirty && backend.State == "Proxy conflict") await backend.ReclaimProxyAsync();
            else if (backend.Running && !dirty) await backend.StopAsync();
            else { await backend.StartAsync(settings.Snapshot()); dirty = false; }
            Refresh();
        });
        connect.Width = connect.Height = 184; connect.FontSize = 21;
        connect.Background = Color("#13C49A"); connect.Foreground = Color("#06241F");
        connect.Template = (ControlTemplate)XamlReader.Parse("""
          <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
            <Grid><Ellipse Fill="{TemplateBinding Background}" Stroke="#194F54" StrokeThickness="10"/>
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Grid>
          </ControlTemplate>
          """);
        center.Children.Add(connect);
        detail = Text(backend.Detail, 13, "#9BAECD"); detail.TextAlignment = TextAlignment.Center; detail.MaxWidth = 405;
        center.Children.Add(detail); exit = Text("Exit IP: —", 14, "#59DCB8"); exit.TextAlignment = TextAlignment.Center; center.Children.Add(exit);
        captureDetail = Text(backend.CaptureStatus, 12, "#9BAECD"); captureDetail.TextAlignment = TextAlignment.Center; center.Children.Add(captureDetail);
        draftNotice = Text("", 13, "#F1C77A"); draftNotice.TextAlignment = TextAlignment.Center; center.Children.Add(draftNotice); body.Children.Add(center);
        var listeners = Stack(); listeners.Children.Add(Text("Local app proxy", 16));
        listeners.Children.Add(Text("HTTP  127.0.0.1:" + settings.HttpPort + "     SOCKS5  127.0.0.1:" + settings.SocksPort, 14, "#B6CAE6"));
        listeners.Children.Add(Text(settings.SystemProxy ? "Connect applies the Windows browser proxy after tunnel verification. Disconnect restores the previous setting." : "Manual mode: set the app's own HTTP / SOCKS proxy. Regular browser traffic is not captured automatically.", 12, "#9BAECD"));
        var tools = new WrapPanel(); tools.Children.Add(Action("Copy HTTP proxy", () => Clipboard.SetText("http://127.0.0.1:" + (backend.Active?.HttpPort ?? settings.HttpPort))));
        tools.Children.Add(Action("Test browser ↗", OpenTestBrowser));
        tools.Children.Add(AsyncAction("Use Personal VPN proxy", backend.ReclaimProxyAsync));
        tools.Children.Add(AsyncAction("Disconnect", backend.StopAsync)); listeners.Children.Add(tools); body.Children.Add(Card(listeners));
        var protocol = Action(settings.Protocol + (settings.Protocol == "REALITY" && settings.ChainReality ? " · via existing proxy" : "") + "   •   Configure ›", () => Navigate("VPN"));
        body.Children.Add(protocol); body.Children.Add(Text("Preview 0.2.4 · Closing disconnects and restores the previous proxy.", 11, "#8395B2"));
        return body;
    }
    private static string Country(string code) => code switch { "JP" => "◉ JP", "SG" => "◒ SG", "US" => "★ US", "KR" => "◉ KR", "TH" => "▰ TH", _ => "◎ " + code };
    private UIElement Profiles()
    {
        var body = Stack();
        var controls = new DockPanel(); var back = Action("←  Back", () => Navigate("Home")); DockPanel.SetDock(back, Dock.Left); controls.Children.Add(back);
        var add = Action("+  Import profile", Import); DockPanel.SetDock(add, Dock.Right); controls.Children.Add(add); body.Children.Add(controls);
        body.Children.Add(Text("Import the folder exported by vpnctl. Credentials are encrypted for your Windows account. An IP alone is not a connection profile.", 13, "#9BAECD"));
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
            })); row.Children.Add(actions); body.Children.Add(Card(row));
        }
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
        var dialog = new Window { Owner = this, Title = "Edit server", Width = 410, Height = 320, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
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
    private UIElement Rules()
    {
        var body = Stack();
        body.Children.Add(Text("Block → Websites → IP / Networks → default", 14, "#B5C9E5"));
        body.Children.Add(Text("Global defaults to Tunnel. Rules defaults to Direct. Explicit exceptions apply in both modes. Changes need Apply & reconnect.", 12, "#9BAECD"));
        var apps = Stack(); apps.Children.Add(Text("Apps", 19));
        apps.Children.Add(Text("Use an app's HTTP / SOCKS proxy setting to opt in. Whole-machine rules by executable need TUN and are not enabled in this preview.", 13, "#9BAECD")); body.Children.Add(Card(apps));
        body.Children.Add(RuleEditor("Websites", settings.Websites, true));
        body.Children.Add(RuleEditor("IP / Networks", settings.Networks, false));
        body.Children.Add(Text("Domains include subdomains. IP rules match literal IP destinations; no local DNS lookup is added to match website hostnames. No path / URL matching.", 12, "#9BAECD"));
        body.Children.Add(AsyncAction("Apply & reconnect", async () => { Store.Save(settings); await backend.StartAsync(settings.Snapshot()); dirty = false; Navigate("Home"); }));
        return body;
    }
    private UIElement RuleEditor(string title, CategoryRule category, bool domains)
    {
        var panel = Stack(); panel.Children.Add(Text(title, 19));
        var mode = Combo(Policy.Modes, category.Mode, _ => { }); panel.Children.Add(mode);
        var entries = new TextBox { Text = string.Join(Environment.NewLine, category.Entries), AcceptsReturn = true, Height = 100, MaxLength = 64000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        panel.Children.Add(entries); panel.Children.Add(Text(domains ? "One domain per line · example.com" : "One IP / CIDR per line · 203.0.113.0/24", 12, "#9BAECD"));
        panel.Children.Add(Action("Save " + title, () =>
        {
            var next = new CategoryRule { Mode = (string)mode.SelectedItem, Entries = entries.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            Policy.Validate(next, domains); category.Mode = next.Mode; category.Entries = next.Entries; Changed();
            MessageBox.Show(this, backend.Running ? "Saved as draft. Use Apply & reconnect to activate it." : "Saved.", "Rules");
        })); return Card(panel);
    }
    private UIElement Vpn()
    {
        var body = Stack(); var protocol = Stack(); protocol.Children.Add(Text("VPN protocol", 19));
        protocol.Children.Add(Combo(new[] { "Hysteria 2", "REALITY" }, settings.Protocol, value => { settings.Protocol = value; Changed(); Navigate("VPN"); }));
        protocol.Children.Add(Text(settings.Protocol == "Hysteria 2"
            ? "UDP 443 · Verified full-certificate SHA-256 pin. UDP availability depends on the network. Failed authentication never falls back to Direct."
            : "TCP 443 · VLESS + REALITY + Vision. TCP loss can reduce throughput. Direct reachability must be tested on the current network.", 13, "#9BAECD"));
        if (settings.Protocol == "REALITY")
        {
            var chained = new CheckBox { Content = "Connect REALITY through an existing local HTTP proxy", IsChecked = settings.ChainReality };
            chained.Click += (_, _) => { settings.ChainReality = chained.IsChecked == true; Changed(); }; protocol.Children.Add(chained);
            protocol.Children.Add(Text("Upstream: 127.0.0.1:" + settings.UpstreamPort + " · requires that other proxy to remain running.", 12, "#9BAECD"));
        }
        body.Children.Add(Card(protocol));
        var browser = Stack(); browser.Children.Add(Text("Browser capture", 19));
        var systemProxy = new CheckBox { Content = "Use Windows system proxy after Connect", IsChecked = settings.SystemProxy };
        systemProxy.Click += (_, _) => { settings.SystemProxy = systemProxy.IsChecked == true; Changed(); }; browser.Children.Add(systemProxy);
        browser.Children.Add(Text("Captures HTTP / HTTPS from browsers that follow Windows proxy settings. Previous manual / PAC / auto-detect settings are saved and restored. An independent watchdog restores them after an app crash. Changes need Apply & reconnect.", 13, "#9BAECD"));
        browser.Children.Add(Text("Apps with their own proxy settings, extensions, UDP / WebRTC or a separate VPN path may bypass system proxy. This is not a full-device TUN.", 13, "#9BAECD")); body.Children.Add(Card(browser));
        var recovery = Stack(); recovery.Children.Add(Text("Recovery", 19));
        recovery.Children.Add(Text("Network changes are debounced. The supervisor cancels old checks, restarts owned cores and re-verifies HTTPS. Backoff: up to 30s + jitter. Existing TCP sessions may need a new request.", 13, "#9BAECD"));
        recovery.Children.Add(Text("Connected means a recent end-to-end check passed; a failed check shows Degraded, then Reconnecting. A public health endpoint outage can affect this result.", 13, "#9BAECD")); body.Children.Add(Card(recovery));
        var capture = Stack(); capture.Children.Add(Text("Capture & mesh", 19));
        capture.Children.Add(Text("System proxy or manual app proxy in this release. TUN, Windows service, system kill switch and executable interception remain unavailable.", 13, "#9BAECD"));
        capture.Children.Add(Text("No adapter, route, DNS or firewall changes. Tailscale / ZeroTier keep existing OS routes; tunneling their outer transport is not implemented or verified.", 13, "#9BAECD")); body.Children.Add(Card(capture));
        body.Children.Add(AsyncAction("Apply & reconnect", async () => { await backend.StartAsync(settings.Snapshot()); dirty = false; Navigate("Home"); }));
        return body;
    }
    private void BuildLogs()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(Text("VPN backend only · session memory · max 1,000 records / 1 MiB\nClosing and reopening starts a fresh log session.", 13, "#9BAECD"));
        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var clear = Action("Clear", () => { backend.Log.Clear(); lastLogText = ""; if (logText != null) logText.Text = ""; Refresh(); }); tools.Children.Add(clear);
        tools.Children.Add(Action("Export", () =>
        {
            var picker = new SaveFileDialog { FileName = "personal-vpn-session.json", Filter = "JSON|*.json" };
            if (picker.ShowDialog(this) == true) File.WriteAllText(picker.FileName, JsonSerializer.Serialize(backend.Log.Snapshot(), Store.JsonOptions));
        })); logCount = Text("0 / 1,000", 12, "#9BAECD"); tools.Children.Add(logCount); Grid.SetRow(tools, 1); grid.Children.Add(tools);
        logText = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, AcceptsReturn = true, MinHeight = 0 };
        Grid.SetRow(logText, 2); grid.Children.Add(logText); content.Content = grid; Refresh();
    }
    private void Refresh()
    {
        if (status != null) { status.Text = backend.State; status.Foreground = Color(backend.State == "Connected" ? "#57DCB5" : "#D7E4F8"); }
        if (detail != null) detail.Text = backend.Detail;
        if (exit != null) exit.Text = "Exit IP: " + backend.ExitIp;
        if (captureDetail != null) captureDetail.Text = backend.CaptureStatus;
        if (connect != null) { connect.Content = backend.Running ? dirty ? "Apply &\nreconnect" : backend.State == "Proxy conflict" ? "Use proxy" : "Disconnect" : "Connect"; connect.IsEnabled = settings.Profiles.Count > 0; }
        if (draftNotice != null) draftNotice.Text = dirty ? "Settings changed · active tunnel still uses the previous snapshot." : "";
        if (page == "Logs" && logText != null)
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
    private void OpenTestBrowser()
    {
        if (backend.State != "Connected") throw new InvalidOperationException("Connect and wait for a verified connection first.");
        string binary = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Edge or Chrome is required for the test browser.");
        string profile = Path.Combine(Store.Root, "test-browser"); Store.SecureDirectory(profile);
        var start = new ProcessStartInfo(binary) { UseShellExecute = false };
        foreach (string arg in new[] { "--user-data-dir=" + profile, "--proxy-server=http://127.0.0.1:" + backend.Active!.HttpPort, "--proxy-bypass-list=<-loopback>", "--disable-quic", "--disable-background-networking", "--no-first-run", "--new-window", "https://api.ipify.org/?format=json" }) start.ArgumentList.Add(arg);
        Process.Start(start);
        MessageBox.Show(this, "A separate browser profile uses this app's HTTP proxy. This does not configure your regular browser.\n\nThe address page should show your VPS exit. Use this window to try other websites. Close it before switching proxy ports or reconnecting. This is an app proxy test, not a system kill switch.", "Test browser");
    }
}
