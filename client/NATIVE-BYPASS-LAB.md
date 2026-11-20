# App Bypass ก่อน TUN และการวัด Windows

อ้างอิงปัญหาและ baseline ใน [Communication Path Survey](COMMUNICATION-PATH-SURVEY.md)

บน Android ให้เปิด **Settings → App Tunnel → เลือกแอป → Choose action**:

- **Direct via TUN** คือ Bypass เดิม: ตรวจและส่งออกโดย VPN core แต่ไม่ผ่าน VPS
- **Bypass VPN** คือให้ Android ยกเว้นทั้งแอปก่อนเข้า VPN; กฎ Site และ SOCKS ภายใน VPN จะไม่เห็น flow ของแอปนี้

กฎเก่าไม่ถูกแปลงอัตโนมัติ การเลือก Bypass VPN เป็นกฎเฉพาะแอปเท่านั้น ไม่ใช่ค่าเริ่มต้น การยกเว้นต้องครอบคลุมทุก package ที่แชร์ UID เดียวกันและใช้ไม่ได้เมื่อ Android เปิด VPN lockdown เมื่อ Apply แล้วรายการยกเว้นเปลี่ยน ระบบสร้าง TUN ใหม่และปิด descriptor เดิมหลัง `establish()` สำเร็จ การสลับโปรโตคอลหรือ Wi‑Fi/cellular ที่ไม่ได้เปลี่ยนรายการยัง reuse TUN ได้ หากดาวน์เกรดไปใช้แอปรุ่นเดิม กฎ Bypass VPN เดิมยังอ่านได้และจะกลายเป็น Block จนกว่าจะเลือกแก้เอง

ชุดทดสอบ emulator ใช้แอปจำลองที่แชร์ UID จริง เช็กว่าระบบ Android มอง network ของแอปนั้นเป็น physical, HTTPS และ UDP เข้าถึง endpoint ที่ Site Tunnel สั่ง Block ได้เมื่อยกเว้นก่อนเข้า TUN และเมื่อเอาการยกเว้นออก Android จับ UID นั้นเข้ากฎอีกครั้ง แยกจากการทดสอบมือถือจริง ลำดับต่อไปบนเครื่องจริงคือวัด IPv4/IPv6 สองโปรโตคอลและสลับ Wi‑Fi/cellular โดยคงกฎ Tunnel/Block ของแอปอื่นไว้

[Android run 44772811400](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/44772811400) ผ่าน build, unit tests, ลายเซ็น และ API 34/37 emulator รายละ 7 tests โดยทั้งสอง log ยืนยัน HTTPS/UDP ของ UID ที่ยกเว้นและคืน capture/Block หลังเอากฎออก โค้ด Android ไม่ได้เปลี่ยนหลัง source `8e77798`; ยังไม่ได้ติดตั้งบนเครื่องจริงหรือเผยแพร่ release ใหม่

Windows ยังใช้ Wintun และ App Direct เดิม ตัววัดเปิดเฉพาะ owned core เมื่อเรียก Windows CI ด้วย `path_timing_lab=true`: จับเวลา resolver, router matching (รวม process lookup และ sniff), physical route lookup, Direct socket dial/UDP socket setup และเวลาตั้ง TUN สำหรับ baseline และ `mixed`/`system` ผลเป็นตัวเลขแบบมีขอบเขต ไม่บันทึกชื่อแอป IP หรือข้อมูลแพ็กเก็ตใน event timing กระบวนการทดสอบจริงใช้ HTTPS และ STUN endpoint เดิมในทุกรอบ; ถ้า UDP สาธารณะถูกบล็อกจะบันทึกผลไม่ตอบโดยไม่ตีความเป็น slowdown ของ stack

[Windows lab 44775178764](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/44775178764) ยืนยัน Direct ผ่าน TUN สำหรับทั้ง `mixed` และ `system` บนทั้งสอง runner เปรียบเทียบเฉพาะภายใน runner เดียวกัน (x64 ไป Cloudflare DFW, x86 บน WOW64 ไป ORD) ค่าด้านล่างเป็นมัธยฐานของ HTTPS 5 ครั้งและ STUN 8 ครั้งต่อทาง ทั้งคู่ตอบ UDP 8/8 ทุกทาง

