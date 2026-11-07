//go:build windows

package tun

import (
    "net/netip"
    "slices"
    "strings"

    "github.com/sagernet/sing-tun/internal/winipcfg"
    E "github.com/sagernet/sing/common/exceptions"
    "golang.org/x/sys/windows"
)

type personalVPNRoute struct {
    prefix netip.Prefix
    name string
    index int
    metric uint64
    safe bool
}

// Each Direct connection follows the longest destination route on an UP
// Ethernet/Wi-Fi link, then the combined route + interface metric. A single
// default-interface binding cannot reach LAN destinations on the other link.
// Never add OS exclusions for Site Bypass: those would defeat App Block/Tunnel.
func personalVPNSelectRoute(address netip.Addr, routes []personalVPNRoute) (personalVPNRoute, error) {
    var selected personalVPNRoute
    found := false
    for _, route := range routes {
        if !route.prefix.Contains(address) { continue }
        if !found || route.prefix.Bits() > selected.prefix.Bits() ||
            (route.prefix.Bits() == selected.prefix.Bits() && (route.metric < selected.metric || route.metric == selected.metric && route.index < selected.index)) {
            selected, found = route, true
        }
    }
    if !found { return selected, ErrNoRoute }
    // Do not silently choose a different link or recurse through capture when
    // Windows cannot enforce the chosen interface binding.
    if !selected.safe { return selected, E.New("unsafe interface binding: forwarding or weak host send; guard preparation required") }
    return selected, nil
}

func personalVPNUnderlay(name string, kind winipcfg.IfType) bool {
    if kind != winipcfg.IfTypeEthernetCSMACD && kind != winipcfg.IfTypeIEEE80211 { return false }
    lower := strings.ToLower(name)
    for _, excluded := range []string{"personalvpn", "zerotier", "tailscale", "vpn", "tap", "tun"} {
        if strings.Contains(lower, excluded) { return false }
    }
    return true
}

// Called by the auto-detect socket controller; read-only, no physical routes or
// interface properties are modified by the native core. UDP uses this too.
func PersonalVPNBypassInterface(address netip.Addr, captureNames []string) (string, int, error) {
    address = address.Unmap()
    family := winipcfg.AddressFamily(windows.AF_INET)
    if address.Is6() { family = windows.AF_INET6 }
    rows, err := winipcfg.GetIPForwardTable2(family)
    if err != nil { return "", -1, err }
    var candidates []personalVPNRoute
    for _, route := range rows {
        prefix := route.DestinationPrefix.Prefix()
        if !prefix.IsValid() || !prefix.Contains(address) || route.Loopback { continue }
        link, err := route.InterfaceLUID.Interface()
        if err != nil || link.OperStatus != winipcfg.IfOperStatusUp || slices.Contains(captureNames, link.Alias()) { continue }
        if !personalVPNUnderlay(link.Alias()+" "+link.Description(), link.Type) { continue }
        ip, err := route.InterfaceLUID.IPInterface(family)
        if err != nil || !ip.Connected || (prefix.Bits() == 0 && ip.DisableDefaultRoutes) { continue }
        candidates = append(candidates, personalVPNRoute{prefix, link.Alias(), int(link.InterfaceIndex), uint64(route.Metric)+uint64(ip.Metric), !ip.ForwardingEnabled && !ip.WeakHostSend})
    }
    selected, err := personalVPNSelectRoute(address, candidates)
    if err != nil { return "", -1, err }
    return selected.name, selected.index, nil
}
