package libbox

import (
 "crypto/ecdh"
 "crypto/ecdsa"
 "crypto/elliptic"
 "crypto/rand"
 "crypto/tls"
 "crypto/x509"
 "encoding/base64"
 "encoding/json"
 "encoding/pem"
 "math/big"
 "net"
 "net/http"
 "os"
 "os/exec"
 "path/filepath"
 "strconv"
 "testing"
 "time"
 box "github.com/sagernet/sing-box"
)

// Interoperate with the same modern Xray release as the VPS, rather than a
// second sing-box implementation that shares the client's old handshake.
func TestPersonalRealityXray(t *testing.T) {
 executable := os.Getenv("PERSONAL_XRAY")
 if executable == "" { t.Fatal("PERSONAL_XRAY must name the verified Xray fixture") }
 key,err:=ecdsa.GenerateKey(elliptic.P256(),rand.Reader);if err!=nil{t.Fatal(err)}
 cert:=&x509.Certificate{SerialNumber:big.NewInt(2),DNSNames:[]string{"fixture.local"},IPAddresses:[]net.IP{net.ParseIP("127.0.0.1")},NotBefore:time.Now().Add(-time.Hour),NotAfter:time.Now().Add(time.Hour),KeyUsage:x509.KeyUsageDigitalSignature,ExtKeyUsage:[]x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
 der,err:=x509.CreateCertificate(rand.Reader,cert,cert,&key.PublicKey,key);if err!=nil{t.Fatal(err)}
 certPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"CERTIFICATE",Bytes:der}))
 listener,err:=net.Listen("tcp","127.0.0.1:0");if err!=nil{t.Fatal(err)}
 target:=&http.Server{TLSConfig:&tls.Config{MinVersion:tls.VersionTLS13,Certificates:[]tls.Certificate{{Certificate:[][]byte{der},PrivateKey:key}}},Handler:http.HandlerFunc(func(w http.ResponseWriter,r *http.Request){w.Write([]byte("198.18.0.1"))})}
 go target.ServeTLS(listener,"","");t.Cleanup(func(){target.Close()})
 socket,err:=net.Listen("tcp","127.0.0.1:0");if err!=nil{t.Fatal(err)};port:=socket.Addr().(*net.TCPAddr).Port;socket.Close()
 reality,err:=ecdh.X25519().GenerateKey(rand.Reader);if err!=nil{t.Fatal(err)}
 uuid:="10000000-0000-4000-8000-000000000001"
 server:=map[string]any{"log":map[string]any{"loglevel":"error"},"inbounds":[]any{map[string]any{"listen":"127.0.0.1","port":port,"protocol":"vless","settings":map[string]any{"decryption":"none","clients":[]any{map[string]any{"id":uuid,"flow":"xtls-rprx-vision"}}},"streamSettings":map[string]any{"network":"raw","security":"reality","realitySettings":map[string]any{"target":listener.Addr().String(),"serverNames":[]string{"fixture.local"},"privateKey":base64.RawURLEncoding.EncodeToString(reality.Bytes()),"shortIds":[]string{"0123456789abcdef"}}}}},"outbounds":[]any{map[string]any{"protocol":"freedom"}}}
 // Xray's freedom outbound blocks private targets by default. Permit only
 // this generated loopback HTTPS fixture; retain its defaults for all else.
 server["outbounds"]=[]any{map[string]any{"protocol":"freedom","settings":map[string]any{"finalRules":[]any{map[string]any{"action":"allow","network":"tcp","ip":[]string{"127.0.0.1/32"},"port":listener.Addr().(*net.TCPAddr).Port}}}}}
 raw,_:=json.Marshal(server);path:=filepath.Join(t.TempDir(),"xray.json");if err=os.WriteFile(path,raw,0600);err!=nil{t.Fatal(err)}
 command:=exec.Command(executable,"run","-c",path);command.Stdout=os.Stdout;command.Stderr=os.Stderr;if err=command.Start();err!=nil{t.Fatal(err)};t.Cleanup(func(){command.Process.Kill();command.Wait()})
 deadline:=time.Now().Add(5*time.Second)
 for { connection,e:=net.DialTimeout("tcp",net.JoinHostPort("127.0.0.1",strconv.Itoa(port)),100*time.Millisecond);if e==nil{connection.Close();break};if time.Now().After(deadline){t.Fatal("Xray did not listen")};time.Sleep(50*time.Millisecond) }
 for _,mode:=range []string{"valid","short_id","public_key"} {
  shortID:="0123456789abcdef";publicKey:=reality.PublicKey().Bytes()
  if mode=="short_id" {shortID="ffffffffffffffff"}
  if mode=="public_key" {other,e:=ecdh.X25519().GenerateKey(rand.Reader);if e!=nil{t.Fatal(e)};publicKey=other.PublicKey().Bytes()}
  ctx:=baseContext(nil)
  config:=map[string]any{"log":map[string]any{"disabled":true},"outbounds":[]any{map[string]any{"type":"vless","tag":"tunnel","server":"127.0.0.1","server_port":port,"uuid":uuid,"flow":"xtls-rprx-vision","tls":map[string]any{"enabled":true,"server_name":"fixture.local","utls":map[string]any{"enabled":true,"fingerprint":"chrome"},"reality":map[string]any{"enabled":true,"public_key":base64.RawURLEncoding.EncodeToString(publicKey),"short_id":shortID}}}}}
  raw,_:=json.Marshal(config);options,e:=parseConfig(ctx,string(raw));if e!=nil{t.Fatal(e)};instance,e:=box.New(box.Options{Context:ctx,Options:options});if e!=nil{t.Fatal(e)};if e=instance.Start();e!=nil{t.Fatal(e)}
  core:=&PersonalInstance{box:instance}
  for i:=0;i<3;i++{result,e:=core.VerifyAt("https://"+listener.Addr().String()+"/ip",certPEM)
   rejected:=mode!="valid" || os.Getenv("PERSONAL_EXPECT_REALITY_REJECT")=="true"
   if rejected {if e==nil{instance.Close();t.Fatal("unauthenticated REALITY must not pass health")};break}
   if e!=nil{instance.Close();t.Fatalf("modern Xray health %d: %v",i,e)};if result!="198.18.0.1"{instance.Close();t.Fatal("unexpected authenticated exit")}
  }
  instance.Close()
 }
}