| Runner | Windows ปกติ HTTPS / UDP | `mixed` HTTPS / UDP | `system` HTTPS / UDP |
| --- | ---: | ---: | ---: |
| x64 | 68.3 / 17.9 ms | 77.0 / 18.2 ms | 87.1 / 18.1 ms |
| x86 app (WOW64) | 29.6 / 1.5 ms | 26.6 / 3.0 ms | 36.2 / 2.7 ms |

HTTPS คือเวลาจากเริ่ม connection ใหม่จนได้ header; UDP คือ round trip ของ STUN โดยไม่ใช้ VPS ข้อมูล route lookup มีมัธยฐาน 0.54–0.58 ms และ policy TCP 0.63–2.82 ms (รวมทราฟฟิกอื่นที่ runner จับได้) ช่วง lease/เตรียม adapter ตอนเริ่มหนึ่ง session ใช้ 2.53–7.58 วินาที จึงเป็นจุดที่ควรแยกวัดต่อสำหรับอาการ reconnect ช้า ไม่ใช่ latency ต่อ packet ผลตัวอย่างนี้ยังไม่สนับสนุนให้เปลี่ยน default จาก `mixed` เพราะ `system` ไม่ได้ลด UDP อย่างชัดเจนและ HTTPS สูงกว่าทั้งสอง runner ยังไม่ได้วัด sustained load, IPv6, throughput หรือพฤติกรรมบน LAN/Wi‑Fi ของผู้ใช้

## Windows native Bypass ในห้องทดสอบแยก

Windows Server 2025 x64 runner ใช้ไดรเวอร์อ้างอิง WFP ของ Mullvad ที่ผ่านการตรวจลายเซ็น พร้อม Wintun และ Hysteria 2 fixture ใน VM เดียวกัน โปรแกรมทดสอบสามตัวใช้ไบนารีเดียวกันคนละ path และเปิด socket ปกติไปที่ IP และพอร์ตเดียวกันต่อโปรโตคอล ก่อนเปิด WFP กฎใน Wintun ตั้งให้ Block ทั้งโปรแกรม Bypass และโปรแกรม Block เพื่อใช้ตรวจว่ามีเพียงโปรแกรมที่ WFP ยกเว้นเท่านั้นที่ออกได้ [ผลทดสอบ 44780593567](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/44780593567)

| ช่วงทดสอบ | Bypass | Tunnel | Block |
| --- | --- | --- | --- |
| เปิด Wintun ก่อนเปิดกฎ WFP | เข้า Wintun และถูกบล็อก | ส่งผ่าน Wintun ได้ | ถูกบล็อก |
| เปิดกฎ WFP | รับส่งผ่าน NIC เดิมได้ | ส่งผ่าน Wintun ได้ | ถูกบล็อก |
| ถอดกฎ WFP | กลับเข้า Wintun และถูกบล็อก | ส่งผ่าน Wintun ได้ | ถูกบล็อก |

ผลตรงกันทั้ง IPv4 TCP/TLS และ UDP/STUN โดยตรวจ source IP จาก socket, ผลตอบกลับปลายทาง และสถานะยกเว้นของ PID จาก kernel ใช้ Wintun core เดิมตลอดการเปรียบเทียบ หลังหยุดทดสอบ adapter ยังใช้ GUID เดิม ไม่มี IP ของ TUN ค้าง ค่า Windows proxy ตรงกับก่อนทดสอบ และถอนบริการไดรเวอร์สำเร็จ รอบแรกพบ `FWP_E_WRONG_SESSION` ตอน callout อ้างถึง sublayer จาก dynamic session อื่น รอบที่ผ่านใช้ sublayer แบบ nonpersistent และลบตามลำดับหลัง reset [ผลรอบแรก](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/44779693437) · [Windows WFP error codes](https://learn.microsoft.com/en-us/windows/win32/fwp/wfp-error-codes)

หลักฐานรอบนี้ครอบคลุม socket และการตอบกลับภายใต้กฎควบคุม การจับแพ็กเก็ตที่ NIC กับ Wintun, shared DNS, IPv6, การสลับ LAN/Wi-Fi, ZeroTier/Tailscale และ cleanup เมื่อ process crash ยังต้องตรวจแยก การทดสอบนี้ยังไม่มีตัวเลข latency สำหรับ native Bypass โค้ดทดสอบอยู่ใน `codex/windows-wfp-lab` และเปิดใช้เฉพาะ build ของ runner
