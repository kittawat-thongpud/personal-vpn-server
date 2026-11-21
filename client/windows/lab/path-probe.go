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
	"syscall"
	"time"
	"unsafe"
)

type result struct {
	Reached   bool    `json:"reached"`
	LocalIP   string  `json:"localIP,omitempty"`
	LocalPort int     `json:"localPort,omitempty"`
	DnsStatus *uint32 `json:"dnsStatus,omitempty"`
	WireToken string  `json:"wireToken,omitempty"`
}

func main() {
	if len(os.Args) != 4 || (os.Args[1] != "tcp" && os.Args[1] != "udp" && os.Args[1] != "dns") {
		os.Exit(2)
	}
	command, err := bufio.NewReader(os.Stdin).ReadString('\n')
	if err != nil || (command != "probe\n" && command != "probe\r\n") {
		os.Exit(2)
	}
	_ = json.NewEncoder(os.Stdout).Encode(probe(os.Args[1], os.Args[2], os.Args[3]))
}

func queryWindowsDNS(name string) result {
	dns := syscall.NewLazyDLL("dnsapi.dll")
	query, free := dns.NewProc("DnsQuery_W"), dns.NewProc("DnsFree")
	queryName, err := syscall.UTF16PtrFromString(name)
	if err != nil {
		return result{}
	}
	var records uintptr
	// Use the Windows DNS client for an uncached, fully qualified A query.
	const options = 0x8 | 0x40 | 0x80 | 0x800 | 0x1000
	code, _, _ := query.Call(uintptr(unsafe.Pointer(queryName)), 1, options, 0, uintptr(unsafe.Pointer(&records)), 0)
	if records != 0 {
		_, _, _ = free.Call(records, 1)
	}
	status := uint32(code)
	return result{Reached: status == 0 || status == 9003, DnsStatus: &status}
}

func probe(network, address, name string) result {
	if network == "dns" {
		return queryWindowsDNS(name)
	}
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
		r.WireToken = fmt.Sprintf("%x", payload[8:])
		local := conn.LocalAddr().(*net.UDPAddr)
		r.LocalIP, r.LocalPort = local.IP.String(), local.Port
		response := make([]byte, 2048)
		for attempt := 0; attempt < 6; attempt++ {
			if _, err = conn.Write(payload); err != nil {
				return r
			}
			_ = conn.SetReadDeadline(time.Now().Add(550 * time.Millisecond))
			n, readErr := conn.Read(response)
			if readErr == nil && n >= 20 && response[0] == 1 && response[1] == 1 && bytes.Equal(response[4:20], payload[4:20]) {
				r.Reached = true
				time.Sleep(75 * time.Millisecond)
			}
		}
		return r
	}
	local := conn.LocalAddr().(*net.TCPAddr)
	r.LocalIP, r.LocalPort = local.IP.String(), local.Port
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
