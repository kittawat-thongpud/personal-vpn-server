// Copied into the pinned libbox package by build-native.sh. No upstream API is
// exposed to Kotlin: the public JNI surface lives in experimental/personalvpn.
package libbox

import (
 "context"
 "io"
 "net"
 "net/http"
 "os"
 "strings"
 "sync/atomic"
 "time"
 box "github.com/sagernet/sing-box"
 "github.com/sagernet/sing-box/adapter"
 "github.com/sagernet/sing-box/service/powerreport"
 tun "github.com/sagernet/sing-tun"
 "github.com/sagernet/sing/common/bufio"
 M "github.com/sagernet/sing/common/metadata"
 N "github.com/sagernet/sing/common/network"
 "github.com/sagernet/sing/service"
)

type PersonalInstance struct { box *box.Box; cancel context.CancelFunc; up, down atomic.Int64 }

func PersonalStart(root, config string, platform PlatformInterface) (*PersonalInstance, error) {
 // Deliberately do not call Setup(): it redirects stderr to an unbounded crash
 // file. Configs remain in memory and backend events are bounded by the app.
 sBasePath = root; sWorkingPath = root; sTempPath = root; sUserID = os.Getuid(); sGroupID = os.Getgid()
 ctx, cancel := context.WithCancel(baseContext(platform))
 power := powerreport.NewManager()
 service.MustRegister[*powerreport.Manager](ctx, power)
 service.MustRegister[adapter.PlatformInterface](ctx, &platformInterfaceWrapper{iif: platform, powerManager: power})
 options, err := parseConfig(ctx, config)
 if err != nil { cancel(); return nil, err }
 options.Log.Disabled = true
 instance, err := box.New(box.Options{Options: options, Context: ctx})
 if err != nil { cancel(); return nil, err }
 result := &PersonalInstance{box: instance, cancel: cancel}
 instance.Router().AppendTracker(result)
 if err = instance.Start(); err != nil { instance.Close(); cancel(); return nil, err }
 return result, nil
}
func (c *PersonalInstance) Close() { c.cancel(); c.box.Close() }
func (c *PersonalInstance) ResetNetwork() { c.box.Network().ResetNetwork(context.Background()) }
func (c *PersonalInstance) Upload() int64 { return c.up.Load() }
func (c *PersonalInstance) Download() int64 { return c.down.Load() }
func tunnel(outbound adapter.Outbound) bool { return outbound != nil && outbound.Tag() == "tunnel" }
func (c *PersonalInstance) RoutedConnection(ctx context.Context, conn net.Conn, metadata adapter.InboundContext, rule adapter.Rule, outbound adapter.Outbound) net.Conn {
 if !tunnel(outbound) { return conn }
 return bufio.NewInt64CounterConn(conn, []*atomic.Int64{&c.up}, []*atomic.Int64{&c.down})
}
func (c *PersonalInstance) RoutedPacketConnection(ctx context.Context, conn N.PacketConn, metadata adapter.InboundContext, rule adapter.Rule, outbound adapter.Outbound) N.PacketConn {
 if !tunnel(outbound) { return conn }
 return bufio.NewInt64CounterPacketConn(conn, []*atomic.Int64{&c.up}, nil, []*atomic.Int64{&c.down}, nil)
}
type personalFlow struct { core *PersonalInstance; count bool }
func (f *personalFlow) AttachFlow(tun.FlowHandle) {}
func (f *personalFlow) CountForward(n int) { if f.count { f.core.up.Add(int64(n)) } }
func (f *personalFlow) CountReverse(n int) { if f.count { f.core.down.Add(int64(n)) } }
func (f *personalFlow) FlowEstablished() {}
func (f *personalFlow) CloseFlow(tun.FlowCloseReason) {}
func (c *PersonalInstance) RoutedFlow(ctx context.Context, metadata adapter.InboundContext, rule adapter.Rule, outbound adapter.Outbound) tun.FlowTracker { return &personalFlow{c, tunnel(outbound)} }
func (c *PersonalInstance) Verify() (string, error) {
 outbound, _ := c.box.Outbound().Outbound("tunnel")
 transport := &http.Transport{DisableKeepAlives: true, DialContext: func(ctx context.Context, network, address string) (net.Conn,error) { return outbound.DialContext(ctx, network, M.ParseSocksaddr(address)) }}
 defer transport.CloseIdleConnections()
 client := &http.Client{Transport: transport, Timeout: 12*time.Second}
 response, err := client.Get("https://api.ipify.org/")
 if err != nil { return "", err }
 defer response.Body.Close()
 body, err := io.ReadAll(io.LimitReader(response.Body, 128))
 if err != nil { return "", err }
 value := strings.TrimSpace(string(body))
 if response.StatusCode != 200 || net.ParseIP(value) == nil { return "", os.ErrInvalid }
 return value, nil
}
