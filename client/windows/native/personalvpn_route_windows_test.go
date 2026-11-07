//go:build windows

package tun

import (
    "net/netip"
    "testing"

    "github.com/sagernet/sing-tun/internal/winipcfg"
)

func TestPersonalVPNDestinationRoutes(t *testing.T) {
    r := func(prefix, name string, index int, metric uint64, safe bool) personalVPNRoute {
        return personalVPNRoute{netip.MustParsePrefix(prefix), name, index, metric, safe}
    }
    lan := r("0.0.0.0/0", "Ethernet", 2, 25, true)
    wifi := r("0.0.0.0/0", "Wi-Fi", 5, 45, true)
    for _, tc := range []struct { name, destination string; routes []personalVPNRoute; index int; fail bool }{
        {"LAN only portal", "10.184.6.32", []personalVPNRoute{lan}, 2, false},
        {"Wi-Fi only", "1.1.1.1", []personalVPNRoute{wifi}, 5, false},
        {"both defaults no Wi-Fi preference", "10.184.6.32", []personalVPNRoute{wifi,lan}, 2, false},
        {"Wi-Fi subnet over LAN default", "10.180.9.2", []personalVPNRoute{lan,wifi,r("10.180.0.0/16","Wi-Fi",5,400,true)}, 5, false},
        {"LAN subnet over Wi-Fi default", "10.181.9.2", []personalVPNRoute{wifi,r("10.181.0.0/16","Ethernet",2,800,true)}, 2, false},
        {"IPv6 longest prefix", "fd00:1::5", []personalVPNRoute{r("::/0","Wi-Fi",5,1,true),r("fd00:1::/64","Ethernet",2,800,true)}, 2, false},
        {"no physical IPv6 route", "2606:4700:4700::1111", []personalVPNRoute{lan,wifi}, 0, true},
        {"no interface after unplug", "10.184.6.32", nil, 0, true},
        {"unsafe preferred link never falls back", "10.184.6.32", []personalVPNRoute{wifi,r("0.0.0.0/0","Ethernet",2,25,false)}, 2, true},
        {"changed metric switches egress", "1.1.1.1", []personalVPNRoute{wifi,r("0.0.0.0/0","Ethernet",2,100,true)}, 5, false},
    } {
        t.Run(tc.name,func(t *testing.T){
            selected, err := personalVPNSelectRoute(netip.MustParseAddr(tc.destination),tc.routes)
            if (err != nil) != tc.fail || (!tc.fail && selected.index != tc.index) { t.Fatalf("selected=%+v error=%v",selected,err) }
        })
    }
}

func TestPersonalVPNInterfaceEligibility(t *testing.T) {
    for _, tc := range []struct{name string; kind winipcfg.IfType; want bool}{
        {"Ethernet Killer E2600",winipcfg.IfTypeEthernetCSMACD,true},
        {"Wi-Fi Intel Wireless",winipcfg.IfTypeIEEE80211,true},
        {"Ethernet Microsoft Hyper-V Network Adapter",winipcfg.IfTypeEthernetCSMACD,true},
        {"ZeroTier One",winipcfg.IfTypeEthernetCSMACD,false},
        {"Tailscale",winipcfg.IfTypeEthernetCSMACD,false},
        {"PersonalVPN",winipcfg.IfTypeEthernetCSMACD,false},
        {"vpn07 Wintun",winipcfg.IfTypeEthernetCSMACD,false},
        {"Loopback",winipcfg.IfTypeSoftwareLoopback,false},
    } {
        if got:=personalVPNUnderlay(tc.name,tc.kind);got!=tc.want {t.Errorf("%s eligibility=%v",tc.name,got)}
    }
}
