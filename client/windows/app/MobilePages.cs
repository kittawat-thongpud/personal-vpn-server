using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace PersonalVpn;

public sealed partial class MainWindow
{
    private static Grid PageRows(params GridLength[] heights)
    {
        var grid = new Grid(); foreach (var height in heights) grid.RowDefinitions.Add(new RowDefinition { Height = height }); return grid;
    }
    private static readonly GridLength Auto = GridLength.Auto, Star = new(1, GridUnitType.Star);
    private static void Put(Grid grid, UIElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
    private static ScrollViewer WidgetScroll(UIElement child) => new() { Style = (Style)Application.Current.FindResource(typeof(ScrollViewer)), Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
    private static readonly string[] ModeLabels = { "Tunnel by default", "Bypass by default", "Block by default", "Rules off · tunnel all" };
    private static readonly string[] ModeValues = { "Tunnel", "Bypass", "Block", "Disabled" };
    private static string ModeName(string mode) => ModeLabels[Math.Max(0, Array.IndexOf(ModeValues, mode))];
    private string DisplayEndpoint(ServerProfile profile)
    {
        if (settings.Protocol != "REALITY" || profile.Xray == null) return profile.Endpoint;
        var remote = JsonNode.Parse(profile.Xray)!["outbounds"]![0]!["settings"]!["vnext"]![0]!;
        return remote["address"] + ":" + remote["port"];
    }
    private UIElement MobileHome()
    {
        var body = PageRows(Auto, Auto, Star, Auto);
        var profile = settings.Profiles.FirstOrDefault(x => x.Id == settings.SelectedId);
        var card = new Grid(); card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); card.ColumnDefinitions.Add(new ColumnDefinition()); card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var flag = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Color("#F4F7FB"), VerticalAlignment = VerticalAlignment.Center };
        flag.Child = profile?.Country == "JP" ? new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = Color("#CC2944") } : new TextBlock { Text = profile?.Country ?? "+", Foreground = Color("#23334C"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
        card.Children.Add(flag);
        var labels = Stack(); var name = Text(profile?.Name ?? "Add your server", 14); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(name);
        labels.Children.Add(Text(profile == null ? "Import a device profile" : DisplayEndpoint(profile), 11, "#9BAECD")); Grid.SetColumn(labels, 1); card.Children.Add(labels);
        var arrow = Text("›", 24, "#86B8FF"); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 2); card.Children.Add(arrow);
        var server = Action("", () => Navigate("Profiles")); server.Content = card; server.HorizontalContentAlignment = HorizontalAlignment.Stretch; server.Name = "ServerCard"; server.Height = 75; server.Margin = new Thickness(0, 0, 0, 8); Put(body, server, 0);
        var capture = Stack(); capture.Children.Add(CaptureSelector());
        var badge = Text(settings.Protocol + "  ·  " + ModeName(settings.Sites!.Mode), 11, "#9BAECD"); badge.TextAlignment = TextAlignment.Center; badge.Margin = new Thickness(0, 1, 0, 3); capture.Children.Add(badge); Put(body, capture, 1);
        var center = PageRows(Auto, Star, Auto, Auto, Auto); center.Margin = new Thickness(0, 3, 0, 8);
        status = Text(backend.State, 22); status.TextAlignment = TextAlignment.Center; Put(center, status, 0);
        connect = AsyncAction("Connect", ToggleConnection);
        connect.Name = "ConnectButton"; connect.Width = connect.Height = 172; connect.FontSize = 18; connect.Margin = new Thickness(0); connect.Background = Color("#14C7A0"); connect.Foreground = Color("#052D28");
        connect.Template = (ControlTemplate)XamlReader.Parse("""
          <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
            <Grid><Ellipse Fill="#173F49"/><Ellipse Margin="9" Fill="{TemplateBinding Background}"/>
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.86"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="Foreground" Value="White"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate>
          """);
        Put(center, new Viewbox { Child = connect, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 184, Margin = new Thickness(0, 4, 0, 5) }, 1);
        detail = Text(backend.Detail, 11, "#9BAECD"); detail.TextAlignment = TextAlignment.Center; detail.MaxHeight = 35; detail.TextTrimming = TextTrimming.CharacterEllipsis; Put(center, detail, 2);
        exit = Text("Exit IP: —", 13, "#59DCB8"); exit.TextAlignment = TextAlignment.Center; Put(center, exit, 3);
        draftNotice = Text("", 11, "#F1C77A"); draftNotice.TextAlignment = TextAlignment.Center; Put(center, draftNotice, 4); Put(body, center, 2);
        var traffic = (FrameworkElement)TrafficPanel(); traffic.Name = "TrafficCard"; Put(body, traffic, 3); return body;
    }
    private UIElement SettingsPage()
    {
        var body = PageRows(Auto, Star, Auto); Put(body, Text("CONNECTION & ROUTING", 10, "#9BAECD"), 0);
        var rows = Stack(); rows.Children.Add(SettingsRow("VPN configure", settings.Protocol + " · protocol & connection options", "VpnConfig"));
        var appTunnel = SettingsRow("App Tunnel", settings.CaptureMode == "Proxy" ? "Unavailable in Proxy · requires TUN mode" : "Executable exceptions · App rules win", "AppTunnel");
        appTunnel.Name = "AppTunnelSettings"; appTunnel.IsEnabled = settings.CaptureMode == "TUN" && CaptureCapabilities.TunAvailable; rows.Children.Add(appTunnel);
        rows.Children.Add(SettingsRow("Site Tunnel", "Domain / IP exceptions · " + ModeName(settings.Sites!.Mode), "SiteTunnel"));
        rows.Children.Add(SettingsRow("Backend logs", "Session only · up to 1,000 records", "Diagnostics")); Put(body, rows, 1);
        Put(body, Text("PERSONAL VPN  0.4.0\nProxy capture · Wintun module in development", 11, "#8395B2"), 2); return body;
    }
    private Button SettingsRow(string title, string description, string target)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var labels = Stack(); labels.Children.Add(Text(title, 15)); labels.Children.Add(Text(description, 11, "#9BAECD")); grid.Children.Add(labels);
        var arrow = Text("›", 23, "#86B8FF"); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 1); grid.Children.Add(arrow);
        var button = Action("", () => Navigate(target)); button.Content = grid; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(14, 11, 14, 11); button.Margin = new Thickness(0, 4, 0, 6); return button;
    }
    private async Task ApplyPolicy()
    {
        settings.Validate(); Changed(); if (backend.Running) await ConnectSelectedProxy(); Navigate("Home");
    }
    private static void ValidateHost(string value)
    {
        if (!IPAddress.TryParse(value, out _)) Policy.Validate(new CategoryRule { Entries = new() { value } }, true);
    }
    private UIElement ProtocolPage()
    {
        var body = PageRows(Auto, Star, Auto);
        Put(body, Combo(new[] { "Hysteria 2", "REALITY" }, settings.Protocol, value => { settings.Protocol = value; Changed(); Navigate("VpnConfig"); }), 0);
        var profile = settings.Profiles.FirstOrDefault(x => x.Id == settings.SelectedId); bool reality = settings.Protocol == "REALITY";
        if (profile == null || (reality ? profile.Xray == null : profile.Hysteria == null))
        {
            Put(body, Text("Import a profile that includes this protocol.", 13, "#9BAECD"), 1); Put(body, Action("Server profiles", () => Navigate("Profiles")), 2); return body;
        }
        var config = JsonNode.Parse(reality ? profile.Xray! : profile.Hysteria!)!;
        var remote = reality ? config["outbounds"]![0]!["settings"]!["vnext"]![0] : null;
        var uri = reality ? null : new Uri("tcp://" + config["server"]!.GetValue<string>());
        var form = Stack(); form.Children.Add(Text(profile.Name, 13, "#86B8FF"));
        form.Children.Add(Text("Server address", 12)); var address = new TextBox { Text = reality ? remote!["address"]!.GetValue<string>() : uri!.Host.Trim('[', ']'), MaxLength = 253 }; form.Children.Add(address);
        form.Children.Add(Text("Server port", 12)); var port = new TextBox { Text = reality ? remote!["port"]!.ToString() : uri!.Port.ToString(), MaxLength = 5 }; form.Children.Add(port);
        form.Children.Add(Text("TLS server name (SNI)", 12)); var sni = new TextBox { Text = reality ? (string?)config["outbounds"]![0]!["streamSettings"]!["realitySettings"]!["serverName"] ?? "" : (string?)config["tls"]?["sni"] ?? "", MaxLength = 253 }; form.Children.Add(sni);
        ComboBox? fingerprint = null; CheckBox? chain = null;
        if (reality)
        {
            form.Children.Add(Text("TLS fingerprint", 12)); fingerprint = Combo(new[] { "chrome", "firefox", "edge", "safari" }, (string?)config["outbounds"]![0]!["streamSettings"]!["realitySettings"]!["fingerprint"] ?? "chrome", _ => { }); form.Children.Add(fingerprint);
            chain = new CheckBox { Content = "Use existing local HTTP proxy (advanced)", IsChecked = settings.ChainReality }; form.Children.Add(chain);
            form.Children.Add(Text("VLESS + REALITY + Vision · TCP\nDevice credentials remain in the imported profile.", 11, "#9BAECD"));
        }
        else form.Children.Add(Text("Hysteria 2 · QUIC / UDP\nFull-certificate pin enforced · BBR congestion control. Credentials and pin remain in the imported profile.", 11, "#9BAECD"));
        form.Children.Add(Text("Capture: Proxy\nApp executable rules require the Wintun module being developed next.", 11, "#9BAECD"));
        Put(body, Card(WidgetScroll(form)), 1);
        Put(body, AsyncAction("Save & apply", async () =>
        {
            string host = address.Text.Trim(), name = sni.Text.Trim(); ValidateHost(host); if (name.Length != 0) ValidateHost(name);
            if (!int.TryParse(port.Text, out int number) || number is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
            if (reality)
            {
                if (name.Length == 0) throw new InvalidOperationException("REALITY needs the SNI accepted by this server.");
                remote!["address"] = host; remote["port"] = number; var tls = config["outbounds"]![0]!["streamSettings"]!["realitySettings"]!;
                tls["serverName"] = name; tls["fingerprint"] = (string)fingerprint!.SelectedItem; profile.Xray = config.ToJsonString(); settings.ChainReality = chain!.IsChecked == true;
            }
            else { config["server"] = (host.Contains(':') ? "[" + host + "]" : host) + ":" + number; config["tls"]!["sni"] = name; profile.Hysteria = config.ToJsonString(); }
            await ApplyPolicy();
        }), 2); return body;
    }
    private UIElement TunnelEditor(bool apps)
    {
        var policy = apps ? settings.Apps : settings.Sites!; string target = apps ? "AppTunnel" : "SiteTunnel";
        var body = PageRows(Auto, Star, Auto); var header = Stack();
        if (apps && (settings.CaptureMode != "TUN" || !CaptureCapabilities.TunAvailable)) body.IsEnabled = false;
        header.Children.Add(Text(apps ? "TUN REQUIRED · SAVED DRAFT ONLY" : "DEFAULT ACTION + EXCEPTIONS", 10, apps ? "#E9BB72" : "#9BAECD"));
        header.Children.Add(Combo(ModeLabels, ModeName(policy.Mode), label => { policy.Mode = ModeValues[Array.IndexOf(ModeLabels, label)]; Changed(!apps); Navigate(target); }));
        header.Children.Add(Text(apps ? "Inactive in Proxy mode. In Wintun, app exceptions win over site exceptions." : "Exceptions override the default. Rules off ignores this list and tunnels captured traffic.", 11, "#9BAECD"));
        header.Children.Add(Action(apps ? "+ Choose app.exe" : "+ Add domain / IP", () => EditException(policy, apps, null))); Put(body, header, 0);
        var list = Stack();
        foreach (var entry in policy.Entries.ToArray())
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            var labels = Stack(); var name = Text(apps ? Path.GetFileName(entry.Value) : entry.Value, 13); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = entry.Value; labels.Children.Add(name);
            labels.Children.Add(Text(entry.Kind + "  →  " + entry.Action, 11, entry.Action == "Block" ? "#F29C9F" : entry.Action == "Tunnel" ? "#58DCB5" : "#8DB8F2")); row.Children.Add(labels);
            var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition());
            var edit = Action("✎", () => EditException(policy, apps, entry)); edit.Padding = new Thickness(2); edit.ToolTip = "Edit exception";
            var remove = Action("×", () => { policy.Entries.Remove(entry); Changed(!apps); Navigate(target); }); remove.Padding = new Thickness(2); remove.ToolTip = "Remove exception"; Grid.SetColumn(remove, 1); buttons.Children.Add(edit); buttons.Children.Add(remove); Grid.SetColumn(buttons, 1); row.Children.Add(buttons);
            var card = Card(row); card.Padding = new Thickness(10, 7, 10, 7); list.Children.Add(card);
        }
        if (policy.Entries.Count == 0) list.Children.Add(Text("No exceptions yet. Add a target and choose Tunnel, Bypass or Block.", 12, "#9BAECD"));
        if (policy.Mode == "Disabled") { list.Opacity = .5; list.ToolTip = "Saved exceptions are ignored while rules are off."; }
        Put(body, Card(WidgetScroll(list)), 1); var footer = Stack(); footer.Children.Add(Text(policy.Entries.Count + " / 500 exceptions" + (apps ? " · not applied to Proxy" : " · explicit Block exceptions first"), 10, "#9BAECD"));
        footer.Children.Add(apps ? Action("Save draft", () => { Policy.ValidateTunnel(policy, true); Changed(false); Navigate("Settings"); }) : AsyncAction("Save & apply", ApplyPolicy)); Put(body, footer, 2); return body;
    }
    private void EditException(TunnelPolicy policy, bool apps, TunnelException? previous)
    {
        string? selected = null;
        if (apps && previous == null) { var file = new OpenFileDialog { Filter = "Windows app (*.exe)|*.exe", CheckFileExists = true }; if (file.ShowDialog(this) != true) return; selected = file.FileName; }
        var dialog = new Window { Owner = this, Title = previous == null ? "Add exception" : "Edit exception", Width = 370, Height = 380, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(Text(apps ? "App executable" : "Target type", 13));
        var kind = Combo(apps ? new[] { "App" } : new[] { "Domain", "IP" }, previous?.Kind ?? (apps ? "App" : "Domain"), _ => { }); if (!apps) panel.Children.Add(kind);
        var value = new TextBox { Text = previous?.Value ?? selected ?? "", MaxLength = apps ? 1024 : 253 }; panel.Children.Add(value);
        panel.Children.Add(Text(apps ? "Full .exe path. Child processes need their own entry." : "Domain includes subdomains. IP accepts IPv4, IPv6 or CIDR.", 11, "#9BAECD")); panel.Children.Add(Text("Action", 13));
        var action = Combo(new[] { "Tunnel", "Bypass", "Block" }, previous?.Action ?? "Tunnel", _ => { }); panel.Children.Add(action);
        panel.Children.Add(Action("Save exception", () =>
        {
            var next = new TunnelException { Kind = (string)kind.SelectedItem, Value = value.Text.Trim(), Action = (string)action.SelectedItem };
            var candidate = new TunnelPolicy { Mode = policy.Mode, Entries = policy.Entries.Where(x => !ReferenceEquals(x, previous)).Append(next).ToList() }; Policy.ValidateTunnel(candidate, apps);
            if (previous == null) policy.Entries.Add(next); else policy.Entries[policy.Entries.IndexOf(previous)] = next;
            Changed(!apps); dialog.Close(); Navigate(apps ? "AppTunnel" : "SiteTunnel");
        })); dialog.Content = panel; dialog.ShowDialog();
    }
    internal FrameworkElement LayoutForTest(string target, Size size)
    {
        if (IsVisible) { Width = size.Width; Height = size.Height; }
        Navigate(target); layoutRoot.InvalidateMeasure(); layoutRoot.Measure(size); layoutRoot.Arrange(new Rect(size)); layoutRoot.UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); return layoutRoot;
    }
    internal void FillPreviewLogs() { for (int i = 0; i < 200; i++) backend.Log.Add("INFO", "backend.retry", "Synthetic layout preview · retry result " + i); }
    internal void StopPreview() { exiting = true; timer.Stop(); backend.DisposeAsync().AsTask().GetAwaiter().GetResult(); Close(); }
}
