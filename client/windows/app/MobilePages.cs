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
        bool compact = lastCompactHome = Height < 680;
        var body = PageRows(Auto, Auto, Star, Auto);
        var profile = settings.Profiles.FirstOrDefault(x => x.Id == settings.SelectedId);
        var card = new Grid(); card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); card.ColumnDefinitions.Add(new ColumnDefinition()); card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var flag = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Color("#F4F7FB"), VerticalAlignment = VerticalAlignment.Center };
        flag.Child = profile?.Country == "JP" ? new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = Color("#CC2944") } : new TextBlock { Text = profile?.Country ?? "+", Foreground = Color("#23334C"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
        card.Children.Add(flag);
        var labels = Stack(); var name = Text(profile?.Name ?? "Add your server", 14); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(name);
        var endpointLabel = Text(profile == null ? "Import a device profile" : DisplayEndpoint(profile), 11, "#9BAECD"); endpointLabel.TextWrapping = TextWrapping.NoWrap; endpointLabel.TextTrimming = TextTrimming.CharacterEllipsis; endpointLabel.ToolTip = endpointLabel.Text; labels.Children.Add(endpointLabel); Grid.SetColumn(labels, 1); card.Children.Add(labels);
        if (profile != null) { var measured = LatencyLabel(profile); Grid.SetColumn(measured,2); card.Children.Add(measured); }
        var arrow = Text("›", 24, "#86B8FF"); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 3); card.Children.Add(arrow);
        var server = Action("", () => Navigate("Profiles")); server.Content = card; server.HorizontalContentAlignment = HorizontalAlignment.Stretch; server.Name = "ServerCard"; server.Height = compact ? 60 : 75; if (compact) server.Padding = new Thickness(12,6,12,6); server.Margin = new Thickness(0, 0, 0, 8); Put(body, server, 0);
        var capture = Stack(); capture.Children.Add(CaptureSelector());
        var protocols = new[] { "REALITY", "Hysteria 2" }.Where(name => name == settings.Protocol ||
            (name == "REALITY" ? profile?.Xray != null : profile?.Hysteria != null));
        var protocol = Combo(protocols, settings.Protocol, async value =>
        {
            try { await SelectProtocol(value); }
            catch (Exception error) { ShowError(error); }
            finally { if (page == "Home") Navigate("Home"); }
        });
        protocol.Name = "HomeProtocol"; protocol.Margin = compact ? new Thickness(0) : new Thickness(0, 1, 0, 3); capture.Children.Add(protocol); Put(body, capture, 1);
        var center = PageRows(Auto, Star, Auto, Auto, Auto); center.Margin = new Thickness(0, 3, 0, 8);
        status = Text(backend.State, compact ? 20 : 22); if (compact) status.Margin = new Thickness(0); status.TextAlignment = TextAlignment.Center; Put(center, status, 0);
        connect = new Button { Content = "Connect" };
        connect.Click += async (_, _) => { try { await ToggleConnection(); } catch (Exception error) { ShowError(error); } };
        connect.Name = "ConnectButton"; connect.Width = connect.Height = 172; connect.FontSize = 18; connect.Margin = new Thickness(0); connect.Background = Color("#14C7A0"); connect.Foreground = Color("#052D28");
        connect.Template = (ControlTemplate)XamlReader.Parse("""
          <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
            <Grid>
              <Ellipse Margin="5" Fill="{TemplateBinding BorderBrush}" Opacity="0.18"><Ellipse.Effect><DropShadowEffect Color="{Binding BorderBrush.Color, RelativeSource={RelativeSource TemplatedParent}}" BlurRadius="18" ShadowDepth="0" Opacity="0.7"/></Ellipse.Effect></Ellipse>
              <Ellipse Margin="9,12,9,6" Fill="#040C17"><Ellipse.Effect><DropShadowEffect BlurRadius="13" ShadowDepth="7" Opacity="0.65"/></Ellipse.Effect></Ellipse>
              <Ellipse x:Name="PowerSurface" Margin="10" Fill="{TemplateBinding Background}" Stroke="#70EBFFF5" StrokeThickness="1"/>
              <Ellipse Margin="11" IsHitTestVisible="False"><Ellipse.Fill><LinearGradientBrush StartPoint="0,0" EndPoint="0,1"><GradientStop Color="#1FFFFFFF" Offset="0"/><GradientStop Color="#00FFFFFF" Offset="0.5"/><GradientStop Color="#18000000" Offset="1"/></LinearGradientBrush></Ellipse.Fill></Ellipse>
              <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                <Path Data="M 12,1 L 12,11 M 5,5 C -1,11 3,22 12,22 C 21,22 25,11 19,5" Stroke="{TemplateBinding Foreground}" StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Width="25" Height="25" Stretch="Uniform" Margin="0,0,0,10"/>
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" TextBlock.TextAlignment="Center"/>
              </StackPanel>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="PowerSurface" Property="Stroke" Value="#D0F5F8FF"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="PowerSurface" Property="StrokeThickness" Value="2"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="PowerSurface" Property="Stroke" Value="#F5F8FF"/><Setter TargetName="PowerSurface" Property="StrokeThickness" Value="2"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate>
          """);
        Put(center, new Viewbox { Child = connect, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = compact ? 140 : 184, Margin = compact ? new Thickness(0,2,0,3) : new Thickness(0, 4, 0, 5) }, 1);
        detail = Text(backend.Detail, 11, "#9BAECD"); detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; detail.TextAlignment = TextAlignment.Center; detail.MaxHeight = 35; detail.TextTrimming = TextTrimming.CharacterEllipsis; Put(center, detail, 2);
        exit = Text("Exit IP: —", 13, "#59DCB8"); if (compact) exit.Margin = new Thickness(0,2,0,2); exit.TextAlignment = TextAlignment.Center; Put(center, exit, 3);
        draftNotice = Text("", 11, "#F1C77A"); draftNotice.TextAlignment = TextAlignment.Center; Put(center, draftNotice, 4); Put(body, center, 2);
        var traffic = (FrameworkElement)TrafficPanel(compact); traffic.Name = "TrafficCard"; Put(body, traffic, 3); return body;
    }
    private async Task SelectProtocol(string selected)
    {
        if (selected == settings.Protocol || PrimaryConnectionAction() is ConnectionAction.Cancel or ConnectionAction.Stopping || quitInProgress) return;
        var profile = settings.Profiles.FirstOrDefault(item => item.Id == settings.SelectedId);
        if (selected == "REALITY" ? profile?.Xray == null : profile?.Hysteria == null) return;
        bool reconnect = backend.Running;
        if (reconnect && MessageBox.Show(this, "Reconnect using " + selected + "?", "Switch protocol", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        settings.Protocol = selected;
        settings.ChainReality = false;
        Changed();
        Navigate("Home");
        if (reconnect) await ConnectSelected();
    }
    private UIElement SettingsPage()
    {
        var body = PageRows(Auto, Star, Auto); Put(body, Text("CONNECTION & ROUTING", 10, "#9BAECD"), 0);
        var rows = Stack(); rows.Children.Add(SettingsRow("VPN configure", settings.Protocol + " · protocol & connection options", "VpnConfig"));
        var appTunnel = SettingsRow("App Tunnel", settings.CaptureMode == "Proxy" ? "Unavailable in Proxy · requires TUN mode" : "Executable exceptions · App rules win", "AppTunnel");
        appTunnel.Name = "AppTunnelSettings"; appTunnel.IsEnabled = settings.CaptureMode == "TUN" && CaptureCapabilities.TunAvailable; rows.Children.Add(appTunnel);
        rows.Children.Add(SettingsRow("Site Tunnel", "Domain / IP exceptions · " + ModeName(settings.Sites!.Mode), "SiteTunnel"));
        rows.Children.Add(SettingsRow("Backend logs", "Connection events", "Diagnostics")); Put(body, rows, 1);
        Put(body, Text("PERSONAL VPN  " + ClientVersion.Current + "\nProxy / TUN · Administrator", 11, "#8395B2"), 2); return body;
    }
    private Button SettingsRow(string title, string description, string target)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var labels = Stack(); labels.Children.Add(Text(title, 15)); labels.Children.Add(Text(description, 11, "#9BAECD")); Grid.SetColumn(labels,1); grid.Children.Add(labels);
        UIElement icon;
        if (target == "AppTunnel")
        {
            var boxes = new GeometryGroup();
            foreach (double x in new[] { 1d,12 }) foreach (double y in new[] { 1d,12 }) boxes.Children.Add(new RectangleGeometry(new Rect(x,y,7,7),1,1));
            boxes.Freeze(); icon = new System.Windows.Shapes.Path { Data=boxes,Stroke=Color("#91BEF4"),StrokeThickness=1.4,Width=21,Height=21,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left };
        }
        else icon = new TextBlock { Text = target switch { "SiteTunnel" => "\uE774", "Diagnostics" => "\uE9D9", _ => "\uE713" }, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize=21, Foreground=Color("#91BEF4"), VerticalAlignment=VerticalAlignment.Center };
        grid.Children.Add(icon);
        var arrow = Text("›", 23, "#86B8FF"); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 2); grid.Children.Add(arrow);
        var button = Action("", () => Navigate(target)); button.Content = grid; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(14, 11, 14, 11); button.Margin = new Thickness(0, 4, 0, 6); return button;
    }
    private async Task ApplyPolicy()
    {
        settings.Validate(); Changed(); if (backend.Running) await ConnectSelected(); Navigate("Home");
    }
    private static void ValidateHost(string value)
    {
        if (!IPAddress.TryParse(value, out _)) Policy.Validate(new CategoryRule { Entries = new() { value } }, true);
    }
    private UIElement ProtocolPage()
    {
        var body = PageRows(Auto, Star, Auto);
        Put(body, Text(settings.Protocol + " settings", 15), 0);
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
        var ipv6 = new CheckBox { Content = "IPv6 DNS in TUN (requires VPS IPv6)", IsChecked = settings.AllowIpv6Dns }; form.Children.Add(ipv6);
        form.Children.Add(Text("IPv4 DNS is the default for VPS compatibility. Enable IPv6 only after its Internet path is working.", 11, "#9BAECD"));
        form.Children.Add(Text("Capture: " + settings.CaptureMode, 11, "#9BAECD"));
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
            settings.AllowIpv6Dns = ipv6.IsChecked == true;
            await ApplyPolicy();
        }), 2); return body;
    }
    private UIElement TunnelEditor(bool apps)
    {
        var policy = apps ? settings.Apps : settings.Sites!; string target = apps ? "AppTunnel" : "SiteTunnel";
        var body = PageRows(Auto, Star, Auto); var header = Stack();
        if (apps && (settings.CaptureMode != "TUN" || !CaptureCapabilities.TunAvailable)) body.IsEnabled = false;
        header.Children.Add(Text(apps && settings.CaptureMode != "TUN" ? "TUN REQUIRED" : "DEFAULT ACTION + EXCEPTIONS", 10, apps ? "#E9BB72" : "#9BAECD"));
        var modeLabels = apps ? ModeValues.Select(mode => mode == "Bypass" ? "Direct via TUN by default" : ModeName(mode)).ToArray() : ModeLabels;
        header.Children.Add(Combo(modeLabels, modeLabels[Math.Max(0, Array.IndexOf(ModeValues, policy.Mode))], label => { policy.Mode = ModeValues[Array.IndexOf(modeLabels, label)]; Changed(!apps || settings.CaptureMode == "TUN"); Navigate(target); }));
        header.Children.Add(Text(apps ? "App exceptions win over site exceptions. Executable rules apply in TUN mode." : "Exceptions override the default. Rules off ignores this list and tunnels captured traffic.", 11, "#9BAECD"));
        header.Children.Add(Action(apps ? "+ Choose app.exe" : "Add site group", () => EditException(policy, apps, null)));
        if (!apps)
        {
            var transfer = new Grid();
            transfer.ColumnDefinitions.Add(new ColumnDefinition());
            transfer.ColumnDefinitions.Add(new ColumnDefinition());
            var import = AsyncAction("Import", ImportSiteTunnel); import.Name = "ImportSiteTunnel"; transfer.Children.Add(import);
            var export = AsyncAction("Export", ExportSiteTunnel); export.Name = "ExportSiteTunnel"; Grid.SetColumn(export, 1); transfer.Children.Add(export);
            header.Children.Add(transfer);
        }
        Put(body, header, 0);
        FrameworkElement list = policy.Entries.Count == 0 ? Text(apps ? "No app exceptions yet." : "No site groups yet.", 12, "#9BAECD") : ExceptionList(policy, apps);
        if (policy.Mode == "Disabled") { list.Opacity = .5; list.ToolTip = "Saved exceptions are ignored while rules are off."; }
        Put(body, Card(list), 1); var footer = Stack(); footer.Children.Add(Text(policy.Entries.Count + (apps ? " app exceptions · not applied to Proxy" : " site groups · explicit Block exceptions first"), 10, "#9BAECD"));
        footer.Children.Add(apps && settings.CaptureMode != "TUN" ? Action("Save draft", () => { Policy.ValidateTunnel(policy, true); Changed(false); Navigate("Settings"); }) : AsyncAction("Save & apply", ApplyPolicy)); Put(body, footer, 2); return body;
    }
    private void EditException(TunnelPolicy policy, bool apps, TunnelException? previous)
    {
        string? selected = null;
        if (apps && previous == null) { var file = new OpenFileDialog { Filter = "Windows app (*.exe)|*.exe", CheckFileExists = true }; if (file.ShowDialog(this) != true) return; selected = file.FileName; }
        var panel = Stack(); panel.Children.Add(Text(apps ? "App executable" : "Target type", 13));
        var kind = Combo(apps ? new[] { "App" } : new[] { "Domain", "IP" }, previous?.Kind ?? (apps ? "App" : "Domain"), _ => { }); if (!apps) panel.Children.Add(kind);
        var name = new TextBox { Name="SiteGroupName", Text = previous?.Name ?? "", MaxLength = 80 };
        if (!apps) { panel.Children.Add(Text("Group name (optional)", 13)); panel.Children.Add(name); panel.Children.Add(Text("Targets", 13)); }
        var value = new TextBox { Name="ExceptionTargets", Text = previous?.Value ?? selected ?? "", MaxLength = apps ? 1024 : SiteTargets.InputLimit, AcceptsReturn = !apps, MinHeight = apps ? 0 : 100, MaxHeight = 180, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(value);
        panel.Children.Add(Text(apps ? "Full .exe path." : "Separate targets with commas or new lines. Domain includes subdomains. IP supports IPv4, IPv6, CIDR and trailing wildcards such as 2.2.2.*.", 11, "#9BAECD")); panel.Children.Add(Text("Action", 13));
        var actions = apps ? new[] { "Tunnel", "Bypass", Policy.NativeBypassAction, "Block" } : new[] { "Tunnel", "Bypass", "Block" };
        bool nativeBypassAvailable = NativeBypass.Available;
        var action = new ComboBox { Name = "ExceptionAction", MinWidth = 125, SelectedValuePath = nameof(ComboBoxItem.Tag) };
        foreach (string key in actions)
        {
            action.Items.Add(new ComboBoxItem
            {
                Content = Policy.ActionLabel(key, apps),
                Tag = key,
                IsEnabled = key != Policy.NativeBypassAction || nativeBypassAvailable
            });
        }
        action.SelectedValue = previous?.Action ?? "Tunnel";
        panel.Children.Add(action);
        if (apps)
        {
            string hint = nativeBypassAvailable
                ? "Bypass VPN · Windows test build. The reference driver can also exclude child processes. System DNS uses its own route."
                : "Bypass VPN requires a signed Windows driver. Direct via TUN sends traffic directly after capture.";
            panel.Children.Add(Text(hint, 11, "#E9BB72"));
        }
        var error = Text("", 12, "#F29C9F"); panel.Children.Add(error);
        var save = Action("Save exception", () =>
        {
            if (action.SelectedItem is not ComboBoxItem { IsEnabled: true, Tag: string selectedAction }) return;
            try
            {
                var next = new TunnelException { Kind = (string)kind.SelectedItem, Name = apps ? "" : name.Text.Trim(), Value = apps ? value.Text.Trim() : SiteTargets.Clean((string)kind.SelectedItem, value.Text), Action = selectedAction };
                var entries = policy.Entries.ToList(); if (previous == null) entries.Add(next); else entries[entries.IndexOf(previous)] = next;
                Policy.ValidateTunnel(new TunnelPolicy { Mode = policy.Mode, Entries = entries }, apps);
                policy.Entries = entries;
                Changed(!apps || settings.CaptureMode == "TUN"); CloseSheet(); Navigate(apps ? "AppTunnel" : "SiteTunnel");
            }
            catch (InvalidOperationException e) { error.Text = e.Message; }
        });
        void UpdateSave() => save.IsEnabled = action.SelectedItem is ComboBoxItem { IsEnabled: true };
        action.SelectionChanged += (_, _) => UpdateSave();
        UpdateSave();
        panel.Children.Add(save);
        ShowSheet(previous == null ? "Add exception" : "Edit exception", panel);
    }
    internal FrameworkElement LayoutForTest(string target, Size size)
    {
        if (IsVisible) { Width = size.Width; Height = size.Height; }
        Navigate(target); layoutRoot.InvalidateMeasure(); layoutRoot.Measure(size); layoutRoot.Arrange(new Rect(size)); layoutRoot.UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); return layoutRoot;
    }
    internal FrameworkElement RuleEditorForTest(Size size, bool apps = false)
    {
        LayoutForTest(apps ? "AppTunnel" : "SiteTunnel", size);
        var policy = apps ? settings.Apps : settings.Sites!;
        EditException(policy, apps, policy.Entries[0]);
        layoutRoot.UpdateLayout();
        return layoutRoot;
    }
    internal void FillPreviewLogs() { for (int i = 0; i < 200; i++) backend.Log.Add("INFO", "backend.retry", "Synthetic layout preview · retry result " + i); }
    internal void StopPreview() { exiting = true; timer.Stop(); backend.DisposeAsync().AsTask().GetAwaiter().GetResult(); Close(); }
}
