using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalVpn;

public static class MobileValidation
{
    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static IEnumerable<FrameworkElement> Elements(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is FrameworkElement element) yield return element;
            foreach (var nested in Elements(child)) yield return nested;
        }
    }
    public static List<string> PolicyChecks()
    {
        var checks = new List<string>();
        var legacy = new Settings { Traffic = "Rules", Websites = new CategoryRule { Mode = "Tunnel", Entries = new() { "example.com" } } };
        Policy.Upgrade(legacy); Assert(legacy.Sites!.Mode == "Bypass" && legacy.Sites.Entries.Single().Action == "Tunnel", "Legacy migration changed defaults or exceptions.");
        foreach (string mode in new[] { "Tunnel", "Bypass", "Block", "Disabled" })
        {
            var settings = new Settings { Sites = new TunnelPolicy { Mode = mode, Entries = new() { new() { Kind = "Domain", Value = "example.com", Action = "Tunnel" } } } };
            var rules = Policy.Compile(settings); Assert((string?)rules.Last()?["outboundTag"] == Policy.Tag(mode == "Disabled" ? "Tunnel" : mode), "Site default action failed.");
            Assert(rules.Count == (mode == "Disabled" ? 2 : 3), "Rules-off must retain but ignore exceptions.");
            if (mode == "Block") Assert((string?)rules[1]?["outboundTag"] == "tunnel", "Tunnel exception must override a Block default.");
        }
        checks.Add("site defaults, exception over Block default, rules-off, legacy migration");
        var policy = new Settings { Sites = new TunnelPolicy { Mode = "Block", Entries = new() { new() { Kind = "Domain", Value = "example.com", Action = "Block" } } }, Apps = new TunnelPolicy { Mode = "Tunnel", Entries = new() { new() { Kind = "App", Value = @"C:\Apps\browser.exe", Action = "Bypass" } } } };
        var plan = TunPlan.Compile(policy, new[] { "203.0.113.10", "2001:db8::10" }, new[] { @"C:\PersonalVPN\cores\xray.exe", @"C:\PersonalVPN\cores\hysteria.exe" });
        var tunRules = plan["route"]!["rules"]!.AsArray();
        Assert((string?)tunRules[3]?["outbound"] == "direct" && tunRules[3]?["process_path_regex"] != null && (string?)tunRules[4]?["action"] == "reject", "App exception must beat a conflicting site Block.");
        Assert(plan["inbounds"]![0]!["route_exclude_address"]!.AsArray().Count == 2, "Transport recursion exclusions lost.");
        var before = Policy.Compile(policy).ToJsonString(); policy.Apps.Entries[0].Action = "Block"; Assert(before == Policy.Compile(policy).ToJsonString(), "App draft must never affect Proxy routing.");
        bool rejected = false; try { Policy.ValidateTunnel(new TunnelPolicy { Entries = new() { new() { Kind = "App", Value = "browser.exe" } } }, true); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "An ambiguous executable name was accepted.");
        Assert(System.Text.RegularExpressions.Regex.IsMatch(@"c:\APPS\BROWSER.EXE", TunPlan.PathPattern(@"C:\Apps\browser.exe")), "Windows executable matching must ignore path casing.");
        checks.Add("Wintun plan app-before-site precedence, transport exclusions, Proxy ignores app drafts, full executable paths"); return checks;
    }
    public static int Run(string[] args)
    {
        MainWindow? window = null; var checks = new List<string>();
        try
        {
            checks.AddRange(PolicyChecks()); _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            checks.AddRange(TrayValidation.Checks());
            string output = Path.GetFullPath(Program.Option(args, "--output") ?? throw new InvalidOperationException("Layout output folder required.")); Directory.CreateDirectory(output);
            var profile = new ServerProfile { Name = "Japan · preview", Country = "JP", Hysteria = """{"server":"203.0.113.10:443","auth":"synthetic-test-only","tls":{"sni":"test.example","insecure":true,"pinSHA256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""" };
            var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id };
            Policy.Upgrade(settings);
            for (int i = 0; i < 120; i++) settings.Sites!.Entries.Add(new TunnelException { Kind = "Domain", Value = "site" + i + ".example", Action = i % 2 == 0 ? "Tunnel" : "Block" });
            settings.Apps.Entries.Add(new TunnelException { Kind = "App", Value = @"C:\Apps\browser.exe", Action = "Bypass" });
            window = new MainWindow(settings, preview: true);
            window.FillPreviewLogs();
            // TextBox scroll metrics are published after a real presentation
            // source/layout pass. Keep this synthetic window offscreen/inactive.
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.WindowStyle = WindowStyle.None;
            window.MinHeight = window.MinWidth = 0; window.Left = window.Top = -30000;
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
            Assert(window.HiddenCloseForTest(), "Closing must hide to tray without stopping backend or clearing logs.");
            checks.Add("X hides window, remains outside taskbar, reopen retains backend session logs");
            foreach (var size in new[] { new Size(380, 590), new Size(438, 750), new Size(1280, 760) })
            foreach (string page in new[] { "Home", "Settings", "AppTunnel", "SiteTunnel", "Diagnostics", "VpnConfig", "Profiles" })
            {
                var root = window.LayoutForTest(page, size); var elements = Elements(root).ToArray();
                if (page == "Home")
                {
                    Assert(!elements.OfType<ScrollViewer>().Any(x => x.IsVisible), "Home must not have an outer scrollbar.");
                    foreach (string name in new[] { "ServerCard", "CaptureSelector", "ConnectButton", "TrafficCard" })
                    {
                        var item = elements.Single(x => x.Name == name); var bounds = item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
                        if (name == "ConnectButton") Assert(bounds.Height >= 96, "Connect button must remain readable at minimum height; actual=" + bounds.Height);
                        Assert(bounds.Top >= -.5 && bounds.Bottom <= size.Height + .5 && bounds.Left >= -.5 && bounds.Right <= size.Width + .5 && bounds.Height > 20, "Home widget clipped: " + name);
                    }
                    Assert(elements.OfType<ComboBox>().Single(x => x.Name == "HomeProtocol").SelectedItem as string == settings.Protocol, "Home protocol selector missing.");
                    Assert(window.BusyCaptureForTest(), "Capture selector must be disabled and retain actual mode during connection operations.");
                    var server = elements.Single(x => x.Name == "ServerCard"); Assert(server.ActualWidth <= 460, "Maximized window lost the centered mobile column.");
                }
                if (page == "Settings") Assert(!elements.OfType<Button>().Single(x => x.Name == "AppTunnelSettings").IsEnabled, "App Tunnel must remain visible but disabled in Proxy.");
                if (page == "AppTunnel")
                {
                    Assert(!elements.OfType<Button>().Single(x => x.Content as string == "+ Choose app.exe").IsEnabled, "Disabled App Tunnel has an active app picker.");
                    Assert(!elements.OfType<Button>().Single(x => x.Content as string == "Save draft").IsEnabled, "Disabled App Tunnel has an active save action.");
                }
                if (page == "Diagnostics")
                {
                    Assert(!elements.OfType<TextBlock>().Any(x => x.Text.Contains("1,000") || x.Text.Contains("KiB") || x.Text.Contains("session memory")), "Log descriptions or size limits remain visible.");
                    var text = elements.OfType<TextBox>().Single(x => x.IsReadOnly);
                    Assert(text.Text.Length > 1000 && text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && text.ExtentHeight > text.ViewportHeight, "Long logs viewport: text=" + text.Text.Length + ", extent=" + text.ExtentHeight + ", viewport=" + text.ViewportHeight);
                }
                foreach (var scrollbar in elements.OfType<System.Windows.Controls.Primitives.ScrollBar>().Where(x => x.Orientation == Orientation.Vertical && x.Visibility == Visibility.Visible && x.ActualWidth > 0))
                    Assert(scrollbar.ActualWidth <= 6.5, page + " scrollbar " + scrollbar.Name + " width=" + scrollbar.ActualWidth + " exceeds 6 DIP.");
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, page + "-" + (int)size.Width + ".png")); encoder.Save(file);
            }
            checks.Add("all mobile pages rendered at small, default and wide sizes; Home widgets fit without page scroll; bounded log viewport");
            var confirmation = window.CaptureConfirmationForTest("TUN");
            confirmation.WindowStartupLocation = WindowStartupLocation.Manual; confirmation.ShowActivated = false; confirmation.Left = confirmation.Top = -30000;
            confirmation.Show(); confirmation.UpdateLayout();
            Assert(Elements(confirmation).OfType<Button>().Single(x => x.Name == "ConfirmCaptureMode").IsEnabled == CaptureCapabilities.TunAvailable && settings.CaptureMode == "Proxy", "TUN confirmation capability or non-mutating preview failed.");
            var dialogBitmap = new RenderTargetBitmap((int)confirmation.ActualWidth, (int)confirmation.ActualHeight, 96, 96, PixelFormats.Pbgra32); dialogBitmap.Render(confirmation);
            var dialogEncoder = new PngBitmapEncoder(); dialogEncoder.Frames.Add(BitmapFrame.Create(dialogBitmap));
            using (var dialogFile = File.Create(Path.Combine(output, "CaptureConfirmation.png"))) dialogEncoder.Save(dialogFile);
            confirmation.Close(); checks.Add("capture confirmation visible; verified TUN can be confirmed; preview retains Proxy and profiles");
            File.WriteAllText(Path.Combine(output, "tun-plan.json"), TunPlan.Compile(settings, new[] { "203.0.113.10" }, new[] { @"C:\PersonalVPN\cores\xray.exe", @"C:\PersonalVPN\cores\hysteria.exe" }).ToJsonString(Store.JsonOptions));
            File.WriteAllText(Program.Option(args, "--report")!, JsonSerializer.Serialize(new { result = "PASS", checks }, Store.JsonOptions)); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Program.Option(args, "--report")!, JsonSerializer.Serialize(new { result = "FAIL", checks, error = error.GetType().Name, reason = error.Message }, Store.JsonOptions)); return 1;
        }
        finally { window?.StopPreview(); }
    }
}
