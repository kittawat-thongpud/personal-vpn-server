# App Bypass ก่อน TUN และการวัด Windows

อ้างอิงปัญหาและ baseline ใน [Communication Path Survey](COMMUNICATION-PATH-SURVEY.md)

บน Android ให้เปิด **Settings → App Tunnel → เลือกแอป → Choose action**:

- **Direct via TUN** คือ Bypass เดิม: ตรวจและส่งออกโดย VPN core แต่ไม่ผ่าน VPS
- **Bypass VPN** คือให้ Android ยกเว้นทั้งแอปก่อนเข้า VPN; กฎ Site และ SOCKS ภายใน VPN จะไม่เห็น flow ของแอปนี้

กฎเก่าไม่ถูกแปลงอัตโนมัติ การเลือก Bypass VPN เป็นกฎเฉพาะแอปเท่านั้น ไม่ใช่ค่าเริ่มต้น การยกเว้นต้องครอบคลุมทุก package ที่แชร์ UID เดียวกันและใช้ไม่ได้เมื่อ Android เปิด VPN lockdown เมื่อ Apply แล้วรายการยกเว้นเปลี่ยน ระบบสร้าง TUN ใหม่และปิด descriptor เดิมหลัง `establish()` สำเร็จ การสลับโปรโตคอลหรือ Wi‑Fi/cellular ที่ไม่ได้เปลี่ยนรายการยัง reuse TUN ได้ หากดาวน์เกรดไปใช้แอปรุ่นเดิม กฎ Bypass VPN เดิมยังอ่านได้และจะกลายเป็น Block จนกว่าจะเลือกแก้เอง

ชุดทดสอบ emulator ใช้แอปจำลองที่แชร์ UID จริง เช็กว่าระบบ Android มอง network ของแอปนั้นเป็น physical, HTTPS และ UDP เข้าถึง endpoint ที่ Site Tunnel สั่ง Block ได้เมื่อยกเว้นก่อนเข้า TUN และเมื่อเอาการยกเว้นออก Android จับ UID นั้นเข้ากฎอีกครั้ง แยกจากการทดสอบมือถือจริง ลำดับต่อไปบนเครื่องจริงคือวัด IPv4/IPv6 สองโปรโตคอลและสลับ Wi‑Fi/cellular โดยคงกฎ Tunnel/Block ของแอปอื่นไว้

Windows ยังใช้ Wintun และ App Direct เดิม ตัววัดเปิดเฉพาะ owned core ใน Windows CI: จับเวลา resolver, router matching (รวม process lookup และ sniff), physical route lookup, Direct socket dial/UDP socket setup และเวลาตั้ง TUN สำหรับ baseline และ `mixed`/`system` ผลเป็นตัวเลขแบบมีขอบเขต ไม่บันทึกชื่อแอป IP หรือข้อมูลแพ็กเก็ตใน event timing กระบวนการทดสอบจริงใช้ HTTPS และ STUN endpoint เดิมในทุกรอบ; ถ้า UDP สาธารณะถูกบล็อกจะบันทึกผลไม่ตอบโดยไม่ตีความเป็น slowdown ของ stack

Windows native Bypass เป็นขั้นถัดไปของห้องทดสอบแยก: deploy ไดรเวอร์ WFP ที่ได้รับอนุญาตให้โหลดใน VM, scope ตาม app identity และคงการเลือกเส้นทาง LAN/Wi‑Fi ตามปลายทาง, หลีกเลี่ยงการยกเว้นตาม IP ทั้งเครื่อง ทดสอบ Bypass/Tunnel/Block จากสามโปรเซสสู่ IP/พอร์ตเดียวกันพร้อมกัน จับ packet headers ทั้ง Wintun และ NIC, ทดสอบ shared system DNS แยก และทดสอบ rollback/crash, IPv4/IPv6, Windows x64/x86 app และ mesh adapters การทดสอบ stack หรือ egress IP อย่างเดียวไม่พิสูจน์การยกเว้น Wintun โดยไม่รับแพ็กเก็ต
