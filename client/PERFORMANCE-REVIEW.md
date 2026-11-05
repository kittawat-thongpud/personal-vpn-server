# VPN stability and performance survey

สถานะอัปเดต 2026-11-06: ดำเนินงาน Android recovery/health check ระยะแรกแล้วใน
Client 0.7.2 ดู [implementation](android/RECOVERY.md) และ
[ผล qualification](VALIDATION.md) รายงานด้านล่างเป็นหลักฐานจาก baseline 0.7.1
ขณะทำ survey; การวัด throughput และการปรับ Windows/VPS ยังเป็นงานถัดไป

วันที่: 2026-11-05 (Asia/Shanghai)

ฐานที่ตรวจ: Client 0.7.1, main `82944ff853b7947e905cdb69be86330853009f4a`
และ native sing-box ที่ pin `c005f35300ce76427dc285481ec5695f5b179cd2`
พร้อม REALITY compatibility patch ของโครงการ งาน survey อยู่ branch
`codex/vpn-performance-review`; งาน P2P หยุดไว้บน branch เดิม

รอบนี้เป็น source review และตรวจเอกสาร upstream ไม่เปลี่ยน client ที่ติดตั้ง,
VPS, routes, DNS, protocol config หรือ release และยังไม่ได้ benchmark throughput
หรือทดสอบ handover บน Redmi จริง

## สิ่งที่ผู้ใช้พบ กับสิ่งที่ยังไม่รู้

- Android เปลี่ยน Wi-Fi → mobile แล้วบางครั้งไม่กลับมาต่อเอง ทั้ง Hysteria 2
  และ REALITY; กด Reconnect เองแล้วกลับมาใช้งานได้
- บางครั้ง YouTube login ไม่ได้ และ ChatGPT ดาวน์โหลดบางไฟล์ไม่ได้
- ยังไม่มี log ช่วงที่ automatic recovery หยุด จึงยังระบุสาเหตุเดียวไม่ได้
- ในโค้ด retry ปกติ **ไม่มีจำนวนครั้งสูงสุด**; backoff Android สูงสุดประมาณ
  30 วินาทีบวก jitter จึงไม่ควรสรุปว่า retry ถูกใช้จนหมด
- VPS ที่แจ้งไว้: 1 vCPU, RAM 1 GB, SSD 25 GB ข้อมูลก่อนหน้านี้มี RAM available
  519 MiB และ CPU idle 91–96% แต่เป็น snapshot เก่า ไม่ใช่ผลขณะโหลดหนัก
  จึงใช้ยืนยันความเร็วสูงสุดหรือสภาพปัจจุบันไม่ได้

## ลำดับที่ควรทำ

| ลำดับ | งาน | ผลที่ต้องการ | สถานะหลักฐาน |
| --- | --- | --- | --- |
| 1 | Android recovery supervisor + network generation | ต่อกลับเอง และทิ้งผลจากเครือข่ายเก่า | พบช่องว่างในโค้ด; ต้องเก็บ log เพื่อจับคู่กับอาการจริง |
| 2 | แยก health check ออกจากการทำลาย core และยกเลิกงานค้างได้ | ลด disconnect ที่เราเป็นคนสร้างเอง | เห็น behavior ปัจจุบันชัดเจน |
| 3 | ตรวจ Windows capture lifetime และ retry state | ลดช่วงไม่มี capture และเวลารอโดยไม่จำเป็น | เห็น teardown/backoff path ในโค้ด |
| 4 | DNS, app attribution, domain/CDN และ HTTP/3 | ลดอาการบางบริการใช้ได้ไม่ครบ | มีจุดเสี่ยง; ยังไม่ยืนยันว่าเป็นเหตุของ YouTube/ChatGPT |
| 5 | A/B TUN stack, MTU และจำนวนชั้นของ Windows | เพิ่ม throughput ต่อ CPU และลด latency | เป็นตัวเลือกที่จะวัด ไม่ใช่ speedup ที่พิสูจน์แล้ว |
| 6 | Server socket buffers / congestion / sizing | ปรับเฉพาะคอขวดที่วัดพบ | ยังไม่มีการวัด under-load ใหม่ |

## Android: ข้อค้นพบจากโค้ด

### A1 — ไม่มีตัวคอยฟื้น worker ที่จบขณะยังต้องการเชื่อมต่อ

