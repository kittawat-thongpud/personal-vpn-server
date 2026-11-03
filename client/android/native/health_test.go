package libbox

import (
 "crypto/ecdsa"
 "crypto/elliptic"
 "crypto/rand"
 "crypto/sha256"
 "crypto/tls"
 "crypto/x509"
 "encoding/base64"
 "encoding/json"
 "encoding/pem"
 "math/big"
 "net"
 "net/http"
 "testing"
 "time"
 box "github.com/sagernet/sing-box"
)

// Real QUIC + TLS regression, before the slower Android build. No public
// network, test bypass, user configuration or mock transport is involved.
func TestPersonalHealthHysteriaRepeated(t *testing.T) {
 key,err:=ecdsa.GenerateKey(elliptic.P256(),rand.Reader);if err!=nil{t.Fatal(err)}
 template:=&x509.Certificate{SerialNumber:big.NewInt(1),IPAddresses:[]net.IP{net.ParseIP("127.0.0.1")},NotBefore:time.Now().Add(-time.Hour),NotAfter:time.Now().Add(time.Hour),KeyUsage:x509.KeyUsageDigitalSignature,ExtKeyUsage:[]x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
 der,err:=x509.CreateCertificate(rand.Reader,template,template,&key.PublicKey,key);if err!=nil{t.Fatal(err)}
 keyDER,_:=x509.MarshalECPrivateKey(key)
 certPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"CERTIFICATE",Bytes:der}));keyPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"EC PRIVATE KEY",Bytes:keyDER}))
 listener,err:=net.Listen("tcp","127.0.0.1:0");if err!=nil{t.Fatal(err)}
 target:=&http.Server{TLSConfig:&tls.Config{MinVersion:tls.VersionTLS13,Certificates:[]tls.Certificate{{Certificate:[][]byte{der},PrivateKey:key}}},Handler:http.HandlerFunc(func(w http.ResponseWriter,r *http.Request){w.Write([]byte("198.18.0.1"))})}
 go target.ServeTLS(listener,"","");t.Cleanup(func(){target.Close()})
 udp,err:=net.ListenPacket("udp","127.0.0.1:0");if err!=nil{t.Fatal(err)};port:=udp.LocalAddr().(*net.UDPAddr).Port;udp.Close()
 start:=func(config map[string]any)*box.Box{t.Helper();ctx:=baseContext(nil);raw,_:=json.Marshal(config);options,err:=parseConfig(ctx,string(raw));if err!=nil{t.Fatal(err)};instance,err:=box.New(box.Options{Context:ctx,Options:options});if err!=nil{t.Fatal(err)};if err=instance.Start();err!=nil{t.Fatal(err)};t.Cleanup(func(){instance.Close()});return instance}
 start(map[string]any{"log":map[string]any{"disabled":true},"inbounds":[]any{map[string]any{"type":"hysteria2","listen":"127.0.0.1","listen_port":port,"users":[]any{map[string]any{"password":"fixture"}},"tls":map[string]any{"enabled":true,"certificate":[]string{certPEM},"key":[]string{keyPEM}}}},"outbounds":[]any{map[string]any{"type":"direct","tag":"direct"}}})
 digest:=sha256.Sum256(der)
 client:=start(map[string]any{"log":map[string]any{"disabled":true},"outbounds":[]any{map[string]any{"type":"hysteria2","tag":"tunnel","server":"127.0.0.1","server_port":port,"password":"fixture","tls":map[string]any{"enabled":true,"server_name":"127.0.0.1","certificate_sha256":[]string{base64.StdEncoding.EncodeToString(digest[:])}}}}})
 core:=&PersonalInstance{box:client}
 for i:=0;i<8;i++{result,err:=core.VerifyAt("https://"+listener.Addr().String()+"/ip",certPEM);if err!=nil{t.Fatalf("health %d: %v",i,err)};if result!="198.18.0.1"{t.Fatalf("unexpected authenticated response: %q",result)}}
}
