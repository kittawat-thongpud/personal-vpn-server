# Personal VPN P2P Lab — lab.2

ตัวทดลองแยกสำหรับจับคู่เครื่องและทดสอบข้อมูล P2P ยังไม่มี group IP หรือ SSH/RDP
forwarding ไม่ใช่การอัปเดต VPN หลัก Client 0.7.1 ไม่สร้าง adapter ไม่แก้ route,
system proxy, DNS หรือ VPN ที่กำลังใช้อยู่ โค้ดอยู่ branch `codex/p2p-lab`

## ติดตั้งจุดจับคู่บน VPS ครั้งเดียว

Actions สร้างและทดสอบ image แล้วส่งขึ้น GHCR แบบ **Private** ตาม repo:
`ghcr.io/kittawat-thongpud/personal-vpn-server/p2p-signal:lab.2`
สคริปต์ `P2P-VPS-INSTALL.sh` ที่ได้จาก run เดียวกันจะ pull ด้วย digest ที่แน่นอน
ไม่ build บน VPS หรือเครื่องจีน และไม่มีลิงก์ดาวน์โหลดที่หมดอายุอยู่ในสคริปต์

1. สร้าง GitHub personal access token **classic** สิทธิ์ `read:packages`
   สำหรับดึง image เท่านั้น กรอกที่ช่อง Password บน VPS ไม่ส่ง token ในแชต
