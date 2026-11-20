// Independent lab executable. It never binds to a source address or adapter.
package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"time"
)

type result struct {
	Reached bool   `json:"reached"`
	LocalIP string `json:"localIP,omitempty"`
}

func main() {
	if len(os.Args) != 4 || (os.Args[1] != "tcp" && os.Args[1] != "udp") {
		os.Exit(2)
	}
	command, err := bufio.NewReader(os.Stdin).ReadString('\n')
	if err != nil || (command != "probe\n" && command != "probe\r\n") {
		os.Exit(2)
	}
	_ = json.NewEncoder(os.Stdout).Encode(probe(os.Args[1], os.Args[2], os.Args[3]))
}

func probe(network, address, name string) result {
	var r result
	conn, err := net.DialTimeout(network+"4", address, 4*time.Second)
	if err != nil {
		return r
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(4 * time.Second))
	if network == "udp" {
		payload := make([]byte, 20)
		copy(payload, []byte{0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42})
		if _, err = rand.Read(payload[8:]); err != nil {
			return r
		}
		if _, err = conn.Write(payload); err != nil {
			return r
		}
		r.LocalIP = conn.LocalAddr().(*net.UDPAddr).IP.String()
		response := make([]byte, 2048)
		n, err := conn.Read(response)
		r.Reached = err == nil && n >= 20 && response[0] == 1 && response[1] == 1 && bytes.Equal(response[4:20], payload[4:20])
		return r
	}
	r.LocalIP = conn.LocalAddr().(*net.TCPAddr).IP.String()
	secure := tls.Client(conn, &tls.Config{ServerName: name, MinVersion: tls.VersionTLS12})
	if _, err = fmt.Fprintf(secure, "GET /cdn-cgi/trace HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n", name); err != nil {
		return r
	}
	resp, err := http.ReadResponse(bufio.NewReader(secure), nil)
	if err != nil {
		return r
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 4096))
	r.Reached = err == nil && resp.StatusCode == 200 && bytes.Contains(body, []byte("\nip="))
	return r
}
