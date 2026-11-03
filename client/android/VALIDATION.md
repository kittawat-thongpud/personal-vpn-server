# Android validation

0.1.0 is under active CI validation. Do not treat this file as proof of an APK
release or physical-phone test until the completed results are recorded below.

Qualification requires JVM policy/import/log tests, a real TLS pin-match and
pin-mismatch handshake, signature verification and Android 14 emulator tests of
both protocols, OS capture, a different-UID app Block, App-over-Site priority,
UDP forwarding and capture cleanup. Fixture credentials are generated on the
isolated runner; the user's VPS credentials are not uploaded to CI.

The target Redmi Note 13 Pro+ 5G / Android 14 is not connected through ADB in
this workspace. Physical throughput, Wi-Fi/cellular transitions, screen-off and
OEM battery policies are not claimed as tested.
