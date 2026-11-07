package tun

import (
    "net/netip"
    "slices"

    "github.com/sagernet/sing-tun/internal/winipcfg"
    "golang.org/x/sys/windows"
)

type interfaceFamily struct {
    luid winipcfg.LUID
    family winipcfg.AddressFamily
}

func routeFamilies(routeRanges []netip.Prefix) []winipcfg.AddressFamily {
    var families []winipcfg.AddressFamily
    if slices.ContainsFunc(routeRanges, func(p netip.Prefix) bool { return p.Addr().Is4() }) { families = append(families, windows.AF_INET) }
    if slices.ContainsFunc(routeRanges, func(p netip.Prefix) bool { return p.Addr().Is6() }) { families = append(families, windows.AF_INET6) }
    return families
}

// Personal VPN's independent guard now owns the durable strong-host lease.
// Native callbacks MUST NOT mutate physical adapters or retain recovery state
// only in RAM. Every Direct socket checks binding safety before it is opened.
func (t *NativeTun) enforceInterfaceBinding(_ winipcfg.LUID) {}
func (t *NativeTun) restoreWeakHostSend() {}

func addressFamilyName(family winipcfg.AddressFamily) string {
    if family == windows.AF_INET { return "IPv4" }
    return "IPv6"
}