2. ใน VPS console รัน `docker login ghcr.io -u kittawat-thongpud` ครั้งแรก
3. ใน PowerShell เครื่อง Windows รัน:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\Personal\Project\VPN\client\tools\prepare-p2p-vps.ps1" -Copy
```

4. วางสคริปต์จาก clipboard ใน VPS console สคริปต์จะติดตั้งใน
   `/opt/personal-vpn-p2p-lab` และรอจน container healthy
   รันซ้ำจะใช้ controller identity และ container เดิมถ้า image ไม่เปลี่ยน
5. เปิด inbound TCP 9443 ใน firewall ของ Vultr และ host firewall ถ้าเปิดใช้อยู่

helper ใช้ GitHub CLI ที่ login บน Windows อยู่แล้วดาวน์โหลดเฉพาะสคริปต์และ
metadata ขนาดเล็ก ตรวจ checksum กับ source ของ run ที่ผ่านทุก gate
เก็บสำเนาสคริปต์ไว้ `.cache/p2p-lab2-vps-install.txt` ใช้สำเนานี้ซ้ำได้
ไม่มี GitHub token ในสคริปต์ ไม่ต้องนำ token ของ Windows ไปไว้บน VPS
Image รุ่นที่ใช้จริงระบุใน `IMAGE-REF.txt`; tag `lab.2` ใช้ชี้รุ่นทดลองปัจจุบัน

ตัวนี้เป็น container แยก ไม่แตะ Compose/containers ของ `/opt/personal-vpn`
และไม่ restart Hysteria/REALITY ใช้ HTTPS บน IP เดิม ใบรับรองและ private key
แยกจาก VPN เดิม แอป pin ใบรับรองตรงกับคำเชิญ ไม่เพิ่มใบรับรองที่เชื่อถือทั้งระบบ

VPS console จะแสดงคำเชิญ owner แบบใช้ได้ครั้งเดียว เริ่มด้วย `pvp2:`
คัดลอกจาก console ไปวางใน Windows P2P Lab ครั้งแรกเท่านั้น อย่าส่งคำเชิญนี้ในแชต
ถ้าหมดอายุ รันในโฟลเดอร์ sidecar:

```bash
docker compose -f compose.signal.yaml exec signal python /app/server.py owner-invite
```

## จับคู่ Windows ↔ Redmi

1. Windows: แตก ZIP x64 หรือ x86 แล้วเปิด `PersonalVPNP2PLab.exe`
   ต้องมี .NET Framework 4.8 และ WebView2 Runtime (Windows 11 เครื่องนี้มีอยู่แล้ว)
   วางคำเชิญ owner จาก VPS แล้วกดตั้งค่าจุดจับคู่ครั้งแรก
2. Android 14+: ติดตั้ง `PersonalVPN-P2P-Lab.apk` เป็นแอปแยกจาก Personal VPN หลัก
   ต้องถอนเฉพาะ P2P Lab lab.1 เดิมหนึ่งครั้ง เพราะรุ่นแรกใช้ signing key ชั่วคราว
   lab.2 ใช้ signing key เฉพาะที่คงเดิมสำหรับอัปเดตรุ่นทดลองถัดไป
3. Windows กดสร้างห้อง จะได้รหัส 8 หลักและ QR ให้ Redmi กดสแกน QR
   QR จะตั้งค่าจุดจับคู่บนมือถือและเข้าห้องให้อัตโนมัติ ไม่ต้องส่ง offer/answer ไปมา
   อุปกรณ์ที่ตั้งค่าจุดจับคู่ไว้แล้วสามารถกรอกรหัสห้องได้ด้วย
4. รอจับคู่กับอีกเครื่องแล้ว จากนั้นดูสถานะเส้นทาง P2P แยกต่างหาก
   เมื่อ P2P ต่อได้ ให้เปรียบเทียบรหัสยืนยันให้ตรงกันและยืนยันบนทั้งสองเครื่อง
5. กดส่ง 1 MiB + ตรวจ SHA-256 บนแต่ละเครื่อง แล้ว Export ผลจาก Logs ทั้งคู่
   เปิด VPN สำรองที่ใช้คุยกันไว้ได้ ไม่ต้องเปลี่ยน route หรือปิด firewall เพื่อทดลอง

เลือก LAN สำหรับเครือข่ายที่อุปกรณ์คุยกันได้ หรือ Internet / STUN เพื่อค้นหา
endpoint เพิ่ม STUN ค่าเริ่มต้นคือ `stun:stun.cloudflare.com:3478` ไม่ใช่ relay
การจับคู่ผ่าน HTTPS อาจสำเร็จแม้ Wi-Fi กัน client-to-client หรือ VPN จับ UDP อยู่
ดังนั้นจับคู่สำเร็จไม่ใช่หลักฐานว่า direct P2P สำเร็จ

## ขอบเขตและการวินิจฉัย

- รายงานเก็บจำนวน host/srflx/prflx, mDNS, IPv4/IPv6, สถานะ ICE และ candidate pairs,
  จำนวน STUN requests/responses, error code และข้อมูลเครือข่ายแบบไม่ใส่ IP ปลายทาง
  ไม่เก็บ SDP, ICE password, device token, คำเชิญหรือเนื้อหาข้อมูลทดสอบใน Export
- Logs อยู่ใน RAM สูงสุด 1,000 รายการ/1 MiB, Clear ได้ ปิดเปิดแอปใหม่เริ่มใหม่
- Credentials จุดจับคู่เก็บด้วย Android Keystore/Windows DPAPI เฉพาะแอป Lab
  QR ใช้ ticket ชั่วคราวครั้งเดียว ไม่ใส่ credential VPN หลักหรือ token ระยะยาว
- ไม่มี TURN หรือ data relay บน VPS ข้อมูลทดสอบใช้ DTLS ระหว่าง clients
  Controller เห็น metadata ICE/SDP สำหรับจับคู่ แต่ไม่รับส่ง payload ทดสอบแทนเรา
- no TURN ไม่ยืนยันการข้าม VPN: WebRTC อาจเลือก VPN/ZeroTier/Tailscale interface
  แอป Android Lab ไม่เป็น VpnService และไม่สามารถ protect socket ใต้ VPN อื่นเอง
  รายงานจึงคง `physicalBypassVerified: false` จนกว่าจะมีหลักฐานเส้นทางจริง
- ถ้าจุดจับคู่หยุด ช่องทาง P2P ที่ต่อได้ต้องไม่ถูกปิดตาม ห้องหมดอายุใน 10 นาที
  และหายเมื่อ controller restart ส่วน credential อุปกรณ์ยังเก็บไว้
- การ reconnect/hand-over แบบ production, group IP, app forwarding,
  persistent peer identity และการรวมเข้า VpnService เดียวยังเป็นงานขั้นถัดไป
- HTML เดี่ยวคงไว้ตรวจ manual pairing/diagnostics โดยไม่มี native host
  การจับคู่อัตโนมัติพร้อม pin ใช้ตัว Windows `.exe` และ Android APK

## Sidecar budget and rollback

Limits: 0.20 CPU, 64 MiB RAM, 64 PIDs, 2 MiB logs; 16 rooms / 2 peers per room,
128 control messages / 256 KiB per room, 10-minute pairing lifetime; one active
pairing room per owner device. Creating a new room replaces that device's previous
signaling lease, without closing an established peer channel. No arbitrary
proxy destination or Internet forwarding. These caps do not establish measured
VPS peak-load headroom; inspect real load alongside the working VPN after deployment.

Rollback in `/opt/personal-vpn-p2p-lab` only:

```bash
docker compose -f compose.signal.yaml down
```

Keep `signal-state` to retain controller identity/devices. Do not change
`/opt/personal-vpn`, its runtime or existing containers.

All APK/EXE/image builds and packaging run in Actions. This is an experimental
Actions artifact. No stable release, Latest marker or snapshot is replaced.
See `client/P2P-LAB-VALIDATION.md` for exact CI and real-device limits.

Primary references: [WebRTC](https://www.w3.org/TR/webrtc/),
[stats](https://www.w3.org/TR/webrtc-stats/),
[Android VPN](https://developer.android.com/develop/connectivity/vpn),
[local HTTPS assets](https://developer.android.com/develop/ui/views/layout/webapps/load-local-content),
[WebView2](https://learn.microsoft.com/microsoft-edge/webview2/),
[ZXing Android](https://github.com/journeyapps/zxing-android-embedded),
[GHCR private authentication](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