`TunnelService.kt:73–80, 148–149`: service ใช้ `desired` ตัดสินว่าเชื่อมต่ออยู่
แต่ไม่ได้ตรวจ `worker.isActive` ก่อนข้ามคำสั่ง CONNECT ถ้าออกทาง outer error
handler จะเปลี่ยนเป็น Error แล้วจบ worker โดยยังคง `desired = true`
ไม่มี completion supervisor/watchdog ที่เริ่ม worker ใหม่เมื่อ network กลับมา
callback ทำเพียงส่ง wake ให้ channel; channel ไม่สามารถสร้าง worker ที่จบแล้วได้

นี่คือช่องว่างที่ยืนยันได้จาก control flow ส่วนเหตุที่ทำให้เครื่องจริงเข้าทางนี้
ยังต้องดู log ไม่ใช่ทุก network failure จะทำให้ worker จบ เพราะ inner loop retry อยู่

แนวทาง: แยก user intent, worker lifetime และ verified connection state;
มี supervisor ที่ serialize คำสั่งและ restart เฉพาะ recoverable failures ด้วย
backoff ที่มีขอบเขต Config/auth/consent errors ต้องแสดงเหตุชัดเจนและไม่วนถี่
เก็บ completion reason และเวลาที่ progress ล่าสุด โดยยังคง log limit เดิม

### A2 — network snapshot มี race และตรวจการเปลี่ยนแปลงไม่ครบ

