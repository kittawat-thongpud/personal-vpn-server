// Copied into the pinned libbox package by build-native.sh. No upstream API is
// exposed to Kotlin: the public JNI surface lives in experimental/personalvpn.
package libbox

import (
 "context"
 "crypto/tls"
 "crypto/x509"
 "fmt"
 "io"
 "net"
 "net/http"
 "net/url"
 "os"
 "strings"
 "sync"
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

type PersonalInstance struct {
 box *box.Box; ctx context.Context; cancel context.CancelFunc; up, down atomic.Int64
 probeMu sync.Mutex; probes map[uint64]context.CancelFunc; nextProbe uint64
}

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
 instance, err := box.New(box.Options{Options: options, Context: ctx})
 if err != nil { cancel(); return nil, err }
 result := &PersonalInstance{box: instance, ctx: ctx, cancel: cancel}
 instance.Router().AppendTracker(result)
 if err = instance.Start(); err != nil { instance.Close(); cancel(); return nil, err }
 return result, nil
}
func (c *PersonalInstance) Close() { if c.cancel != nil { c.cancel() }; c.CancelPending(); c.box.Close() }
// Cancel only probes; capture and application transports stay owned by the core.
func (c *PersonalInstance) CancelPending() {
 c.probeMu.Lock(); pending:=make([]context.CancelFunc,0,len(c.probes));for _,cancel:=range c.probes { pending=append(pending,cancel) };c.probeMu.Unlock()
 for _,cancel:=range pending { cancel() }
}
func (c *PersonalInstance) probeContext(parent context.Context) (context.Context,func()) {
 base:=c.ctx;if base==nil { base=context.Background() }
 ctx,cancel:=context.WithTimeout(base,8*time.Second)
 stop:=context.AfterFunc(parent,cancel)
 if parent.Err()!=nil { cancel() }
 c.probeMu.Lock();if c.probes==nil { c.probes=make(map[uint64]context.CancelFunc) };c.nextProbe++;id:=c.nextProbe;c.probes[id]=cancel;c.probeMu.Unlock()
 return ctx,func(){stop();cancel();c.probeMu.Lock();delete(c.probes,id);c.probeMu.Unlock()}
}
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
 return c.VerifyAt("https://api.ipify.org/", "")
}
func (c *PersonalInstance) VerifyAt(address, certificate string) (string, error) {
 return c.VerifyAtContext(context.Background(),address,certificate)
}
func (c *PersonalInstance) VerifyAtContext(parent context.Context,address, certificate string) (string, error) {
 outbound, ok := c.box.Outbound().Outbound("tunnel")
 if !ok { return "",os.ErrInvalid }
 return c.verifyHTTP(parent,address,certificate,func(ctx context.Context, network, address string) (net.Conn,error) { return outbound.DialContext(ctx, network, M.ParseSocksaddr(address)) })
}
// Plain OS sockets deliberately have no platform Protect()/outbound detour.
// Both DNS and HTTPS must cross Android's retained TUN, independently of VerifyAt.
func (c *PersonalInstance) VerifyCapturedAt(address, certificate string) (string,error) {
 return c.VerifyCapturedAtContext(context.Background(),address,certificate)
}
func (c *PersonalInstance) VerifyCapturedAtContext(parent context.Context,address, certificate string) (string,error) {
 resolver:=&net.Resolver{PreferGo:true,Dial:func(ctx context.Context, network, _ string)(net.Conn,error){return (&net.Dialer{}).DialContext(ctx,network,"172.29.255.2:53")}}
 dialer:=&net.Dialer{Resolver:resolver,Timeout:5*time.Second}
 return c.verifyHTTP(parent,address,certificate,dialer.DialContext)
}
func (c *PersonalInstance) verifyHTTP(parent context.Context,address, certificate string, dial func(context.Context,string,string)(net.Conn,error)) (string, error) {
 parsed, err := url.Parse(address)
 if err != nil || parsed.Scheme != "https" || parsed.Hostname()=="" || parsed.User!=nil { return "", os.ErrInvalid }
 ctx,done:=c.probeContext(parent);defer done()
 // Read the authenticated response before closing its transport. Requesting
 // Connection: close can race the upstream QUIC stream's buffered final data.
 transport := &http.Transport{DialContext:dial,TLSHandshakeTimeout:5*time.Second,ResponseHeaderTimeout:5*time.Second}
 if certificate != "" {
  roots := x509.NewCertPool()
  if !roots.AppendCertsFromPEM([]byte(certificate)) { return "", os.ErrInvalid }
  transport.TLSClientConfig = &tls.Config{RootCAs: roots, MinVersion: tls.VersionTLS13}
 }
 defer transport.CloseIdleConnections()
 client := &http.Client{Transport: transport,CheckRedirect:func(*http.Request,[]*http.Request)error{return http.ErrUseLastResponse}}
 request,err:=http.NewRequestWithContext(ctx,http.MethodGet,address,nil);if err!=nil{return "",err}
 response, err := client.Do(request)
 if err != nil { return "", err }
 defer response.Body.Close()
 body, err := io.ReadAll(io.LimitReader(response.Body, 129))
 if err != nil { return "", err }
 value := strings.TrimSpace(string(body))
 if response.StatusCode != 200 { return "",fmt.Errorf("health HTTP status %d",response.StatusCode) }
 if len(body)>128 || net.ParseIP(value) == nil { return "",errorsHealthResponse }
 return value, nil
}
var errorsHealthResponse = fmt.Errorf("health response invalid")
