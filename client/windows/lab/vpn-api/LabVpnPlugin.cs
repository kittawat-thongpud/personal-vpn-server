using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Networking.Vpn;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TestVpnPluginAppBg
{
    internal sealed class TestVpnPlugin : IVpnPlugIn
    {
        private DatagramSocket transport;
        private string probeName;
        private int captured;

        public void Connect(VpnChannel channel)
        {
            try
            {
                // Windows 11 can add connection metadata as XML nodes.
                // Read our own element and attributes, not the document text.
                var configuration = XElement.Parse(channel.Configuration.CustomField)
                    .DescendantsAndSelf().Single(element => element.Name.LocalName == "PersonalVpnApiLab");
                probeName = (string)configuration.Attribute("probe");
                string route = (string)configuration.Attribute("route");
                Log("plugin-start " + probeName);
                if (probeName != "probe-a" && probeName != "probe-b")
                    throw new InvalidOperationException("Expected a lab probe");
                string exe = File.ReadAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, probeName + ".txt")).Trim();
                if (!Path.IsPathRooted(exe) || Path.GetFileName(exe) != probeName + ".exe")
                    throw new InvalidOperationException("Expected a lab executable");
                if (route != "1.1.1.1" && route != "192.0.2.42")
                    throw new InvalidOperationException("Expected a lab route");
                captured = 0;
                transport = new DatagramSocket();
                channel.AssociateTransport(transport, null);
                // UDP connect establishes the socket locally. The plug-in emits
                // no outer packets and does not require a VPN or relay server.
                transport.ConnectAsync(new HostName("198.51.100.20"), "51821").AsTask().GetAwaiter().GetResult();
                var filters = new VpnTrafficFilterAssignment();
                filters.TrafficFilterList.Add(new VpnTrafficFilter(new VpnAppId(VpnAppIdType.FilePath, exe))
                {
                    RoutingPolicyType = VpnRoutingPolicyType.ForceAllTrafficOverVpn
                });
                var routes = new VpnRouteAssignment
                {
                    Ipv4InclusionRoutes = new List<VpnRoute> { new VpnRoute(new HostName(route), 32) }
                };
                channel.StartWithTrafficFilter(new List<HostName> { new HostName("172.31.254.2") },
                    null, null, routes, null, 1400, 1500, false, transport, null, filters);
                Log("channel-started " + probeName);
            }
            catch (Exception error)
            {
                transport?.Dispose();
                transport = null;
                Log("channel-error " + error);
                throw;
            }
        }

        public void Encapsulate(VpnChannel channel, VpnPacketBufferList packets, VpnPacketBufferList outbound)
        {
            while (packets.Size > 0)
            {
                var packet = packets.RemoveAtBegin();
                var buffer = packet.Buffer;
                if (buffer.Length < 24 || captured == 32) continue;
                // Inspect only the IPv4 header and the two TCP ports. Drop
                // incoming probe packets deliberately so the two paths differ.
                using (var reader = DataReader.FromBuffer(buffer))
                {
                    reader.ByteOrder = ByteOrder.BigEndian;
                    byte first = reader.ReadByte();
                    uint headerLength = (uint)((first & 15) * 4);
                    if ((first >> 4) != 4 || headerLength < 20 || buffer.Length < headerLength + 4) continue;
                    var fields = new byte[8];
                    reader.ReadBytes(fields);
                    if (reader.ReadByte() != 6) continue;
                    reader.ReadUInt16();
                    var source = new byte[4];
                    var destination = new byte[4];
                    reader.ReadBytes(source);
                    reader.ReadBytes(destination);
                    if (destination[0] != 1 || destination[1] != 1 || destination[2] != 1 || destination[3] != 1) continue;
                    if (headerLength > 20) reader.ReadBytes(new byte[headerLength - 20]);
                    reader.ReadUInt16();
                    if (reader.ReadUInt16() != 443) continue;
                    captured++;
                    Log("captured " + probeName + " " + string.Join(".", source) + " tcp/443");
                }
            }
        }

        public void Decapsulate(VpnChannel channel, VpnPacketBuffer packet, VpnPacketBufferList inbound, VpnPacketBufferList control)
        {
        }

        public void GetKeepAlivePayload(VpnChannel channel, out VpnPacketBuffer packet)
        {
            packet = null;
        }

        public void Disconnect(VpnChannel channel)
        {
            channel.Stop();
            transport?.Dispose();
            transport = null;
            Log("channel-stopped " + probeName);
        }

        private static void Log(string message)
        {
            File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "channel.log"), message + "\n");
        }
    }
}
