package personalvpn

import (
 "context"
 "sync"
 "sync/atomic"
 "testing"
 "time"
)

type blockedProbe struct { entered chan struct{}; active atomic.Int32; closes atomic.Int32; earlyClose atomic.Bool }
func (b *blockedProbe) Close(){if b.active.Load()!=0{b.earlyClose.Store(true)};b.closes.Add(1)}
func (b *blockedProbe) CancelPending(){}
func (b *blockedProbe) Upload()int64{return 17}
func (b *blockedProbe) Download()int64{return 23}
func (b *blockedProbe) VerifyAtContext(ctx context.Context,_,_ string)(string,error){b.active.Add(1);defer b.active.Add(-1);b.entered<-struct{}{};<-ctx.Done();return "",ctx.Err()}
func (b *blockedProbe) VerifyCapturedAtContext(ctx context.Context,a,c string)(string,error){return b.VerifyAtContext(ctx,a,c)}

func TestProbeCancellationKeepsCountersResponsiveAndCoreReusable(t *testing.T){
 b:=&blockedProbe{entered:make(chan struct{},2)};c:=&Core{instance:b,closeDone:make(chan struct{})}
 defer c.Close()
 for n:=0;n<2;n++{
  result:=make(chan error,1);go func(){_,err:=c.VerifyAt("https://unused.invalid/","");result<-err}()
  select{case <-b.entered:case <-time.After(time.Second):t.Fatal("probe did not enter")}
  counters:=make(chan bool,1);go func(){counters<-c.Upload()==17&&c.Download()==23}()
  select{case ok:=<-counters:if !ok{t.Fatal("counters changed")};case <-time.After(time.Second):t.Fatal("probe blocked counters")}
  c.CancelPending()
  select{case err:=<-result:if err!=context.Canceled{t.Fatalf("unexpected result %v",err)};case <-time.After(time.Second):t.Fatal("cancellation did not settle probe")}
  if b.closes.Load()!=0{t.Fatal("probe cancellation closed application transports")}
 }
}

func TestConcurrentCloseCancelsAndJoinsProbeBeforeClosingInstance(t *testing.T){
 b:=&blockedProbe{entered:make(chan struct{},1)};c:=&Core{instance:b,closeDone:make(chan struct{})}
 go c.VerifyCapturedAt("https://unused.invalid/","");<-b.entered
 done:=make(chan struct{});go func(){var wg sync.WaitGroup;wg.Add(2);for i:=0;i<2;i++{go func(){defer wg.Done();c.Close()}()};wg.Wait();close(done)}()
 select{case <-done:case <-time.After(time.Second):t.Fatal("Close waited for probe deadline instead of cancellation")}
 if b.earlyClose.Load()||b.closes.Load()!=1{t.Fatal("instance lifetime overlapped a probe or closed twice")}
 if _,err:=c.Verify();err==nil{t.Fatal("closed core accepted a new probe")}
}
