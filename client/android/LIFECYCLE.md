# Pending connection and task removal

Client 0.9.3 keeps Cancel available during Connecting, Verifying, Reconnecting
and Waiting for network. Previously the power button was disabled for `busy`
states; its disabled content was faded over the pending yellow background.
Dirty settings could also select reconnect rather than Disconnect. Pending
Cancel now takes precedence, while an established connection with edited
settings retains Apply & reconnect.

Pending status uses light blue text, with a blue button and explicit light
content. Connected/idle/error retain distinct colors. Gradient stops and text
colors are checked for a contrast ratio of at least 4.5:1, including the disabled
Stopping label. Stopping prevents duplicate input while teardown completes.

Connect marks the session pending immediately. Accepted commands have a
generation so an older queued Stop cannot stop a newer connection. Stop cancels
DNS/network waits and native health probes, joins the existing transport cleanup,
closes capture, removes notification 1, and stops the foreground service. The
cleanup section completes even if the stopping coroutine is cancelled by a new
command or service destruction. Backend events record stop request and result.
Command generations are atomic, and Android's potentially off-main revocation
callback is serialized with command entry points before using the same stop path.

Removing the app task invokes Android Service.onTaskRemoved, using the same
stop path. Home/backgrounding retains VPN so other apps can use it. The service
explicitly keeps stopWithTask=false to receive the callback and finish cleanup.
Android Always-on is a separate system policy, managed in Android VPN settings;
the app keeps the existing managed-disconnect behavior for it.

Android 14 and 17 qualification checks Cancel through the real Home button in both
protocols, with dirty settings during Connecting and an actual stalled native
HTTPS verification. The controlled server exposes its active slow-request count
so the test knows I/O is pending before cancellation. It requires capture, VPN
network, notification and own running service to disappear within five seconds,
then checks ordinary separate-app HTTPS over the physical network. Task removal
uses Activity.finishAndRemoveTask during Connecting, Verifying and Connected;
Home retains a working captured connection. Rapid Connect/Cancel/Connect checks
that the latest command wins. Slow-server/test controls are isolated fixtures;
no user-facing command enables them.

The emulators establish framework behavior; physical Samsung/Redmi Recent Apps
swipes and manufacturer lifecycle policy require on-device acceptance. Android
may keep an idle cached application process; that is distinct from an active VPN
or foreground service. Build status is recorded in [Client validation](../VALIDATION.md).
The complete publication run for source
`a64b83d29e98fa17e9d302270765b6cacec10115` passes all seven instrumentation
tests on API 34 and API 37, including every cancellation/task-removal scenario
above. Foreground readiness is confirmed when tests reopen a removed task;
cleanup and independent traffic assertions remain enabled.

References:

- [Service.onTaskRemoved](https://developer.android.com/reference/android/app/Service#onTaskRemoved(android.content.Intent))
- [VpnService](https://developer.android.com/reference/android/net/VpnService)
