# Personal VPN server module

ชุดสร้าง Docker/OCI images สำหรับ VPS Linux ใช้คนเดียวหลายอุปกรณ์:

| ส่วน | หน้าที่ |
| --- | --- |
| Xray v26.10.31 | VLESS + REALITY + Vision, TCP 443 เป็นตัวหลัก |
| Hysteria v2.13.0 | QUIC/UDP 443 เป็นทางเลือก เปิดด้วย Compose profile `hy2` |
| `vpnctl.py` | สร้าง key, เพิ่ม/ถอนสิทธิ์อุปกรณ์ และ export config แบบ offline |

ไม่มี web panel, database, subscription server หรือ management daemon ที่เปิดออกอินเทอร์เน็ต ใช้ upstream core โดยตรง ไม่สร้าง encryption/protocol เอง รุ่นและ SHA256 ของ binary ถูกตรึงไว้ใน Dockerfile และสร้างได้ทั้ง amd64/arm64

## ติดตั้งจาก Private GitHub Release

สำหรับ VPS แนะนำใช้ [DEPLOY.md](DEPLOY.md): GitHub Actions build container images แล้ว VPS ดาวน์โหลด Release และใช้ `docker load` ได้เลย ไม่ต้องอัปโหลดไฟล์จากเครื่องในจีนและไม่ต้อง build image บน VPS

Repo เป้าหมายคือ `kittawat-thongpud/personal-vpn-server` แบบ Private การดาวน์โหลด asset จึงต้องยืนยันตัวตน GitHub ด้วย token ที่ให้สิทธิ์อ่านเฉพาะ repo นี้ ส่วน VPN key/password สร้างบน VPS และไม่อยู่ใน Git/Release/image

Workflow `.github/workflows/release.yml` ทำงานเมื่อ push `main` หรือสั่งเอง: ตรวจ unit tests และ native Linux tunnel, build/test container บน Linux แล้ว publish GHCR images กับ GitHub Release เมื่อผ่านทั้งหมด การเพิ่มเวอร์ชันทำผ่านไฟล์ `VERSION` และไม่เขียนทับ Release ที่ออกแล้ว ดู [PUBLISH.md](PUBLISH.md)

Release มี deployment bundle, Docker image archive สำหรับ Linux amd64 และ `SHA256SUMS`; มี Compose สำหรับดึง GHCR ด้วย digest แยกไว้ด้วย GHCR build รองรับ amd64/arm64 แต่ container smoke test ใน CI ตรวจ amd64

ขั้นตอนด้านล่างเป็นทางเลือกสำหรับ build จาก source เอง

## ขอบเขตและข้อจำกัด

- เป็น proxy/tunnel server; การทำ full-device VPN, TUN, DNS policy, kill switch และเลือก app/IP ที่ bypass ต้องจัดการที่ client ซึ่งอยู่นอกงานระยะนี้
- ใช้หลาย TCP connection หรือหลาย QUIC stream พร้อมกันได้ ไม่ใช่การรวม bandwidth หลาย WAN และไม่เพิ่มเพดาน bandwidth/โควตาของ VPS
- REALITY เป็น camouflage ไม่ใช่การรับประกันการซ่อนตัว ทุกวิธีอาจถูกวิเคราะห์/บล็อกได้ ต้องทดสอบจาก ISP/Wi-Fi/มือถือที่ใช้ในจีนจริง
- สองโปรโตคอลบน IP เดียวช่วยกรณี TCP หรือ UDP มีปัญหา แต่ถ้า IP ถูกบล็อกทั้งคู่จะได้รับผลกระทบ ไม่มี failover อัตโนมัติใน server นี้
- Compose เริ่มต้นใช้ IPv4 + Docker bridge เพื่อให้ deploy ง่าย ไม่เปิด TUN หรือ `NET_ADMIN`; ไม่มี IPv6 egress ในค่าเริ่มต้น อย่าเปิดใช้ IPv6 ฝั่ง client โดยไม่มีนโยบาย tunnel/leak ที่ตรวจแล้ว
- Image ไม่มี credential; `.state/`, `runtime/`, `exports/` ถูกสร้างแยกใน VPS ห้ามส่งให้คนอื่นหรือ commit

## ติดตั้งบน VPS

แนะนำเริ่มด้วย Ubuntu 24.04 LTS, public IPv4 และ VPS ที่มีเส้นทางไปเครือข่ายที่ใช้จริงดี เลือก location จากการวัด latency/loss/throughput ไม่สามารถยืนยันว่า Tokyo หรือผู้ให้บริการใดดีที่สุดโดยไม่ได้วัด

