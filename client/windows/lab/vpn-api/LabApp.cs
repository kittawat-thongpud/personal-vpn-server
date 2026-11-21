using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Networking.Vpn;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TestVpnPluginApp
{
    sealed partial class App : Application
    {
        private const string ProfileName = "Personal VPN API Lab";
        private readonly VpnManagementAgent agent = new VpnManagementAgent();

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (Window.Current.Content == null)
                Window.Current.Content = new TextBlock { Text = ProfileName };
            Window.Current.Activate();
            string command = args.Arguments;
            string log = Path.Combine(ApplicationData.Current.LocalFolder.Path, "profile.log");
            try
            {
                if (command == "init")
                {
                    File.AppendAllText(log, "initialized\n");
                    return;
                }
                var current = (await agent.GetProfilesAsync()).OfType<VpnPlugInProfile>()
                    .SingleOrDefault(item => item.ProfileName == ProfileName &&
                        item.VpnPluginPackageFamilyName == Package.Current.Id.FamilyName);
                if (current != null)
                {
                    if (current.ConnectionStatus != VpnManagementConnectionStatus.Disconnected)
                        RequireOk(await agent.DisconnectProfileAsync(current), "DisconnectProfile");
                    RequireOk(await agent.DeleteProfileAsync(current), "DeleteProfile");
                }
                if (command == "cleanup")
                {
                    File.AppendAllText(log, "cleaned\n");
                    return;
                }
                if (command != "probe-a" && command != "probe-b")
                    throw new InvalidOperationException("Unknown lab command");

                string path = File.ReadAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, command + ".txt")).Trim();
                string route = File.ReadAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "route.txt")).Trim();
                if (!Path.IsPathRooted(path) || Path.GetFileName(path) != command + ".exe")
                    throw new InvalidOperationException("Invalid probe executable");
                var profile = new VpnPlugInProfile
                {
                    ProfileName = ProfileName,
                    VpnPluginPackageFamilyName = Package.Current.Id.FamilyName,
                    RequireVpnClientAppUI = false,
                    CustomConfiguration = new XElement("PersonalVpnApiLab",
                        new XAttribute("route", route), new XAttribute("probe", command)).ToString()
                };
                profile.ServerUris.Add(new Uri("https://198.51.100.20/"));
                profile.AppTriggers.Add(new VpnAppId(VpnAppIdType.FilePath, path));
                profile.TrafficFilters.Add(new VpnTrafficFilter(new VpnAppId(VpnAppIdType.FilePath, path))
                {
                    RoutingPolicyType = VpnRoutingPolicyType.ForceAllTrafficOverVpn
                });
                RequireOk(await agent.AddProfileFromObjectAsync(profile), "AddProfile");
                File.AppendAllText(log, "profile-added " + command + "\n");
                var stored = (await agent.GetProfilesAsync()).OfType<VpnPlugInProfile>()
                    .Single(item => item.ProfileName == ProfileName);
                RequireOk(await agent.ConnectProfileAsync(stored), "ConnectProfile");
                File.AppendAllText(log, "connected " + command + "\n");
            }
            catch (Exception error)
            {
                File.AppendAllText(log, "error " + command + " " + error + "\n");
            }
        }

        private static void RequireOk(VpnManagementErrorStatus result, string operation)
        {
            if (result != VpnManagementErrorStatus.Ok)
                throw new InvalidOperationException(operation + ": " + result);
        }
    }
}
