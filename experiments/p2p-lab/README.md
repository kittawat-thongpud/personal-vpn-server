# Personal VPN P2P Lab — isolated experiment

This is a **separate test tool**, not a new stable VPN client release. It never
installs an adapter, changes routes/proxy/DNS, contacts our VPS, opens SSH/RDP
listeners or implements Internet forwarding. The installed Client 0.7.1 stays
unchanged. All APK/HTML packaging and browser/emulator qualification run in Actions.

## ทดลอง Windows ↔ Android

1. Windows: เปิด `Personal-VPN-P2P-Lab.html` ด้วย Edge/Chrome รุ่นปัจจุบัน
   Android 14 ขึ้นไป: ติดตั้ง `PersonalVPN-P2P-Lab.apk` และเปิด **Personal VPN P2P Lab**
   เป็นอีกแอปหนึ่ง ไม่ทับ Personal VPN เดิม และไม่ขอสิทธิ์ VPN
2. เริ่มด้วย Wi-Fi/LAN เดียวกันทั้งสองเครื่องและเลือก **LAN**
   ไม่ต้องปิด VPN สำรองที่ใช้คุยกันอยู่ ระหว่างทดลองให้หน้าจอทั้งคู่เปิดไว้
3. เครื่อง A กด **สร้างคำเชิญ** ส่งข้อความหรือไฟล์คำเชิญให้ B ผ่านช่องทางที่ไว้ใจ
   B กด **รับคำเชิญ** วาง/อ่านไฟล์ แล้วกด **สร้างคำตอบ** ส่งคำตอบกลับให้ A
   A วางคำตอบแล้วกด **เชื่อมต่อด้วยคำตอบ** ต้องทำภายใน 10 นาที
4. เปรียบเทียบรหัสยืนยันให้ตรงกัน **บนทั้งสองเครื่อง** แล้วกดยืนยันทั้งคู่
5. กด **ส่ง 1 MiB + ตรวจ SHA-256** บน A แล้วทำกลับจาก B ดู ping, อัตราข้อมูล
   และผล `SHA-256 OK` จากนั้น **Logs → Export ผลทดสอบ** เก็บผลของทั้งสองเครื่อง
   ไฟล์ผลลัพธ์ไม่ใส่ SDP, ICE password, IP/port หรือเนื้อหาข้อมูลทดสอบ
6. เมื่อ LAN ใช้ได้ ค่อยทดลองคนละเครือข่าย โดยทั้งคู่เลือก **Internet / STUN**
   ก่อนสร้างคู่ใหม่ ค่าเริ่มต้น STUN คือ `stun:stun.cloudflare.com:3478`
   STUN ใช้ค้นหา endpoint เท่านั้น ไม่มี TURN/relay หรือ automatic Direct fallback
   หากขึ้น `No direct path` แปลว่ารอบนี้หาเส้นทางใช้ไม่ได้ ไม่แก้ VPN เดิมให้อัตโนมัติ

คำเชิญ/คำตอบมี ICE credentials ชั่วคราวและอาจมี IP ปลายทาง ส่งให้เครื่องที่ไว้ใจ
เท่านั้น ไม่แนบไฟล์จับคู่ใน issue/log สาธารณะ ปุ่มหยุดจะปิด peer และล้างข้อความ
จับคู่ในหน้าแอป; ไฟล์/clipboard ที่ผู้ใช้บันทึกไว้นอกแอปต้องจัดการแยกเอง

## What the result proves

SHA-256 success proves a real encrypted WebRTC Data Channel transferred and
verified 1 MiB in that direction. The identity comparison binds the session and
both DTLS certificates. Reports distinguish actual application payload from
all Internet/VPN traffic. Transfer goodput includes the peer's hash verification
and acknowledgement; it is not a line-rate bandwidth benchmark.

Selected ICE candidates are inspected: relay candidates in configuration,
remote signaling or selected stats are rejected. A stale succeeded/nominated
pair is not treated as the selected path. There is no TURN configuration or
external signaling endpoint, CDN, telemetry, storage, camera or microphone.
The optional STUN provider can observe discovery metadata; peers learn usable
endpoints. No-candidate/failed paths remain unavailable.

**No TURN does not prove physical-network bypass.** Windows WebRTC can select
ZeroTier/Tailscale interfaces, and Android's active VPN can capture the lab's
sockets. The separate Android app deliberately has no VpnService/protect API.
Its report always says `physicalBypassVerified: false`. An explicit Bypass rule
for **Personal VPN P2P Lab** in the existing VPN may allow a useful next test;
this still needs packet/path evidence before claiming native physical P2P.
Do not globally disable the communication VPN or change host routes for this test.

No automatic ICE restart/re-pairing after Wi-Fi/cellular handover is claimed.
The lab reports disconnect/timeout and asks for a fresh exchange. Background
survival/foreground service, group IPs, SSH/RDP port forwarding and production
mesh integration are future work. This experiment alone cannot qualify China
cross-border reachability or failure isolation inside the production native core.

## Qualification and source

`codex/p2p-lab` is an experimental branch; no stable tag, Latest marker or
production snapshot is moved. The workflow publishes a short-lived **Actions
artifact**, not a partial client release. The Android package is
`dev.personalvpn.p2plab`; its version derives from `client/VERSION` plus `-lab.1`.
The temporary APK uses a job-generated test signing key, not the production VPN
identity. Uninstall the old lab before installing a later artifact if Android
reports a signature mismatch; never uninstall the stable VPN for this reason.

CI checks input/relay rejection, bounded logs, identity binding, real separate
Chromium peers with bidirectional payloads, stale cancellation, Android WebView
peers and close behavior. Those local/emulated checks are recorded separately
from the required real Windows ↔ Redmi / Wi-Fi ↔ cellular / cross-border trials.

Sources: [WebRTC](https://www.w3.org/TR/webrtc/),
[stats](https://www.w3.org/TR/webrtc-stats/),
[ICE](https://www.rfc-editor.org/info/rfc8445/),
[Android VPN](https://developer.android.com/develop/connectivity/vpn),
[Cloudflare STUN endpoint](https://developers.cloudflare.com/realtime/turn/).
