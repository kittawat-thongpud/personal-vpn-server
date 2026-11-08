# Android shared-UID App routing

Client 0.9.2 corrects the classification of known shared UIDs in App Tunnel.
Android's `getPackagesForUid` can return several packages for one UID. Versions
through 0.9.1 converted every such result into `__unresolved__` while App rules
were enabled, then rejected it before applying Site rules or the Tunnel default.
Turning App rules off avoided that rejection. This is different from Windows'
physical-interface forwarding / binding problem; Android outbound sockets are
protected and bound to the selected underlying Network.

The report was Tunnel by default, YouTube using Default, unrelated App Bypass
exceptions, and Site Bypass for `2.2.2.2` and `10.184.6.32`. Those IP exceptions
alone should not divert another destination. Shared ownership is a verified
source defect consistent with failures of helper/account services. Without
on-device owner diagnostics it is not proof that every YouTube failure has this
cause, or that YouTube itself uses a shared UID.

Known owners retain all package names. Explicit App exceptions still precede
Site rules, and the default applies when no exception matches. Because one
socket identifies a UID rather than a unique package within it, conflicts on
that UID use Block > Tunnel > Bypass, independent of saved edit order. Shared
packages cannot be separated using socket UID lookup alone.

Negative/absent UID, missing packages and lookup exceptions remain unresolved.
With App rules enabled they remain rejected, preserving existing Block/privacy
behavior. Backend notices distinguish `policy.shared_uid` from
`policy.owner_unresolved`; each kind is emitted at most once per minute per
connection session. Notices contain no destination or visited-domain history
and use the existing RAM-only bounded session log.

Qualification adds two debug-only companion APKs with the same signing key and
`sharedUserId`. Android 14 instrumentation checks their actual equal UIDs and
the real package-manager lookup, then sends separate-UID HTTPS through both VPN
protocols under the reported default/private-IP policy. It also checks an
exception attached to either package and conflicting Block/Bypass exceptions
in both edit orders. These companion APKs are not included in the release.
Unit tests cover missing owners, conflict precedence and notice rate limits.

Build and qualification status is recorded in [Client validation](../VALIDATION.md).
Physical Redmi YouTube login/data loading still requires acceptance after the
signed APK update. Keep the current rules to compare the same configuration.

References:

- [Android PackageManager.getPackagesForUid](https://developer.android.com/reference/android/content/pm/PackageManager#getPackagesForUid(int))
- [Android ConnectivityManager.getConnectionOwnerUid](https://developer.android.com/reference/android/net/ConnectivityManager#getConnectionOwnerUid(int,%20java.net.InetSocketAddress,%20java.net.InetSocketAddress))
