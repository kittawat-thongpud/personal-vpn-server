// Copied into the pinned sing-box source only for Personal VPN Windows builds.
package pvnpath

import (
	"fmt"
	"os"
	"sync/atomic"
	"time"
)

var enabled = os.Getenv("PVN_PATH_TIMING") == "1"
var reserved atomic.Uint32

func noop() {}

// The qualification runner opts in for its owned child only. Never include
// addresses, app names, rule text, or packet bytes. Cap output per process.
func Start(stage string) func() {
	if !enabled || reserved.Load() >= 256 || reserved.Add(1) > 256 {
		return noop
	}
	start := time.Now()
	return func() { fmt.Fprintf(os.Stderr, "pvn-path %s %d\n", stage, time.Since(start).Nanoseconds()) }
}
