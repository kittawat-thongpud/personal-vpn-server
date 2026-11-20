// Copied into the pinned sing-box source only for Personal VPN Windows builds.
package pvnpath

import (
	"fmt"
	"os"
	"sync/atomic"
	"time"
)

var enabled = os.Getenv("PVN_PATH_TIMING") == "1"
var reserved = map[string]*atomic.Uint32{
	"policy_tcp": {}, "policy_udp": {},
	"adapter_route": {}, "adapter_default": {},
	"socket_dial": {}, "socket_udp": {},
}

func noop() {}

// The qualification runner opts in for its owned child only. Never include
// addresses, app names, rule text, or packet bytes. Cap output per process.
func Start(stage string) func() {
	if !enabled {
		return noop
	}
	// Unrelated Windows traffic must not use the budget for every stage.
	count := reserved[stage]
	if count == nil || count.Load() >= 32 || count.Add(1) > 32 {
		return noop
	}
	start := time.Now()
	return func() { fmt.Fprintf(os.Stderr, "pvn-path %s %d\n", stage, time.Since(start).Nanoseconds()) }
}

func Direct(outbound, stage string) func() {
	if outbound != "direct" {
		return noop
	}
	return Start(stage)
}
