// Minimal gomobile surface; this is built only in GitHub Actions.
package personalvpn

import (
 "context"
 "encoding/json"
 "errors"
 "sync"
 "github.com/sagernet/sing-box/experimental/libbox"
)

type Platform interface {
 OpenTun(mtu int32) (int32,error)
 Protect(fd int32) error
 Owner(protocol int32, source string, sourcePort int32, destination string, destinationPort int32) string
 Interfaces() string
 DefaultInterface() string
}
type coreInstance interface {
 Close();CancelPending();VerifyAtContext(context.Context,string,string)(string,error);VerifyCapturedAtContext(context.Context,string,string)(string,error)
 Upload()int64;Download()int64
}
type Core struct { instance coreInstance; platform *mobilePlatform; mu sync.Mutex; probes sync.WaitGroup; pending map[uint64]context.CancelFunc; nextProbe uint64; closeDone chan struct{} }
func Start(root, config string, platform Platform) (*Core,error) {
 p := &mobilePlatform{callback:platform}
 instance, err := libbox.PersonalStart(root, config, p)
 if err != nil { return nil,err }
 return &Core{instance:instance,platform:p,closeDone:make(chan struct{})},nil
}
func (c *Core) Close() {
 c.mu.Lock();instance:=c.instance
 if instance==nil { done:=c.closeDone;c.mu.Unlock();if done!=nil{<-done};return }
 c.instance=nil;if c.closeDone==nil{c.closeDone=make(chan struct{})};done:=c.closeDone
 pending:=c.cancellationsLocked();c.mu.Unlock();for _,cancel:=range pending{cancel()}
 instance.CancelPending();c.probes.Wait();instance.Close();close(done)
}
func (c *Core) cancellationsLocked() []context.CancelFunc { pending:=make([]context.CancelFunc,0,len(c.pending));for _,cancel:=range c.pending{pending=append(pending,cancel)};return pending }
func (c *Core) CancelPending() { c.mu.Lock();pending:=c.cancellationsLocked();c.mu.Unlock();for _,cancel:=range pending{cancel()} }
func (c *Core) Verify() (string,error) { return c.VerifyAt("https://api.ipify.org/", "") }
func (c *Core) beginProbe() (coreInstance,context.Context,func(),error) {
 c.mu.Lock();defer c.mu.Unlock();if c.instance==nil{return nil,nil,nil,errors.New("closed")}
 ctx,cancel:=context.WithCancel(context.Background());if c.pending==nil{c.pending=make(map[uint64]context.CancelFunc)};c.nextProbe++;id:=c.nextProbe;c.pending[id]=cancel;c.probes.Add(1)
 done:=func(){cancel();c.mu.Lock();delete(c.pending,id);c.mu.Unlock();c.probes.Done()};return c.instance,ctx,done,nil
}
func (c *Core) VerifyAt(address, certificate string) (string,error) { instance,ctx,done,err:=c.beginProbe();if err!=nil{return "",err};defer done();return instance.VerifyAtContext(ctx,address,certificate) }
func (c *Core) VerifyCapturedAt(address, certificate string) (string,error) { instance,ctx,done,err:=c.beginProbe();if err!=nil{return "",err};defer done();return instance.VerifyCapturedAtContext(ctx,address,certificate) }
func (c *Core) Upload() int64 { c.mu.Lock(); defer c.mu.Unlock(); if c.instance==nil { return 0 }; return c.instance.Upload() }
func (c *Core) Download() int64 { c.mu.Lock(); defer c.mu.Unlock(); if c.instance==nil { return 0 }; return c.instance.Download() }
func (c *Core) NetworkChanged() { c.mu.Lock(); defer c.mu.Unlock(); if c.instance!=nil { c.platform.update() } }
func Check(config string) error { return libbox.CheckConfig(config) }
type stringsIterator struct { values []string; index int }
func (i *stringsIterator) Len() int32 { return int32(len(i.values)-i.index) }
func (i *stringsIterator) HasNext() bool { return i.index<len(i.values) }
func (i *stringsIterator) Next() string { s:=i.values[i.index];i.index++;return s }
type interfacesIterator struct { values []*libbox.NetworkInterface; index int }
func (i *interfacesIterator) HasNext() bool { return i.index<len(i.values) }
func (i *interfacesIterator) Next() *libbox.NetworkInterface { s:=i.values[i.index];i.index++;return s }
type mobilePlatform struct { callback Platform; mu sync.Mutex; listener libbox.InterfaceUpdateListener }
func (p *mobilePlatform) LocalDNSTransport() libbox.LocalDNSTransport { return nil }
func (p *mobilePlatform) UsePlatformAutoDetectInterfaceControl() bool { return true }
func (p *mobilePlatform) AutoDetectInterfaceControl(fd int32) error { return p.callback.Protect(fd) }
func (p *mobilePlatform) OpenTun(options libbox.TunOptions) (int32,error) { return p.callback.OpenTun(options.GetMTU()) }
func (p *mobilePlatform) FindConnectionOwner(protocol int32,source string,sp int32,destination string,dp int32) (*libbox.ConnectionOwner,error) {
 var owner struct { UID int32 `json:"uid"`; Packages []string `json:"packages"` }
 err:=json.Unmarshal([]byte(p.callback.Owner(protocol,source,sp,destination,dp)),&owner)
 if err!=nil || len(owner.Packages)==0 { owner.UID=-1;owner.Packages=[]string{"__unresolved__"} }
 result:=&libbox.ConnectionOwner{UserId:owner.UID};result.SetAndroidPackageNames(&stringsIterator{values:owner.Packages});return result,nil
}
func (p *mobilePlatform) update() {
 var value struct { Name string `json:"name"`; Index int32 `json:"index"`; Expensive bool `json:"expensive"` }
 if json.Unmarshal([]byte(p.callback.DefaultInterface()),&value)!=nil { return }
 p.mu.Lock(); listener:=p.listener;p.mu.Unlock()
 if listener!=nil { listener.UpdateDefaultInterface(value.Name,value.Index,value.Expensive,false) }
}
func (p *mobilePlatform) StartDefaultInterfaceMonitor(listener libbox.InterfaceUpdateListener) error { p.mu.Lock();p.listener=listener;p.mu.Unlock();p.update();return nil }
func (p *mobilePlatform) CloseDefaultInterfaceMonitor(listener libbox.InterfaceUpdateListener) error { p.mu.Lock();p.listener=nil;p.mu.Unlock();return nil }
func (p *mobilePlatform) GetInterfaces() (libbox.NetworkInterfaceIterator,error) {
 var entries []struct { Index int32 `json:"index"`; Name string `json:"name"`; MTU int32 `json:"mtu"`; Addresses []string `json:"addresses"`; DNS []string `json:"dns"`; Type int32 `json:"type"`; Metered bool `json:"metered"` }
 if err:=json.Unmarshal([]byte(p.callback.Interfaces()),&entries);err!=nil{return nil,err}
 list:=make([]*libbox.NetworkInterface,0,len(entries))
 for _,e:=range entries { list=append(list,&libbox.NetworkInterface{Index:e.Index,Name:e.Name,MTU:e.MTU,Flags:3,Addresses:&stringsIterator{values:e.Addresses},DNSServer:&stringsIterator{values:e.DNS},Type:e.Type,Metered:e.Metered}) }
 return &interfacesIterator{values:list},nil
}
func (p *mobilePlatform) UnderNetworkExtension() bool { return false }
func (p *mobilePlatform) IncludeAllNetworks() bool { return false }
func (p *mobilePlatform) ReadWIFIState() *libbox.WIFIState { return nil }
func (p *mobilePlatform) ClearDNSCache() {}
func (p *mobilePlatform) SendNotification(*libbox.Notification) error { return nil }
func (p *mobilePlatform) CancelNotification(string,int32) error { return nil }
func (p *mobilePlatform) StartNeighborMonitor(libbox.NeighborUpdateListener) error { return nil }
func (p *mobilePlatform) CloseNeighborMonitor(libbox.NeighborUpdateListener) error { return nil }
func (p *mobilePlatform) RegisterMyInterface(string) {}
func (p *mobilePlatform) UsePlatformShell() bool { return false }
func (p *mobilePlatform) CheckPlatformShell() error { return errors.New("shell disabled") }
func (p *mobilePlatform) OpenShellSession(*libbox.PlatformUser,string,libbox.StringIterator,string,int32,int32) (libbox.ShellSession,error) { return nil,errors.New("shell disabled") }
func (p *mobilePlatform) LookupUser(string) (*libbox.PlatformUser,error) { return nil,errors.New("users unavailable") }
func (p *mobilePlatform) LookupSFTPServer() (string,error) { return "",errors.New("SFTP disabled") }
func (p *mobilePlatform) ReadSystemSSHHostKey() (string,error) { return "",errors.New("SSH disabled") }
func (p *mobilePlatform) TailscaleHostname() string { return "" }
func (p *mobilePlatform) UsePlatformBridge() bool { return false }
func (p *mobilePlatform) CreateBridge(*libbox.BridgeOptions) (libbox.BridgeSession,error) { return nil,errors.New("bridge disabled") }
