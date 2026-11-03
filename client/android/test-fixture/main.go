// Ephemeral local test gateway. No user credentials or VPS configuration.
package main
import (
 "context"
 "crypto/ecdh"
 "crypto/ecdsa"
 "crypto/elliptic"
 "crypto/rand"
 "crypto/sha256"
 "crypto/x509"
 "encoding/base64"
 "encoding/hex"
 "encoding/json"
 "encoding/pem"
 "math/big"
 "net"
 "os"
 "time"
 box "github.com/sagernet/sing-box"
 "github.com/sagernet/sing-box/include"
 "github.com/sagernet/sing-box/option"
 sj "github.com/sagernet/sing/common/json"
)
func main() {
 key,_:=ecdsa.GenerateKey(elliptic.P256(),rand.Reader)
 template:=&x509.Certificate{SerialNumber:big.NewInt(1),DNSNames:[]string{"fixture.local"},NotBefore:time.Now().Add(-time.Hour),NotAfter:time.Now().Add(time.Hour),KeyUsage:x509.KeyUsageDigitalSignature,ExtKeyUsage:[]x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
 der,_:=x509.CreateCertificate(rand.Reader,template,template,&key.PublicKey,key);digest:=sha256.Sum256(der)
 keyDER,_:=x509.MarshalECPrivateKey(key)
 certPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"CERTIFICATE",Bytes:der}));keyPEM:=string(pem.EncodeToMemory(&pem.Block{Type:"EC PRIVATE KEY",Bytes:keyDER}))
 reality,_:=ecdh.X25519().GenerateKey(rand.Reader)
 uuid:="10000000-0000-4000-8000-000000000001";authBytes:=make([]byte,24);rand.Read(authBytes);auth:=base64.RawURLEncoding.EncodeToString(authBytes)
 config:=map[string]any{"log":map[string]any{"disabled":true},"inbounds":[]any{
  map[string]any{"type":"hysteria2","listen":"0.0.0.0","listen_port":18443,"users":[]any{map[string]any{"password":auth}},"tls":map[string]any{"enabled":true,"certificate":[]string{certPEM},"key":[]string{keyPEM}}},
  map[string]any{"type":"vless","listen":"0.0.0.0","listen_port":18444,"users":[]any{map[string]any{"uuid":uuid,"flow":"xtls-rprx-vision"}},"tls":map[string]any{"enabled":true,"reality":map[string]any{"enabled":true,"handshake":map[string]any{"server":"www.amazon.co.jp","server_port":443},"private_key":base64.RawURLEncoding.EncodeToString(reality.Bytes()),"short_id":[]string{"0123456789abcdef"}}}},
 },"outbounds":[]any{map[string]any{"type":"direct","tag":"direct"}},"dns":map[string]any{"servers":[]any{map[string]any{"type":"local","tag":"system"}}},"route":map[string]any{"final":"direct","default_domain_resolver":"system"}}
 ctx:=box.Context(context.Background(),include.InboundRegistry(),include.OutboundRegistry(),include.EndpointRegistry(),include.DNSTransportRegistry(),include.ServiceRegistry(),include.CertificateProviderRegistry())
 raw,_:=json.Marshal(config);options,err:=sj.UnmarshalExtendedContext[option.Options](ctx,raw);if err!=nil{panic(err)}
 instance,err:=box.New(box.Options{Context:ctx,Options:options});if err!=nil{panic(err)};if err=instance.Start();err!=nil{panic(err)};defer instance.Close()
 fixture:=map[string]any{"reality":map[string]any{"server":"10.0.2.2","port":18444,"uuid":uuid,"sni":"www.amazon.jp","publicKey":base64.RawURLEncoding.EncodeToString(reality.PublicKey().Bytes()),"shortId":"0123456789abcdef"},"hysteria":map[string]any{"server":"10.0.2.2","port":18443,"auth":auth,"sni":"fixture.local","pin":hex.EncodeToString(digest[:]),"insecure":true,"obfs":""}}
 encoded,_:=json.Marshal(fixture);if err=os.WriteFile(os.Args[1],encoded,0600);err!=nil{panic(err)}
 socket,err:=net.ListenPacket("udp","0.0.0.0:19000");if err!=nil{panic(err)};defer socket.Close()
 buffer:=make([]byte,2048);for {n,peer,err:=socket.ReadFrom(buffer);if err==nil{socket.WriteTo(buffer[:n],peer)}}
}