`TunnelService.kt:52–60, 178–185`: callbacks ทิ้ง capabilities/link properties
ที่ Android ส่งมา แล้ว query `allNetworks`/capabilities ซ้ำหลายครั้ง รวมถึง `!!`
หลังจาก filter ไปแล้ว Network อาจหายระหว่างสองรอบ ทำให้เกิด exception ได้
Android เตือนเรื่อง synchronous queries ภายใน callback โดยตรงใน
[network-state guide](https://developer.android.com/develop/connectivity/network-ops/reading-network-state)

signature ปัจจุบันมี Network/interface/DNS แต่ไม่มี local addresses, routes,
MTU หรือ validated/suspended state การเปลี่ยนบางแบบบน Network เดิมจึงไม่ปลุก
recovery และไม่มี debounce/hysteresis สำหรับการแกว่งของ Wi-Fi/cellular

แนวทาง: สร้าง snapshot จาก callback arguments บน serial event loop;
ใช้ generation เพิ่มเมื่อเส้นทางเปลี่ยนจริง เลือก physical network ที่เหมาะสม
โดยไม่ถือว่า Wi-Fi ต้องดีกว่ามือถือเสมอ แยก transport-changing events ออกจาก
metadata-only events เพื่อไม่ reset connection เมื่อแค่สถานะ metered เปลี่ยน

`setUnderlyingNetworks()` ถูกเรียกเฉพาะตอน selected ไม่เป็น null และอาจถูกเรียก
ก่อน TUN establish โดยไม่ตรวจผล ไม่แจ้ง empty set ตอน upstream หาย และไม่ apply
อีกครั้งโดยตรงใน openTun ควรผูก underlying-network metadata กับ capture lifetime
ตาม [VpnService API](https://developer.android.com/reference/android/net/VpnService#setUnderlyingNetworks(android.net.Network[]))
ค่านี้เป็นการแจ้งเครือข่ายที่ VPN ใช้ ไม่ได้ย้าย socket เดิมให้เอง

### A3 — งานเก่าไม่ถูกยกเลิกทันที และ health check ล็อก control operations

`TunnelService.kt:98, 106–130`: DNS ใช้ blocking `Network.getAllByName` โดยไม่มี
deadline ที่แอปควบคุม; ถ้าไม่พบ IPv4 จะเรียก resolver ซ้ำ ผล HTTPS ไม่มีการตรวจ
generation ของ physical network ก่อนประกาศ Connected

`native/core.go:25–30`: Verify ถือ mutex เดียวกับ Close, NetworkChanged และ
อ่าน byte counters ตลอด network request; native health timeout 12 วินาที
(`personal_instance.go:69–90`) ตามด้วย captured HTTPS ที่มี connect/read timeout
อย่างละ 8 วินาที การ cancel coroutine อย่างเดียวไม่หยุด blocking JNI/HTTP เหล่านี้
ทันที นอกจาก reconnect ช้าแล้ว อาจเห็น traffic graph/notification ไม่อัปเดตด้วย
สิ่งนี้ไม่แปลว่า data plane ถูกล็อกทุก packet

แนวทาง: generation-bound cancellable probes, native request contexts ที่ผูกกับ
session/network, DNS แบบ cancel ได้ เช่น
[Android DnsResolver](https://developer.android.com/reference/android/net/DnsResolver)
แยก lifetime lock ออกจาก I/O และ atomic counters อย่างปลอดภัย ต้องไม่เกิด
use-after-close หลังย้ายงานออกจาก mutex และต้องมี deadline ครอบ core startup/close

### A4 — มีเส้นทาง reset network ซ้ำ

`native/core.go:30` เรียกทั้ง platform interface update และ explicit ResetNetwork
บน handover ใน upstream ที่ pin ไว้ interface callback สามารถ schedule reset ได้อยู่แล้ว
และ reset ปิด existing connections พร้อมแจ้ง outbounds ให้ reset transport
Hysteria outbound จะปิด session ปัจจุบันเมื่อได้รับ interface update
ดู [interface monitor](https://github.com/SagerNet/sing-box/blob/c005f35300ce76427dc285481ec5695f5b179cd2/experimental/libbox/monitor.go#L126),
[network reset](https://github.com/SagerNet/sing-box/blob/c005f35300ce76427dc285481ec5695f5b179cd2/route/network.go#L438)
และ [Hysteria outbound](https://github.com/SagerNet/sing-box/blob/c005f35300ce76427dc285481ec5695f5b179cd2/protocol/hysteria2/outbound.go#L203)

การ reset ซ้ำหรือ reset ที่มาถึงช้าอาจตัด connection ที่เพิ่งสร้างใหม่ ต้อง trace
ก่อนเลือกว่าจะใช้ interface notification หรือ explicit reset ในแต่ละเหตุการณ์
เป้าหมายคือ reset หนึ่งครั้งต่อ transition ที่จำเป็น พร้อมคง TUN เดิม

### A5 — health endpoint ล้มครั้งเดียวทำลายทั้ง core

`TunnelService.kt:115–144`: ตรวจ native outbound และ captured path กับ
`api.ipify.org` ทุกประมาณ 15 วินาที เมื่อ probe ใด fail ครั้งเดียวจะ close core
และเริ่ม retry จึงมี external endpoint เดียวที่สามารถทำให้ session ที่ยังใช้งานได้
ถูกตัด รวมถึง DNS cache และ connections ของแอปต่าง ๆ ที่อยู่ใน core

แนวทาง: แยก initial authentication/capture proof ออกจาก ongoing liveness;
ใช้หลาย independent HTTPS targets ที่มี deadline, hysteresis และ failure reason
แยก DNS/TLS/auth/capture/HTTP status รักษา core เมื่อหลักฐานยังไม่พอว่าขาดจริง
probe แบบ fresh connection ยังจำเป็นหลังเปลี่ยน network เพื่อไม่รับผลจาก pool เก่า
คง certificate verification และไม่ fallback Direct อัตโนมัติ

### A6 — attribution อาจทำให้บางแอปใช้ได้ไม่ครบ

`TunnelService.kt:87, 200–206`, `Model.kt:132–135`: เมื่อ App Tunnel mode ไม่ใช่
Disabled จะ reject unresolved UID และ shared UID ที่มีจำนวน packages ไม่เท่ากับหนึ่ง
นี่เป็น conservative policy ที่ตั้งใจไว้ แต่ควรมี aggregate counter/reason ใน logs
เพื่อแยกจาก DNS/transport failure โดยเฉพาะ login ที่พึ่ง service/แอปอื่น

ยังไม่มีหลักฐานว่าเครื่องนี้ YouTube ล้มเพราะ shared UID และไม่ควรแก้ด้วยการ
allow ทุก unresolved flow แนวทางที่จะศึกษา: ประเมิน effective policy ทั้ง UID;
ยอมรับได้เฉพาะเมื่อ packages ที่เกี่ยวข้องให้ผลเดียวกัน กรณีขัดกันต้องชัดเจน
cache UID→packages แบบมีขอบเขตและ invalidate เมื่อ package เปลี่ยน;
ไม่ cache socket ownership ข้ามการ reuse port อย่างไม่มีอายุ

## Windows: ความเสถียรและต้นทุนที่ลดได้

### W1 — adapter คงเดิม แต่ capture ยังถูก teardown ทุก retry

`Backend.cs:343–369, 520–535`, `TunLease.cs:76–107`, `reset-tun.ps1:7–21`:
การจบ RunCycle จะปิด transport/TUN cores และ release capture routes/DNS
ก่อนรอ backoff แม้ device จะไม่ถูกลบ การมี persistent adapter จึงไม่เท่ากับ
มี capture ตลอด recovery; ระหว่างนั้น OS อาจใช้ route ปกติได้ ไม่มีหลักฐาน
ว่ามี session-wide firewall guard รับประกัน fail-closed ในช่วงนี้

ควรแยก capture/session lifetime ออกจาก transport-attempt lifetime พร้อม guard
ที่ครอบ reconnect; ต้องรักษา ZeroTier/Tailscale exclusions และ rollback หลัง
Disconnect/crash ให้ถูกต้อง ไม่ควรแก้เพียงเพิ่ม firewall block กว้างทั้งเครื่อง

### W2 — backoff ไม่ reset หลังใช้งานสำเร็จนานแล้ว

`Backend.cs:343, 363–368`: failures สะสมข้าม RunCycle โดยไม่มีการ reset ตอน
Connected รอบถัดไปจึงอาจเริ่มด้วย backoff 30 วินาทีแม้ session ก่อนหน้าเสถียรนาน
ควร reset หลัง success/stability window และเมื่อมี physical network ใหม่ที่ใช้ได้

cleanup ยังมี PowerShell reset timeout 45 วินาที และ guard wait สูงสุด 50 วินาที
(`TunReset.cs:29`, `TunLease.cs:104`) เป็น upper bounds ของคนละขั้น ไม่ใช่ค่า
ที่เกิดจริงทุกครั้ง ต้องมี teardown timing เพื่อแยกเวลารอก่อน retry ออกจาก handshake

### W3 — data plane หลายชั้นใน TUN

จาก `Backend.cs:414–456, 476–484` และ `TunPlan.cs:62–64`:

- Windows Hysteria: TUN sing-box → SOCKS/Xray router → SOCKS/Hysteria → VPS
- Windows REALITY: TUN sing-box → SOCKS/Xray → VPS
- Android: TUN/libbox → selected outbound → VPS

Windows มี loopback sockets, buffers, scheduling และ process supervision มากกว่า
เป็น candidate สำหรับ CPU/latency optimization แต่ยังไม่มี measurement ว่าส่วนนี้ตัน
ทางเลือกระยะถัดไปคือ native single-core pipeline ที่คง Windows adapter ownership,
app policies, byte counters, TLS และ recovery เดิมให้ครบ ความเสี่ยงการย้ายสูงกว่า
การแก้ reconnect จึงควรทำหลังมี baseline เปรียบเทียบ

## DNS, TUN และพลังงาน

| จุดปัจจุบัน | ทางเลือกที่จะทดลอง | เงื่อนไข/ข้อควรคงไว้ |
| --- | --- | --- |
| Android/Windows TUN มี DoH upstream เดียว `1.1.1.1` | secondary resolver ผ่าน tunnel พร้อม timeout/failover ที่มีขอบเขต | ไม่ส่ง DNS Direct เพื่อแก้ความช้า; resolver คู่ควรเป็นคนละ failure domain |
| sniff ก่อน user app rules ทั้งสอง platform | fast path สำหรับ policy ที่ตัดสินได้แน่แล้ว; sniff เมื่อจำเป็นต่อ domain rules | ต้องคง DNS hijack และ App exceptions ชนะ Site exceptions |
| Android ใช้ gVisor และ MTU 1280; Windows mixed/1400 | A/B stack บนเวอร์ชันที่ pin; ทดสอบ MTU ภายใน 1280/1400/1500 ตามเครือข่าย | virtual TUN MTU กับ outer QUIC path MTU คนละชั้น; ไม่ตั้งค่าตาม Ethernet MTU อย่างเดียว |
| Verify share mutex กับ counters | แยก atomic stats reads จาก probe lifetime | กราฟค้างไม่ใช่หลักฐานว่าความเร็ว network เป็นศูนย์ |
| notification ทุก 1 วินาที และ state/history สร้างใหม่ตลอด | คง 1 Hz เมื่อมี traffic/หน้าจอเปิด; ลด idle/screen-off work | ไม่ตัด foreground service; resume ด้วย sample ปัจจุบัน ไม่สะสม queue |
| TUN ถูกประกาศ `setMetered(false)` เสมอ | รายงาน mobile/metered ให้ตรง physical network | ลด background work ที่แอปอื่นอาจเริ่มเพราะคิดว่าเป็น unmetered |

DNS config ไม่ได้ disable cache จึงไม่ควรสรุปว่าตอนนี้ไม่มี DNS cache
ให้วัด cache hit/miss และเวลาของ DoH ก่อนเพิ่ม cache อีกชั้น
[Pinned DNS options](https://github.com/SagerNet/sing-box/blob/c005f35300ce76427dc285481ec5695f5b179cd2/docs/configuration/dns/index.md)

ความหมาย stack ใน core ที่ใช้อยู่ตรวจจาก
[เอกสาร ณ pinned revision](https://github.com/SagerNet/sing-box/blob/c005f35300ce76427dc285481ec5695f5b179cd2/docs/configuration/inbound/tun.md#stack)
ไม่ใช้ข้อความของ core รุ่นอื่นมาอ้างเป็นผลทดสอบของเรา

## Server: ปรับได้ตรงไหน

1. **Congestion control:** config generator `vpnctl.py:234–235` ใช้
   `ignoreClientBandwidth: true` และ BBR standard อยู่แล้ว การใส่ตัวเลข Mbps
   สูงบน client จะไม่ทำให้ negotiation เปลี่ยนตามตัวเลขนั้น BBR ของ Hysteria
   และ Linux TCP BBR เป็นคนละส่วน ไม่ควรใช้ชื่อเดียวกันแล้วถือว่าปรับทั้งสองแล้ว
   [Hysteria server configuration](https://v2.hysteria.network/docs/advanced/Full-Server-Config/)
2. **Socket buffers:** `scripts/host-tuning.sh` มีทางเลือก rmem/wmem maxima
   8 MiB แต่ยังไม่ทราบว่า VPS ใช้จริงหรือไม่ ตรวจค่าที่มีผลใน container namespace
   และ delta ของ UDP RcvbufErrors/SndbufErrors ขณะโหลดก่อน พิจารณา 8→16 MiB
   เฉพาะเมื่อขาด buffer; maximum ไม่ใช่การ allocate เต็มทุก socket ทันที
3. **QUIC receive windows:** ตรวจว่าตันด้วย bandwidth-delay product ก่อนเพิ่ม
   native Hysteria docs ระบุ default stream/connection 8/20 MiB ตัวอย่าง
   100 Mbps × RTT 150 ms ต้องมี in-flight ประมาณ 1.8 MiB จึงยังไม่ใช่เหตุผล
   อัตโนมัติที่จะขยาย windows มาก ๆ อย่าสรุปว่า embedded Android library ใช้
   defaults ตรงกันโดยไม่ตรวจ pinned source การเพิ่ม windows เพิ่ม memory exposure
   [Hysteria performance](https://v2.hysteria.network/docs/advanced/Performance/)
4. **CPU/host contention:** QUIC เป็น user-space transport ต้องวัด CPU,
   softirq, steal time, GC/heap และ goodput ขณะส่งจริง 1 vCPU อาจเป็นคอขวดได้
   แต่ idle snapshot พิสูจน์ไม่ได้ ให้พิจารณา 2 vCPU เมื่อ throughput ชน CPU
   ต่อเนื่อง; เพิ่ม RAM เมื่อมี memory pressure/swap activity/OOM จริง
5. **Docker bridge:** มี NAT/conntrack เพิ่ม แต่ยังไม่มีหลักฐานว่ามันตัน
   host networking กระทบ isolation/port binding จึงไม่ควรเปลี่ยนก่อนวัด
6. **Parallelism:** วัด 1 กับ 4 streams และ latency ขณะ download/upload เต็ม
   แยกต่อ-device/app fairness จาก peak Mbps การเร่ง stream เดียวจน ping แย่
   ไม่ใช่ผลดีสำหรับ login, SSH หรือการคุยแบบ real time

ไม่เสนอ real-time process priority, huge buffers, aggressive Brutal หรือการลด
TLS/ACL checks เป็นค่าเริ่มต้น เพราะเสี่ยงทำให้ VPS เล็กและ mobile network แกว่งขึ้น

ตรวจ official latest stable ระหว่าง survey: sing-box
[v1.14.4](https://github.com/SagerNet/sing-box/releases/tag/v1.14.4) และ Hysteria
[v2.13.0](https://github.com/HyNetworks/hysteria/releases/tag/app/v2.13.0) และ Xray
[v26.10.31](https://github.com/XTLS/Xray-core/releases/tag/v26.10.31) ตรงกับ
baseline dependencies ที่ใช้ ไม่มีเหตุให้เปลี่ยนรุ่นเพียงเพราะคาดว่าจะเร็วกว่า
หน้า docs sing-box มีหัวข้อ 1.15 ล่วงหน้า; ไม่ถือว่ามี stable upgrade ผ่านการทดสอบแล้ว

## YouTube login / ChatGPT file downloads

ยังจัดเป็น compatibility diagnosis แยกจาก peak bandwidth:

- ตรวจ effective App/Site policy และ service ที่ login เรียกใช้ด้วย
- เก็บเพียง domain ที่ผู้ใช้เลือกตรวจ, status/error category และ timing;
  ไม่เก็บ cookies, Authorization, signed download URLs หรือเนื้อหาไฟล์
- เปรียบเทียบ DNS/TLS/HTTP status และ HTTP/2 vs HTTP/3 ในเส้นทางเดียวกัน
  ทดลอง TCP-only เป็นการวินิจฉัยเฉพาะงาน ไม่ปิด QUIC ทั้งเครื่องเป็นค่าเริ่มต้น
- IP reputation, session/cookie, download URL expiry และปลายทาง CDN
  เป็นสมมติฐานที่ต้องตรวจ ไม่ใช่สิ่งที่เพิ่ม vCPU หรือเปลี่ยน MTU แล้วรับประกันหาย
- ถ้า default เป็น Bypass/Block ต้องครอบคลุมบริการประกอบ/CDN ที่จำเป็น;
  ต้องมีหลักฐานก่อนเพิ่ม domain rules แทนการ allow ทั้งหมวดกว้าง

## แผนวัดและเกณฑ์รับงานก่อนเริ่ม optimize

1. **Handover:** ทั้งสอง protocol, Wi-Fi→cellular และย้อนกลับอย่างละ 20 รอบ;
   network หาย 5/30/60 วินาที, airplane mode, captive portal และ screen-off
   แยกการรอ Android validate network จากการรอ VPN เอง ตรวจว่า desired session
   มี worker ที่ยังเดิน, ไม่มีผลเก่าประกาศ Connected และไม่ต้องกด Reconnect เอง
2. **Timing:** เก็บ network generation, stage duration, retry number/deadline,
   last progress และ worker completion reason พร้อม fixed categories
   ไม่ต้องเปิด per-request browsing logs เป้าหมายเบื้องต้นที่จะตั้งหลัง baseline:
   recovery p50 ≤5s / p95 ≤15s หลัง physical network และเส้นทางถึง VPS ใช้งานได้
   ตัวเลขนี้เป็นเป้าหมายออกแบบ ไม่ใช่ผลที่วัดแล้วหรือการรับประกันทุก carrier
3. **Throughput:** แยก client↔VPS, VPS↔Internet และ full path;
   download/upload 1/4 streams, DNS cold/warm, time-to-first-byte, retransmits,
   latency ใต้ load และ CPU/RAM ของแต่ละชั้น ใช้ payload ที่ควบคุมและจำกัดขนาด
   ไม่ใช้ข้อมูลจาก session/login ของผู้ใช้เป็น benchmark
4. **Isolation:** เปลี่ยนทีละตัวแปร เช่น stack หรือ buffer ก่อน แล้วเทียบหลายรอบ
   ภายใต้เครือข่าย/ช่วงเวลาใกล้กัน ไม่สรุปจาก speed test ครั้งเดียว
5. **Acceptance:** App precedence, Block/Bypass, certificate/pin rejection,
   IPv4/IPv6 behavior, counter accuracy, TUN reuse และ Windows mesh exclusions
   ต้องผ่านพร้อมกัน performance gain จึงจะรับเข้า stable ได้

ลำดับ implementation ที่เสนอ: A1–A5 ก่อน → W1/W2 → DNS/compatibility และ
การวัด under-load → เลือก TUN/data-plane/server tuning ตามคอขวดที่พบ
ข้อความและหมายเลขบรรทัดข้างต้นอ้างถึงช่วง survey ก่อนเริ่มแก้ runtime;
สถานะ implementation ล่าสุดอยู่ในลิงก์ต้นรายงาน
