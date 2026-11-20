# Communication path และคอขวดของ Personal VPN

ตรวจ 21 พฤศจิกายน 2026 (Asia/Shanghai) จาก `main` ที่ `1de9e19` และวัด Windows ที่ใช้งานจริงในโหมด TUN / Hysteria 2 ผ่าน Wi‑Fi การสำรวจรอบนี้ไม่สลับ VPN ไม่แก้ route, DNS, firewall, profile หรือ VPS และไม่ได้ติดตั้ง native split tunneling บนเครื่องผู้ใช้

## เส้นทางจริงและขอบเขตของแต่ละโหมด

| กรณี | เส้นทาง | ความหมายและต้นทุนที่ควรตรวจ |
| --- | --- | --- |
| Windows Proxy | แอปที่ใช้ HTTP proxy → Xray HTTP frontend → REALITY หรือ SOCKS ภายในไป Hysteria 2 → VPS → ปลายทาง | ไม่มี Wintun; แอปที่ไม่ใช้ proxy ไม่ได้รับการคุมด้วย App Tunnel |
| Windows TUN / REALITY | แอป → Wintun → sing-box จับคู่กฎ → SOCKS บน loopback → Xray/REALITY → VPS → ปลายทาง | มี TUN และการข้าม process; ไม่มีผลวัด REALITY บนเครื่องนี้ในรอบนี้ |
| Windows TUN / Hysteria 2 | แอป → Wintun → sing-box → SOCKS/Xray → SOCKS/Hysteria → QUIC/UDP → VPS → ปลายทาง | เส้นทางที่วัดจริง มี loopback สองช่วงและกฎใน sing-box; ค่า `mixed` ใช้ gVisor สำหรับ UDP |
| Windows TUN / Bypass เดิม | แอป → Wintun → sing-box `direct` → socket ที่เลือก LAN/Wi‑Fi → ปลายทาง | ไม่ผ่าน Xray/Hysteria หรือ VPS สำหรับ data flow ที่ตรงกฎ แต่ยังเข้า TUN; DNS ที่ถูก VPN จับใช้ DoH ผ่าน tunnel |
| Android TUN | แอป → Android `VpnService` → libbox/gVisor → protocol outbound → VPN gateway; ถ้ากฎเป็น Bypass ให้ libbox ส่งตรงผ่าน socket ที่ผูกกับเครือข่ายจริง | ไม่มีการต่อ SOCKS ข้าม process แบบ Windows ในเส้นทาง VPN หลัก; Bypass เดิมยังเข้า TUN; การระบุแอปอิง UID และอาจเป็น UID ร่วม |
| Windows mesh ที่ระบบมี route อยู่แล้ว | แอป → route ของ Tailscale/ZeroTier → mesh adapter; ทราฟฟิกที่ตัว daemon ส่งออกเป็นอีกเส้นทาง | ตรวจเส้นทาง peer แยกจากการที่ daemon ออก Direct หรือ VPN; IP ของ peer ไม่บอกว่า P2P หรือ relay |
| Android TailSocks ที่เสนอ | แอป → Personal VPN TUN → Local SOCKS5 → TailSocks → P2P/relay → peer | ยังไม่ได้ทำ; มีการส่งต่อในเครื่องเพิ่ม แต่ใช้ช่อง Android VPN เพียงช่องเดียว ถ้าจะให้ทราฟฟิกขาออกของ TailSocks ผ่าน VPN ด้วย ต้องกำหนดกฎแยกและตรวจ recursion |
| Bypass ทั้งแอปที่เสนอ | แอป → เส้นทางปกติของ OS โดยได้รับยกเว้นก่อน TUN | กฎ Site/Advanced ภายใน TUN มองไม่เห็น flow นี้ และต้องบอกสถานะ DNS ของระบบแยกต่างหาก |

