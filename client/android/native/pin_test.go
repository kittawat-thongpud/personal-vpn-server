package tls

import (
 "context"
 "crypto/sha256"
 "net/http"
 "net/http/httptest"
 "testing"
 "time"
 "github.com/sagernet/sing-box/log"
 "github.com/sagernet/sing-box/option"
)

// Actual TLS handshake to a self-signed endpoint: a full-certificate pin must
// authenticate the endpoint and a wrong pin must fail, even with insecure=true.
func TestPersonalCertificatePinHandshake(t *testing.T) {
 server:=httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter,r *http.Request){w.WriteHeader(204)}));defer server.Close()
 pin:=sha256.Sum256(server.Certificate().Raw)
 for _,valid:=range []bool{true,false} {
  hash:=append([]byte(nil),pin[:]...);if !valid {hash[0]^=255}
  config,err:=NewSTDClient(context.Background(),log.NewNOPFactory().NewLogger("pin-test"),"localhost",option.OutboundTLSOptions{Enabled:true,Insecure:true,CertificateSHA256:[][]byte{hash}})
  if err!=nil {t.Fatal(err)}
  std,err:=config.STDConfig();if err!=nil {t.Fatal(err)}
  transport:=&http.Transport{TLSClientConfig:std};client:=&http.Client{Transport:transport,Timeout:3*time.Second}
  response,err:=client.Get(server.URL)
  if valid && err!=nil {t.Fatalf("valid pin rejected: %v",err)}
  if !valid && err==nil {response.Body.Close();t.Fatal("wrong pin accepted")}
  if response!=nil {response.Body.Close()};transport.CloseIdleConnections()
 }
}
