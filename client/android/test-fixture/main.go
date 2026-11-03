// Ephemeral local test gateway. No user credentials or VPS configuration.
package main
import (
 "context"
 "crypto/ecdh"
 "crypto/ecdsa"
 "crypto/elliptic"
 "crypto/rand"
 "crypto/sha256"
 "crypto/tls"
 "crypto/x509"
 "encoding/base64"
 "encoding/hex"
 "encoding/json"
 "encoding/pem"
 "math/big"
 "log"
 "net"
 "net/http"
 "os"
 "os/exec"
 "os/signal"
 "path/filepath"
 "syscall"
 "time"
 box "github.com/sagernet/sing-box"
 "github.com/sagernet/sing-box/include"
 "github.com/sagernet/sing-box/option"
 sj "github.com/sagernet/sing/common/json"
 "golang.org/x/net/dns/dnsmessage"
)
func main() {
 lifetime,cancel:=signal.NotifyContext(context.Background(),os.Interrupt,syscall.SIGTERM);defer cancel()
 key,_:=ecdsa.GenerateKey(elliptic.P256(),rand.Reader)
 template:=&x509.Certificate{SerialNumber:big.NewInt(1),DNSNames:[]string{"fixture.local"},NotBefore:time.Now().Add(-time.Hour),NotAfter:time.Now().Add(time.Hour),KeyUsage:x509.KeyUsageDigitalSignature,ExtKeyUsage:[]x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
 der,_:=x509.CreateCertificate(rand.Reader,template,template,&key.PublicKey,key);digest:=sha256.Sum256(der)
 keyDER,_:=x509.MarshalECPrivateKey(key)
 certPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"CERTIFICATE",Bytes:der}));keyPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"EC PRIVATE KEY",Bytes:keyDER}))
 // REALITY's test target is a real, controlled TLS 1.3 endpoint. Avoid an
 // unrelated public CDN's routing, availability and anti-bot policy in CI.
 targetListener,err:=net.Listen("tcp","0.0.0.0:18445");if err!=nil{panic(err)}
 target:=&http.Server{TLSConfig:&tls.Config{MinVersion:tls.VersionTLS13,Certificates:[]tls.Certificate{{Certificate:[][]byte{der},PrivateKey:key}}},Handler:http.HandlerFunc(func(w http.ResponseWriter,r *http.Request){log.Printf("fixture HTTP %s %s %s",r.Method,r.URL.Path,r.Proto);if r.URL.Path=="/ip" {w.Write([]byte("198.18.0.1"))} else {w.WriteHeader(204)}})}
 go target.ServeTLS(targetListener,"","")
 defer target.Close()
 reality,_:=ecdh.X25519().GenerateKey(rand.Reader)
 uuid:="10000000-0000-4000-8000-000000000001";authBytes:=make([]byte,24);rand.Read(authBytes);auth:=base64.RawURLEncoding.EncodeToString(authBytes)
 config:=map[string]any{"log":map[string]any{"level":"debug","timestamp":true},"inbounds":[]any{
  map[string]any{"type":"hysteria2","listen":"0.0.0.0","listen_port":18443,"users":[]any{map[string]any{"password":auth}},"tls":map[string]any{"enabled":true,"certificate":[]string{certPEM},"key":[]string{keyPEM}}},
 },"outbounds":[]any{map[string]any{"type":"direct","tag":"direct"}},"dns":map[string]any{"servers":[]any{map[string]any{"type":"local","tag":"system"}}},"route":map[string]any{"final":"direct","default_domain_resolver":"system"}}
 ctx:=box.Context(context.Background(),include.InboundRegistry(),include.OutboundRegistry(),include.EndpointRegistry(),include.DNSTransportRegistry(),include.ServiceRegistry(),include.CertificateProviderRegistry())
 raw,_:=json.Marshal(config);options,err:=sj.UnmarshalExtendedContext[option.Options](ctx,raw);if err!=nil{panic(err)}
 instance,err:=box.New(box.Options{Context:ctx,Options:options});if err!=nil{panic(err)};if err=instance.Start();err!=nil{panic(err)};defer instance.Close()
 // REALITY is served by official Xray, not by the client library itself.
 xrayConfig:=map[string]any{"log":map[string]any{"loglevel":"warning"},"inbounds":[]any{map[string]any{"listen":"0.0.0.0","port":18444,"protocol":"vless","settings":map[string]any{"decryption":"none","clients":[]any{map[string]any{"id":uuid,"flow":"xtls-rprx-vision"}}},"streamSettings":map[string]any{"network":"raw","security":"reality","realitySettings":map[string]any{"target":"127.0.0.1:18445","serverNames":[]string{"fixture.local"},"privateKey":base64.RawURLEncoding.EncodeToString(reality.Bytes()),"shortIds":[]string{"0123456789abcdef"}}}}},"outbounds":[]any{map[string]any{"protocol":"freedom"}}}
 // Allow only the controlled HTTPS/UDP fixtures. Xray's production private
 // address restrictions must not silently blackhole these CI-only targets.
 xrayConfig["outbounds"]=[]any{map[string]any{"protocol":"freedom","settings":map[string]any{"finalRules":[]any{
  map[string]any{"action":"allow","network":"tcp","ip":[]string{"198.18.0.1/32"},"port":18445},
  map[string]any{"action":"allow","network":"udp","ip":[]string{"198.18.0.1/32"},"port":19000},
 }}}}
 xrayBytes,_:=json.Marshal(xrayConfig);xrayPath:=filepath.Join(filepath.Dir(os.Args[1]),"fixture-xray.json");if err=os.WriteFile(xrayPath,xrayBytes,0600);err!=nil{panic(err)};defer os.Remove(xrayPath)
 xray:=exec.CommandContext(lifetime,os.Args[2],"run","-c",xrayPath);xray.Stdout=os.Stdout;xray.Stderr=os.Stderr;if err=xray.Start();err!=nil{panic(err)};defer func(){xray.Process.Kill();xray.Wait()}()
 fixture:=map[string]any{"reality":map[string]any{"server":"10.0.2.2","port":18444,"uuid":uuid,"sni":"fixture.local","publicKey":base64.RawURLEncoding.EncodeToString(reality.PublicKey().Bytes()),"shortId":"0123456789abcdef"},"hysteria":map[string]any{"server":"10.0.2.2","port":18443,"auth":auth,"sni":"fixture.local","pin":hex.EncodeToString(digest[:]),"insecure":true,"obfs":""}}
 fixture["ca"]=certPEM
 encoded,_:=json.Marshal(fixture);if err=os.WriteFile(os.Args[1],encoded,0600);err!=nil{panic(err)}
 dnsSocket,err:=net.ListenPacket("udp","0.0.0.0:15353");if err!=nil{panic(err)};defer dnsSocket.Close()
 go func(){buffer:=make([]byte,4096);for{n,peer,err:=dnsSocket.ReadFrom(buffer);if err!=nil{return};var query dnsmessage.Message;if query.Unpack(buffer[:n])!=nil{continue};answer:=dnsmessage.Message{Header:dnsmessage.Header{ID:query.ID,Response:true,RecursionDesired:query.RecursionDesired,RecursionAvailable:true},Questions:query.Questions};for _,q:=range query.Questions{if q.Type==dnsmessage.TypeA{answer.Answers=append(answer.Answers,dnsmessage.Resource{Header:dnsmessage.ResourceHeader{Name:q.Name,Type:dnsmessage.TypeA,Class:dnsmessage.ClassINET,TTL:60},Body:&dnsmessage.AResource{A:[4]byte{198,18,0,1}}})}};data,err:=answer.Pack();if err==nil{dnsSocket.WriteTo(data,peer)}}}()
 socket,err:=net.ListenPacket("udp","0.0.0.0:19000");if err!=nil{panic(err)};defer socket.Close()
 buffer:=make([]byte,2048);for {n,peer,err:=socket.ReadFrom(buffer);if err==nil{socket.WriteTo(buffer[:n],peer)}}
}
