// Diagnostic only: the Android native HTTP client's Go TLS stack, through an
// explicitly supplied loopback SOCKS proxy. No host routes or proxy settings.
package main
import (
 "fmt"
 "io"
 "net/http"
 "net/url"
 "os"
 "time"
)
func main() {
 proxy,err:=url.Parse(os.Args[1]);if err!=nil || proxy.Scheme!="socks5" || proxy.Hostname()!="127.0.0.1" {panic("loopback SOCKS URL required")}
 transport:=&http.Transport{Proxy:http.ProxyURL(proxy)};defer transport.CloseIdleConnections()
 client:=&http.Client{Transport:transport,Timeout:15*time.Second}
 response,err:=client.Get("https://api.ipify.org/");if err!=nil{fmt.Println(err);os.Exit(1)};defer response.Body.Close()
 body,err:=io.ReadAll(io.LimitReader(response.Body,128));if err!=nil{panic(err)};fmt.Printf("HTTP %d, exit %s\n",response.StatusCode,body)
}
