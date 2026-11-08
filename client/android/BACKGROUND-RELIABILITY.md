# Background reliability checks

Client 0.9.4 automatically opens a review on a fresh Launcher entry only when a
restriction is actually detected. The automatic sheet contains only those issues.
Configured, unavailable and manufacturer-only manual checks, and previous process
exit records, do not trigger a pop-up. A Recent Apps/history entry, notification
or widget command does not request this review. Pending connection work defers
the sheet so Cancel remains reachable. Fixing the detected issues and returning
from Settings or checking again dismisses the automatic review.

Settings → Background reliability keeps optional device checks, configured
settings, unavailable readings and last-exit evidence in separate collapsed
sections. Each actionable check uses two or three numbered steps. I've set this
persists the user's manual acknowledgement; Review again reverses it. These
choices never relabel a measured restriction as safe or change VPN configuration.
All reads run away from the UI thread; the compatibility status request has a
two-second bound.

| Item | What can be read | User action |
| --- | --- | --- |
| Android battery optimization | Own package's Doze exemption | Don't optimize for continuous VPN use |
| Background restriction | ActivityManager.isBackgroundRestricted | Allow background use / Unrestricted |
| Battery Saver | PowerManager.isPowerSaveMode | Turn off while continuous VPN is needed |
| Data Saver | Own application's background restriction status | Allow unrestricted/background data |
| Notifications | App permission and VPN channel importance | Allow notifications and the VPN channel |
| Unused-app restriction | AndroidX permission auto-reset / hibernation status | Disable unused-app suspension for this app |
| Don't keep activities | Public Settings.Global value | Turn off the developer option |
| Developer background process limit | No public status API used | Manually check Standard limit when developer options are enabled |

Configured, Change recommended, Check manually and Could not check are distinct. A denied
read is never shown as a safe setting or a detected disabled switch. The checker
does not change any setting, add permissions, alter VPN profiles or reconnect.
Buttons use public Settings intents via ActivityResult, with App info/system
Settings fallbacks if a device omits a destination. Vendor-only pages have
instructions and an accurately named broader Settings button; no private OEM
status is asserted. Notification visibility alone is not evidence of a kill.

## Samsung / One UI

The reported device is Galaxy A56 5G (SM-A566E/DS), One UI 9 / Android 17. The
following lists are manual checks because public Android APIs do not expose them:

- Battery → Background usage limits: remove Personal VPN from Sleeping / Deep
  sleeping apps and use Never auto sleeping apps where available.
- App info → Battery: Unrestricted. Menus and eligible list entries vary by build.
- Device care → Memory → Excluded apps: add Personal VPN if available to protect
  it from memory cleanup.
- Device care → Auto optimization → Auto restart: review scheduled/automatic
  phone restarts if they interrupt sessions. This is separate from a live
  Hysteria connection stuck in Verifying.

The app also retains Xiaomi/Redmi/POCO instructions for No restrictions,
Background autostart, screen-lock mobile-data/cache limits and optional Clear all
protection. Those are marked manual too; Android Doze exemption does not certify
that every OEM rule is disabled. A protected Recent task must be unlocked before
deliberately removing it to stop VPN.

## Process exit evidence and limits

On API 30+, only this application's historical process exit reason/time is read.
Crash, native crash, ANR, low memory, user/task stop and unknown reasons are kept
distinct. Android 17 documents a memory limiter affecting all target versions;
the checker recognizes its fixed MemoryLimiter:AnonSwap marker for REASON_OTHER.
Raw descriptions, traces and device identifiers are not displayed or saved.
The device heading uses manufacturer/model/Android version, never IMEI, SIM,
phone number or serial. A cached process with no VPN/service is not a zombie VPN.

An exit reason does not prove a battery policy caused it. The friend's Hysteria
Verifying problem and intermittent REALITY disconnects need backend phase/result
logs to establish their network cause. Startup guidance supplements cancellation
and recovery; it does not silently switch protocol or fall back to Direct.

Framework qualification runs on Android 14 (API 34) and Android 17 (API 37.0
official system-image package). UI cases check Launcher filtering, explicit
manual review, saved acknowledgement across Activity recreation, numbered
instructions and dismissal after a simulated restriction is resolved, without
changing device settings. Existing cancellation/capture/handovers remain enabled.
Physical Samsung and Redmi behavior still requires acceptance. Source/status is
recorded in [Client validation](../VALIDATION.md).

## Sources

- [Android power restrictions](https://source.android.com/docs/core/power/app_mgmt)
- [Unused-app restrictions](https://developer.android.com/reference/androidx/core/content/UnusedAppRestrictionsConstants)
- [Settings intent contracts](https://developer.android.com/reference/android/provider/Settings)
- [Android 17 memory limits](https://developer.android.com/about/versions/17/behavior-changes-all)
- [Samsung sleeping apps](https://www.samsung.com/us/support/answer/ANS10002534/)
- [Samsung Device care / auto restart](https://www.samsung.com/us/support/answer/ANS10001953/)
- [Samsung memory exclusion](https://www.samsung.com/us/support/answer/ANS10002029/)
- [Xiaomi autostart](https://www.mi.com/global/support/faq/details/KA-507608/)
- [Xiaomi background app battery policy](https://www.mi.com/my/support/faq/details/KA-538010/)
- [Xiaomi Background App lock](https://www.mi.com/global/support/faq/details/KA-535435/)
- [Xiaomi screen-lock mobile data](https://www.mi.com/uk/support/faq/details/KA-170677/)