### 1. เตรียมเครื่องและอัปโหลด

ติดตั้ง Docker Engine และ Compose plugin ตาม [คู่มือ Docker สำหรับ Ubuntu](https://docs.docker.com/engine/install/ubuntu/) หรือเลือก VPS image ที่ติดตั้ง Docker ไว้แล้ว จากนั้นตรวจ:

```sh
sudo docker version
sudo docker compose version
sudo apt-get update
sudo apt-get install -y python3-venv
```

อัปโหลด `vpn-server-source.tar.gz` ไป VPS แล้วแตกในโฟลเดอร์ใหม่:

```sh
sudo mkdir -p /opt/personal-vpn
sudo tar -xzf vpn-server-source.tar.gz -C /opt/personal-vpn
cd /opt/personal-vpn
sudo python3 -m venv .venv
sudo .venv/bin/python -m pip install -r requirements.txt
```

Python ใช้เฉพาะตอนจัดการ config; traffic วิ่งใน Go binary สองตัว ไม่ผ่าน Python

### 2. สร้าง server config

แทน `YOUR_VPS_IP`, `YOUR_TARGET_HOST` และ `YOUR_TARGET_SNI` ก่อนรัน:

```sh
sudo .venv/bin/python vpnctl.py init \
  --endpoint YOUR_VPS_IP \
  --reality-target YOUR_TARGET_HOST:443 \
  --reality-sni YOUR_TARGET_SNI

sudo .venv/bin/python vpnctl.py add-device android
sudo .venv/bin/python vpnctl.py add-device windows
sudo .venv/bin/python vpnctl.py add-device linux
```

REALITY target คือเว็บ TLS ที่ VPS เชื่อมถึงและรับ SNI ที่ระบุ ไม่ใช่ชื่อ VPS ของเรา เลือกเว็บที่รองรับ TLS 1.3 และ HTTP/2 และตรวจจาก VPS ก่อนใช้งาน ดู [เกณฑ์ของ REALITY](https://xtls.github.io/en/config/transports/reality.html) การใช้ target หลัง CDN อาจทำให้ fallback ที่ไม่ได้ authenticate กลายเป็น port forwarder ที่ถูกนำไปใช้ได้ จึงไม่มี target กลางที่ hardcode ให้ทุกเครื่อง

ค่าเริ่มต้น Hysteria ใช้ ECDSA certificate ที่สร้างเอง + exact SHA256 pin เพื่อเริ่มได้โดยไม่มีโดเมน Native Hysteria client ต้องใช้ `insecure:true` **พร้อม** `pinSHA256`; โปรแกรมนี้ export ทั้งคู่ ไม่มีโหมดปล่อย TLS โดยไม่ตรวจ pin สำหรับ pinned deployment ต้องตรวจว่า client app เก็บและบังคับ pin จริงก่อน import link อย่าเปิด allow-insecure อย่างเดียว ควรใช้ ACME ด้านล่างสำหรับ GUI client หลายแพลตฟอร์ม

### 3. สร้าง images และเริ่ม

```sh
sudo docker compose --profile hy2 build --pull
sudo .venv/bin/python vpnctl.py doctor
sudo docker compose --profile hy2 config --quiet
sudo docker compose run --rm xray run -test -config /etc/vpn/xray.json
sudo docker compose --profile hy2 up -d
sudo docker compose --profile hy2 ps
```

ถ้าเริ่มเฉพาะ REALITY ใช้ `sudo docker compose up -d xray` ยังไม่จำเป็นต้องเปิด UDP 443/TCP 80

ตรวจ target จาก binary ที่ build แล้วได้ด้วย:

```sh
sudo docker compose run --rm xray tls ping YOUR_TARGET_HOST:443
```

`tls ping` ช่วยตรวจปลายทาง TLS แต่ไม่ใช่ผลทดสอบการผ่าน Great Firewall

ตั้ง firewall/security group ของผู้ให้บริการให้รับ TCP 443 และ UDP 443; จำกัด SSH เฉพาะ IP ที่ใช้บริหาร ถ้าใช้ ACME HTTP challenge ให้รับ TCP 80 ด้วย ทั้งใน cloud firewall และกฎของ host ไม่มีสคริปต์เปลี่ยน firewall ให้อัตโนมัติ [Docker published ports อาจข้ามกฎ UFW](https://docs.docker.com/engine/network/packet-filtering-firewalls/); ใช้ cloud firewall และ Docker firewall chain ให้ตรง backend ที่ใช้

Compose publish IPv4 TCP443/UDP443 และ TCP80 ของ Hysteria เท่านั้น อย่าเปิด HTTPS masquerade TCP443 ใน Hysteria เพราะชนกับ Xray หากเปลี่ยน port ใน CLI ต้องแก้ port mapping ใน Compose ให้ตรงกันด้วย

### 4. รับไฟล์สำหรับ client

```sh
sudo .venv/bin/python vpnctl.py export android
sudo .venv/bin/python vpnctl.py list-devices
```

ไฟล์อยู่ใน `exports/android/`:

- `links.txt`: VLESS URI และ Hysteria 2 URI มีรหัสผ่านอยู่ในลิงก์
- `xray.json`: native Xray client, SOCKS5 ที่ `127.0.0.1:10808`
- `hysteria.json`: native Hysteria client, SOCKS5 ที่ `127.0.0.1:10809`

ส่งไฟล์ผ่าน SSH/SCP ที่เชื่อถือได้ ไม่เปิด exports เป็นเว็บสาธารณะ ไม่มี app UI/TUN ในชุดนี้ และยังไม่ได้รับรองการ import กับ Hiddify หรือ GUI client ใด

Native smoke test หลังนำ config ไปเครื่อง client (ติดตั้ง core รุ่นที่รองรับ):

```sh
xray run -config xray.json
# อีก terminal: curl --proxy socks5h://127.0.0.1:10808 https://example.com/

hysteria client -c hysteria.json
# อีก terminal: curl --proxy socks5h://127.0.0.1:10809 https://example.com/
```

## Hysteria กับโดเมนจริงและ ACME

ตั้ง DNS A ของ `vpn.your-domain.com` ให้ชี้ public IPv4 ของ VPS โดยตรง ไม่ผ่าน HTTP CDN proxy; อย่าใส่ AAAA หากยังไม่ได้ทำ IPv6 ให้ครบ เปิด TCP80 ให้เข้าถึงจากอินเทอร์เน็ต และตั้ง:

```sh
sudo .venv/bin/python vpnctl.py configure \
  --hy2-mode acme \
  --hy2-domain vpn.your-domain.com \
  --acme-email your-email@example.com
sudo docker compose --profile hy2 up -d
sudo docker compose --profile hy2 restart xray hysteria
```

ACME ใช้ HTTP challenge ที่ TCP80 เพื่อไม่ชน REALITY TCP443 เก็บ account/certificate ใน named volume `personal-vpn_hysteria-state` และต่ออายุอัตโนมัติ อย่าใช้ `down -v` ถ้าต้องการเก็บ volume นี้ Import profile ใหม่หลังเปลี่ยน TLS mode

ACME จำเป็นต้องติดต่อ CA/DNS จาก VPS และได้รับ challenge จากอินเทอร์เน็ต การรัน `doctor` ไม่ตรวจสิ่งเหล่านี้ หาก certificate ไม่ออกให้ตรวจ logs/DNS/port80

## หลายอุปกรณ์และการถอนสิทธิ์

ทุกอุปกรณ์มี UUID, short ID และ Hysteria password คนละชุด ไม่แชร์ profile เดียวกัน ถอนสิทธิ์เครื่องหาย:

```sh
sudo .venv/bin/python vpnctl.py revoke-device android
sudo docker compose --profile hy2 restart xray hysteria
```

CLI เปลี่ยน state/config ในดิสก์เท่านั้น **การถอนสิทธิ์จะมีผลหลัง restart ทั้งสอง service** ซึ่งตัด session ที่ค้างอยู่ด้วย ชื่อที่ถอนแล้วไม่สามารถใช้ซ้ำ ให้สร้าง `android-new` เพื่อออก credential ใหม่

เพิ่ม/เปลี่ยน config แล้วต้อง restart เช่นกัน การเปลี่ยน endpoint, SNI, obfuscation หรือ TLS mode ต้องนำ profile ใหม่ไปทุกเครื่องที่ใช้งาน ส่วนการเพิ่มเครื่องใหม่ไม่เปลี่ยน credential เครื่องเก่า

```sh
sudo .venv/bin/python vpnctl.py configure --endpoint NEW_VPS_IP
sudo .venv/bin/python vpnctl.py render
sudo docker compose --profile hy2 restart xray hysteria
```

Pinned certificate อายุ 397 วัน `doctor` แจ้งเมื่อเหลือน้อยกว่า 30 วัน ต่ออายุโดย `rotate-certificate` แล้วนำ profile ใหม่ไปทุกเครื่อง; pin จะเปลี่ยน ห้ามลบ key/cert แล้วหวังว่า pin เดิมยังใช้ได้

## Parallel throughput และการพราง

- Vision ใช้ TCP connection อิสระและปิด client TCP mux ไว้ ลดการผูกหลาย flow ลง TCP เส้นเดียว
- Hysteria ใช้ QUIC streams และ BBR เป็นค่าเริ่มต้น ไม่ตั้ง bandwidth ตัวเลขสมมุติเพื่อเปิด Brutal และตั้ง `ignoreClientBandwidth:true` เพื่อให้ทุกเครื่องใช้ congestion control นี้
- ไม่ขยาย QUIC receive windows หรือจำนวน stream เองก่อนวัดจริง มี nofile limit และ log rotation ใน Compose
- Host TCP BBR/fq และ UDP buffer maxima เป็นตัวเลือกใน `scripts/host-tuning.sh`; TCP BBR ของ kernel ไม่ใช่ QUIC BBR ของ Hysteria การเพิ่ม socket buffer อย่างเดียวไม่รับประกันว่าจะเร็วขึ้น
- Hysteria ปกติใช้ HTTP/3-like traffic; ถ้าตั้ง `configure --obfs salamander` จะใช้ packet obfuscation แทน และไม่เป็น HTTP/3 server มาตรฐานอีกต่อไป ไม่ควรอ้างทั้งสองอย่างพร้อมกัน ค่าเริ่มต้น `none` ไม่เปิด experimental mimic/port hopping
- การทำ app/IP bypass ที่ช่วยลด traffic ต้องทำก่อนเข้า tunnel ที่ client; server นี้ไม่ได้ทราบ process/app ของเครื่องต้นทาง

## ดูแล อัปเดต และสำรอง

```sh
sudo docker compose --profile hy2 logs --tail 80
sudo .venv/bin/python vpnctl.py doctor
sudo docker compose --profile hy2 down
```

Logs อาจมี IP/error ที่เป็นข้อมูลส่วนตัว แม้ Xray access log ปิดแล้ว ไม่ส่ง log ดิบเผยแพร่โดยไม่ตรวจ

สำรอง `.state/server.json`, `runtime/` และ ACME named volume ด้วยวิธีที่เข้ารหัสและจำกัดสิทธิ์ `.state` มี private key และ password ของทุกเครื่อง ไฟล์ POSIX `.state/exports` ใช้ 700/600; runtime ใช้ 750/640 และ group 10001 เพื่อให้ container อ่านได้ รัน CLI ด้วย sudo บน Linux; Windows ไม่บังคับ POSIX modes

อัปเดตโดยแก้ version+SHA256 ใน Dockerfile พร้อมกัน ตรวจ official release แล้ว build/test ก่อน deploy เก็บ image รุ่นเดิมไว้ rollback และสำรอง state ก่อนเปลี่ยนรุ่น CLI ห้ามใช้ `latest` แบบเปลี่ยนเองโดยไม่ตรวจ ใช้ `docker save`/`docker load` ได้หากต้องย้าย image ไป VPS ที่ download ไม่สะดวก

## การตรวจสอบในโปรเจกต์นี้

```sh
python -m unittest discover -s tests -p 'test_*.py' -v
python tests/integration.py --download
```

บน Linux ให้ใช้ sudo กับ Python ใน venv เนื่องจาก CLI ต้องจัด group ของ runtime files Integration เลือก official binaries ตาม platform และตรวจ SHA256; รอบที่ตรวจในงานนี้ใช้ Windows และ native tunnel บน loopback ไม่ใช่ container หรือเส้นทางจากจีน ไฟล์ผลทดสอบและข้อจำกัดอยู่ใน `VALIDATION.md` สำหรับการใช้งานจริงยังต้องทดสอบ Docker Linux, ACME และ load จากเครือข่ายจริงตามรายการนั้น

แหล่งอ้างอิง: [REALITY](https://xtls.github.io/en/config/transports/reality.html), [Freedom final rules](https://xtls.github.io/en/config/outbounds/freedom.html), [Hysteria server](https://v2.hysteria.network/docs/advanced/Full-Server-Config/), [Hysteria client](https://v2.hysteria.network/docs/advanced/Full-Client-Config/), [URI scheme](https://v2.hysteria.network/docs/developers/URI-Scheme/), [Xray release](https://github.com/XTLS/Xray-core/releases/tag/v26.10.31), [Hysteria release](https://github.com/HyNetworks/hysteria/releases/tag/app/v2.13.0)