อ้างอิงโค้ด: [Windows transport และ health](windows/app/Backend.cs#L454), [Windows TUN policy](windows/app/TunPlan.cs#L28), [Windows route binding](windows/native/personalvpn_route_windows.go#L55), [Android policy และ outbound](android/app/src/main/java/dev/personalvpn/client/Model.kt#L128), [Android TUN และ physical binding](android/app/src/main/java/dev/personalvpn/client/TunnelService.kt#L345), [server bindings](../compose.yaml). เอกสารภายนอก: [sing-box TUN stack](https://sing-box.sagernet.org/configuration/inbound/tun/#stack), [TailSocks Proxy mode](https://bropines.github.io/tailsocks/connection-modes/#proxy), [Tailscale direct/relay](https://tailscale.com/docs/reference/connection-types).

## ผลที่วัดได้บน Windows

ยืนยันว่า TUN มี split routes, runtime ตั้ง `mixed`, MTU 1400 และ fallback `tunnel` โดยต่อ Xray SOCKS ไป Hysteria ภายใน เครื่องใช้ Wi‑Fi; Ethernet ไม่ได้ต่ออยู่ `Find-NetRoute` เลือก PersonalVPN ให้ปลายทางทดสอบ HTTPS/STUN และยังเลือก ZeroTier สำหรับ `10.46.136.125` ส่วน Tailscale รายงาน `Running` และ `Online` ผลเหล่านี้ไม่ยืนยันว่าเครื่อง peer ตอบหรือเชื่อม P2P

ทดสอบสามทางสลับลำดับในแต่ละรอบด้วย [สคริปต์วัดแบบอ่านอย่างเดียว](windows/tools/measure-communication-path.py) แต่ละฝั่งต่อ IP เดียวกันสำหรับแต่ละโปรโตคอล: HTTPS ไป `speed.cloudflare.com` (`162.159.140.220`) และ STUN/UDP ไป `stun.cloudflare.com:3478` (`162.159.207.0`) ทาง Wi‑Fi ตรงใช้ Winsock ผูกเฉพาะ socket ของโปรแกรมทดสอบกับ Wi‑Fi ไม่แก้การตั้งค่าของเครื่อง ผลเก็บสถานะว่า egress ตรงกับ VPS หรือไม่ โดยไม่บันทึก IP สาธารณะของ Wi‑Fi [Cloudflare HTTPS probe](https://github.com/cloudflare/speedtest/blob/main/README.md) · [Cloudflare public STUN](https://developers.cloudflare.com/realtime/turn/#service-address-and-ports) · [RFC 8489](https://datatracker.ietf.org/doc/html/rfc8489)

| ทางที่วัด | HTTPS edge / egress | HTTPS เปิด connection ใหม่¹ | HTTPS ใช้ connection เดิม² | UDP STUN RTT³ | UDP ตอบ |
| --- | --- | ---: | ---: | ---: | ---: |
| Socket ตาม route ปกติ → TUN → Hysteria | Tokyo/NRT / ตรง VPS | 411.3 ms | 139.8 ms | 116.3 ms | 11/12 |
| Socket เข้า Xray SOCKS บน loopback โดยตรง → Hysteria | Tokyo/NRT / ตรง VPS | 367.0 ms | 136.9 ms | 113.0 ms | 12/12 |
| Socket ทดสอบผูก Wi‑Fi | Hong Kong/HKG / คนละ egress กับ VPS | 292.9 ms | 85.7 ms | 59.7 ms | 12/12 |

¹ ค่ากลาง 14 ครั้งต่อทาง โดยรวมการเปิด TCP, TLS และรับผล trace ขนาดเล็ก HTTPS สำเร็จ 14/14 ทุกทาง ² ค่ากลาง 12 ครั้งต่อทางจนได้รับ header สำหรับคำขอ 0 ไบต์ ³ ค่ากลางเฉพาะ STUN reply ที่ยืนยัน transaction และ mapped IP; STUN ไม่มีรหัส edge ให้เปรียบเทียบ หนึ่ง timeout ยังใช้ประเมินอัตราสูญเสียระยะยาวไม่ได้

- **การเปิด connection ใหม่มีช่องให้ตรวจมากที่สุด**: ผ่าน TUN ช้ากว่าเข้า SOCKS ตรงประมาณ 44 ms ในชุดที่ใช้ VPS และ Tokyo edge เดียวกัน แต่เมื่อ reuse connection ค่ากลางต่างราว 3 ms และ STUN ต่าง 3.3 ms การวัดนี้สนับสนุนให้แยกเวลาตั้ง flow ออกจากต้นทุนต่อแพ็กเก็ต ไม่ได้แยกเวลาของตัว Wintun, sniff, app classification, loopback หรือ server ทีละส่วน การจับเวลา Winsock `connect()` ของสองทางยังมีปลาย handshake ต่างกัน จึงไม่ควรใช้ตัวเลข `connect()` อย่างเดียวแทน WAN RTT
- **เส้นทางอินเทอร์เน็ตเปลี่ยนด้วย**: ทาง Wi‑Fi และ VPN ใช้ Cloudflare คนละ edge แม้ DNS จะตรึง IPv4 เดียวกัน ผลต่าง TUN กับ Wi‑Fi จึงรวมเปลี่ยนทางออกและ WAN และไม่ใช่การวัดต้นทุนของกฎ Bypass เดิมเทียบกับ Bypass ใหม่
- **DNS แยกเป็นคนละงานกับ latency ของ flow ที่เปิดอยู่**: TUN ลงทะเบียน DNS เสมือนทั้ง IPv4/IPv6 ผล `getaddrinfo()` ของสามชื่อผ่าน resolver ปัจจุบันรอบแรกอยู่ประมาณ 116–120 ms และรอบซ้ำต่ำกว่า 0.5 ms ไม่ได้ล้าง cache หรือจับ DNS packet จึงเรียกว่า first/repeat lookup ได้ แต่สรุปไม่ได้ว่าเวลานั้นมาจาก DoH เองเท่าไร การตรวจ DNS ของแอปที่ยกเว้นต้องแยกด้วย โดยเฉพาะ Windows ที่หลายแอปใช้ DNS Client service ร่วมกัน
- **ยังไม่มีหลักฐานว่าตันด้าน Mbps**: ดาวน์โหลด 512 KiB สองครั้งต่อทางเพื่อ smoke test เท่านั้น ตัวเลข TUN เปลี่ยนจากราว 8.5 เป็น 22.9 Mbps ระหว่างสองรอบ บอกความแปรปรวนได้ แต่ใช้ตัดสินความจุ VPS หรือรุ่น stack ไม่ได้ ไม่มีการ stress test เครื่องที่ใช้งานอยู่

## จุดที่ควรพัฒนาตามหลักฐาน

1. **แยก Bypass ก่อน capture ออกจาก Direct ผ่าน TUN** ให้เลือกเฉพาะรายแอปแบบ opt-in และไม่ย้ายกฎเดิมอัตโนมัติ Android เริ่มด้วย `addDisallowedApplication()` โดยไม่ยกเว้น package ที่มีนโยบายขัดกับ package อื่นใน UID เดียวกัน และแสดงข้อจำกัดของ Android lockdown บน Windows ใช้ต้นแบบ WFP ใน VM แยกโดยให้ตัวจัดการ driver/การคืนค่าทำงานร่วมกับ lifecycle เดิม ทดสอบก่อนเพิ่มไดรเวอร์ใน installer และไม่ยกเว้น IP/โดเมนทั้งเครื่องเพื่อแทนกฎรายแอป วิธีนี้แก้ความหมายของ Bypass โดยตรงโดยไม่ต้องแก้ VPS [Android per-app VPN](https://developer.android.com/develop/connectivity/vpn#per-app) · [Microsoft WFP bind redirection](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-bind-or-connect-redirection)
2. **แยกการตั้ง flow, DNS และการส่งต่อจริง**: เพิ่ม diagnostic แบบเปิดเมื่อขอทดสอบ ให้ระบุ rule ที่ชนะ, outbound, interface และเวลาก่อนเปิด socket/หลังเปิด socket จากนั้นเทียบ Windows `mixed` กับ `system` ใน VM โดยใช้โปรโตคอล, IP, NIC และโหลดเดียวกัน หากต้นทุนอยู่ที่ flow setup ให้ดู sniff/lookup/การเปิด socket ก่อนแก้ MTU หรือเขียน UDP path ใหม่ ไม่บันทึกทุกแพ็กเก็ตลง session log
3. **แยก capture lifetime จาก transport recovery เท่าที่ทำได้**: Android มี network generation และยกเลิก probe เก่าแล้ว Windows ปัจจุบัน recheck ก่อน restart เมื่อเส้นทางเปลี่ยน แต่หลัง health fail สองครั้งยังผ่าน cleanup/reconnect แยก timing ของ link change, probe, teardown และ verify ให้เห็นเอง อย่าถือว่า persistent adapter แปลว่า capture ต่อเนื่องตลอด retry [Android recovery](android/RECOVERY.md) · [Windows health and recovery](windows/app/Backend.cs#L565)
4. **ทำ Advanced Routing หลังวัด native path ได้**: เก็บ `Direct ผ่าน TUN` สำหรับกฎ App + Site และ Local SOCKS5; เปิด SOCKS5 เฉพาะ peer IP/TCP ก่อน เติม split DNS และทดสอบ UDP ตามความสามารถของ proxy เมื่อ SOCKS5 ล่มให้ fail เฉพาะกฎนั้นและไม่ fallback ไปเครือข่ายตรงอัตโนมัติ ทดสอบ Tailscale/ZeroTier แยก inner route กับ outer transport และทดสอบประกาศ subnet ที่ซ้อนกับ LAN
5. **ค่อยตัดสินใจปรับ VPS จากการวัดพร้อมกัน**: บน endpoint ทดสอบที่ควบคุมได้ วัด upload/download และ latency ภายใต้โหลดทั้งสอง protocol พร้อม CPU/steal, memory, NIC utilization, concurrent flows, Linux UDP buffer drops และ TCP retransmits ก่อนเปลี่ยน QUIC buffers, congestion หรือ spec เครื่อง `ignoreClientBandwidth` ถูกตั้งไว้ใน generator ปัจจุบัน การตั้ง bandwidth สูงบน client จึงไม่ใช่การทดลองแรก หลักฐานสดยืนยัน egress และ traffic ขนาดเล็กเท่านั้น ไม่มี SSH/metrics ฝั่ง VPS ในรอบนี้ [Hysteria server options](https://v2.hysteria.network/docs/advanced/Full-Server-Config/)

## เกณฑ์ทดสอบก่อนใช้ Bypass จริง

- ให้สามแอปที่ระบุได้แน่ชัดต่อไปยัง **endpoint ที่เราควบคุมเดียวกัน** โดยใช้พอร์ตเดียวกัน: แอป Bypass ตอบผ่าน physical NIC และไม่ปรากฏ flow ที่ TUN; แอป Tunnel ผ่าน TUN และออก VPS; แอป Block ไม่ถึง server ทั้งสามต้องทดสอบ TCP/UDP และ IPv4/IPv6 ยืนยันเส้นทางทั้งสองด้านด้วย packet headers ของ fixture และผลบน server เช่น [Windows Pktmon](https://learn.microsoft.com/en-us/windows-server/networking/technologies/pktmon/pktmon) ไม่ใช้เพียง exit IP
- ใช้สี่กรณีเปรียบเทียบ: VPN off, Direct ผ่าน TUN เดิม, Direct ผ่าน TUN ที่ทดลองปรับ และ Bypass ก่อน capture วัด cold/warm, RTT p50/p95, jitter, loss, throughput และ CPU ทั้งช่วง idle และภายใต้โหลดที่ควบคุมได้
- ทำ A/B ตอนเสียบและถอด LAN, เปลี่ยน Wi‑Fi/cellular, สลับ Proxy/TUN, disconnect, crash และใช้ ZeroTier/Tailscale พร้อมกัน ยืนยันว่าไม่มี route/DNS/firewall filter ของแอปตกค้าง และแอปอื่นที่ต่อไปยัง IP เดียวกันยังทำงานตาม Block/Tunnel ตรวจ Android shared UID และ lockdown; ตรวจ Windows x64 และ x86 app บน WOW64 แยกจากการรองรับ Windows 32-bit จริง
- ชุด release 0.9.5 เดิมมีหลักฐาน CI ว่า Windows ผ่าน Direct HTTPS และ SOCKS UDP แยกกัน และ Android API 34/37 ผ่านการทดสอบ emulator 7 รายการต่อ API รวม recovery ของสองโปรโตคอล เหล่านี้ยังไม่พิสูจน์การยกเว้นก่อน capture หรือความเร็วบนมือถือจริง [หลักฐาน 0.9.5](VALIDATION.md)
- ทดสอบ server control plane และ release/deployment fixtures ใน workspace รอบนี้: 18 รายการ ผ่าน 17 ข้าม 1 ที่ใช้ permission แบบ POSIX การทดสอบนี้ใช้งานไฟล์จำลองในเครื่องและไม่ยืนยัน performance ฝั่ง VPS

ผลดิบของ Windows: `.cache/communication-path-survey/windows-live-stun-20261120-160310.json` (ชื่อไฟล์ใช้ UTC) และรอบแรก `.cache/communication-path-survey/windows-live-25691121-000142.json` (ชื่อไฟล์เครื่องใช้ปฏิทินไทย) ผลดิบไม่ถูก commit รอบนี้ไม่ได้รัน Pktmon แบบ elevated ไม่ได้ทดสอบ Windows VM/ไดรเวอร์ใหม่และไม่ได้รันมือถือจริง

รันซ้ำด้วย Python 3.12 จาก root ของ repo โดยใช้ IPv4 ของ adapter ที่ต้องการทดสอบ, public IP ของ VPN profile ที่เชื่อมอยู่ และ port ของ Personal VPN localhost SOCKS:

```powershell
$nic = Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias 'Wi-Fi' |
    Where-Object AddressState -eq 'Preferred' | Select-Object -First 1
$vpnExit = '45.77.179.227' # ใช้ exit IP ของ profile ที่ต้องการทดสอบ
python client/windows/tools/measure-communication-path.py `
    --interface-index $nic.InterfaceIndex --source-address $nic.IPAddress `
    --expected-vpn $vpnExit --socks-port 17891 --samples 12 --stun `
    --output .cache/communication-path-report.json
```
