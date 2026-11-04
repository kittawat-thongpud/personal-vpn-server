import { PeerLab, LIMIT } from './core.mjs';
import { NativeBridge, Rooms } from './rooms.mjs';
const $ = id => document.getElementById(id);
const states = { idle:'พร้อมจับคู่', gathering:'กำลังค้นหาเส้นทาง…', waiting:'รออีกเครื่อง', connecting:'กำลังเชื่อมต่อ P2P…', connected:'เชื่อมต่อ P2P แล้ว', degraded:'สัญญาณขาด กำลังรอ…', failed:'เส้นทาง P2P ขัดข้อง · ดู Logs', closed:'หยุดแล้ว' };
let receiver = false, busy = false, revision = -1, shownCode = '';
const lab = new PeerLab(render); window.lab = lab;
const bridge = new NativeBridge();let rooms,qrValue='';window.pvpRooms=null;
const bytes = n => { const units = ['B','KiB','MiB']; let i = 0; while (n >= 1024 && i < 2) { n /= 1024; i++; } return `${n.toFixed(i ? 1 : 0)} ${units[i]}`; };
function banner(text = '') { $('error').textContent = text; $('error').hidden = !text; }
async function action(fn) {
  if (busy) return; busy = true; banner(); render();
  try { await fn(); } catch (e) { banner(`ไม่สำเร็จ: ${e.code || e.name || 'operation.failed'}`); }
  finally { busy = false; render(); }
}
function stun() { return $('network').value === 'stun' ? $('stun').value : ''; }
function page(logs) { $('connect-page').hidden = logs; $('logs-page').hidden = !logs; $('connect-nav').toggleAttribute('aria-current', !logs); $('logs-nav').toggleAttribute('aria-current', logs); }
function download(text, name) {
  if (window.P2PLab) { window.P2PLab.save(text, name); return; }
  const url = URL.createObjectURL(new Blob([text], {type:'application/json'}));
  const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function render() {
  $('state').textContent = states[lab.phase] || lab.phase;
  $('path').textContent = lab.path ? `${lab.path.local} ↔ ${lab.path.remote} · ${lab.path.protocol} · no TURN` : 'ยังไม่ยืนยันเส้นทาง';
  const connected = lab.phase === 'connected' || lab.phase === 'degraded';
  $('setup').hidden = !!lab.pc; $('exchange').hidden = connected || (!lab.pc && !receiver);
  if(rooms){
    $('setup').hidden=true;$('exchange').hidden=true;$('automatic').hidden=false;
    $('server-setup').hidden=rooms.status.configured;$('room-controls').hidden=!rooms.status.configured;
    $('room-status').textContent=rooms.peerJoined?'จับคู่กับอีกเครื่องแล้ว':({setup:'ตั้งค่าเซิร์ฟเวอร์ครั้งแรก',ready:'พร้อมสร้างหรือเข้าห้อง',waiting_peer:'รออีกเครื่องเข้าห้อง',unavailable:'ติดต่อเซิร์ฟเวอร์จับคู่ไม่ได้'}[rooms.state]||'พร้อมจับคู่');
    $('room-code').textContent=rooms.room?.code||'—';$('room-view').hidden=!rooms.room;
    $('scan-room').hidden=!rooms.status.scanSupported;
    $('native-warning').hidden=!rooms.status.network?.vpnActive;
    const invite=rooms.room?.invite||'';
    if(invite!==qrValue){qrValue=invite;$('room-qr').hidden=true;if(invite)void bridge.call('qr',{value:invite}).then(png=>{if(qrValue===invite){$('room-qr').src=png;$('room-qr').hidden=false;}}).catch(()=>banner('qr.unavailable'));}
  }
  $('out-box').hidden = !lab.signalText; $('outgoing').value = lab.signalText;
  $('in-box').hidden = !!lab.remote;
  $('in-label').textContent = receiver ? 'วางคำเชิญจากอีกเครื่อง' : 'วางคำตอบที่อีกเครื่องส่งกลับมา';
  $('out-label').textContent = receiver ? 'ส่งคำตอบนี้กลับไปให้เครื่องที่สร้างคำเชิญ' : 'ส่งคำเชิญนี้ให้อีกเครื่อง';
  $('apply').textContent = receiver ? 'สร้างคำตอบ' : 'เชื่อมต่อด้วยคำตอบ';
  $('offer').disabled = busy; $('join').disabled = busy; $('apply').disabled = busy;
  $('identity').hidden = !lab.code || !connected;
  $('pair-code').textContent = lab.code;
  if (shownCode !== lab.code) { shownCode = lab.code; $('confirm').disabled = false; }
  $('confirm').hidden = lab.confirmed;
  $('confirm').disabled = !lab.path?.noTurn;
  $('ready').textContent = lab.confirmed ? (lab.remoteReady ? 'ทั้งสองเครื่องยืนยันแล้ว พร้อมทดสอบ' : 'รออีกเครื่องยืนยันรหัส') : '';
  $('transfer').disabled = !lab.canTest() || lab.sending;
  $('up').textContent = `${bytes(lab.upRate)}/s`; $('down').textContent = `${bytes(lab.downRate)}/s`;
  $('up-total').textContent = bytes(lab.totalUp); $('down-total').textContent = bytes(lab.totalDown);
  $('rtt').textContent = lab.rtt === null ? '—' : `${Math.round(lab.rtt)} ms`;
  const result = lab.results.at(-1); $('result').textContent = result ? `SHA-256 OK · ${result.goodputMbps} Mbps` : 'ยังไม่ทดสอบ';
  const max = Math.max(1, ...lab.history.flat());
  for (const [i,id] of ['up-graph','down-graph'].entries()) $(id).setAttribute('points', lab.history.map((r,k) => `${k * 300 / 59},${47 - r[i] * 45 / max}`).join(' '));
  if (revision !== lab.log.revision) {
    revision = lab.log.revision; const frag = document.createDocumentFragment();
    for (const row of lab.log.rows) {
      const li = document.createElement('li'); li.textContent = `${row.time.slice(11,19)}  ${row.level}  ${row.code}`;
      if (row.detail) { const detail = document.createElement('span'); detail.textContent = row.detail; li.append(detail); } frag.append(li);
    }
    const nearBottom = $('logs').scrollTop + $('logs').clientHeight >= $('logs').scrollHeight - 30;
    $('logs').replaceChildren(frag); if (nearBottom) $('logs').scrollTop = $('logs').scrollHeight;
  }
}
$('network').onchange = () => { $('stun-box').hidden = $('network').value !== 'stun'; };
$('offer').onclick = () => action(async () => { receiver = false; $('incoming').value = ''; await lab.offer(stun()); });
$('join').onclick = () => { receiver = true; $('incoming').value = ''; $('exchange').hidden = false; render(); $('incoming').focus(); };
$('apply').onclick = () => action(async () => {
  if (receiver) await lab.answer($('incoming').value, stun()); else await lab.accept($('incoming').value);
  $('incoming').value = '';
});
$('copy').onclick = () => action(async () => {
  if (window.P2PLab) window.P2PLab.copy(lab.signalText);
  else { try { await navigator.clipboard.writeText(lab.signalText); } catch { $('outgoing').select(); banner('เลือกข้อความไว้แล้ว กรุณาคัดลอกด้วยตนเอง'); } }
});
$('save-signal').onclick = () => download(lab.signalText, `p2p-${lab.local?.kind || 'signal'}.json`);
window.receiveLabSignal = text => { if (typeof text === 'string' && new TextEncoder().encode(text).length <= LIMIT.signal) $('incoming').value = text; else banner('ไฟล์คำเชิญใหญ่เกินไป'); };
$('read-signal').onclick = () => { if (window.P2PLab) window.P2PLab.read(); else $('file').click(); };
$('file').onchange = () => action(async () => {
  const file = $('file').files[0]; if (file) { if (file.size > LIMIT.signal) throw {code:'signal.size'}; window.receiveLabSignal(await file.text()); } $('file').value = '';
});
$('confirm').onclick = () => action(() => lab.confirm());
$('transfer').onclick = () => { void action(() => lab.transfer()); };
$('stop').onclick = () => { rooms?.close();receiver = false; lab.stop(); $('incoming').value = ''; render(); };
$('clear').onclick = () => { lab.log.clear(); render(); };
$('report').onclick = () => download(JSON.stringify(lab.report(), null, 2), 'p2p-lab-result.json');
$('connect-nav').onclick = () => page(false); $('logs-nav').onclick = () => page(true);
window.addEventListener('pagehide', () => lab.stop());
const automaticStun=()=>$('auto-network').value==='stun'?$('stun').value:'';
$('create-room').onclick=()=>action(()=>rooms.create(automaticStun()));
$('join-room').onclick=()=>action(()=>rooms.join($('join-code').value.trim(),automaticStun()));
$('retry-room').onclick=()=>action(()=>rooms.retry());
$('enroll-controller').onclick=()=>action(async()=>{await rooms.enroll($('controller-invite').value.trim());$('controller-invite').value='';});
$('show-controller').onclick=()=>{$('server-setup').hidden=!$('server-setup').hidden;};
$('scan-room').onclick=()=>action(async()=>{const value=await bridge.call('scan');if(value){rooms.stun=automaticStun();await rooms.enroll(value);}});
window.addEventListener('pagehide',()=>rooms?.close());
if(bridge.available()){rooms=new Rooms(lab,bridge,render);window.pvpRooms=rooms;void rooms.initialize().catch(e=>banner(e.code||'native.unavailable'));}
if (!globalThis.RTCPeerConnection || !globalThis.crypto?.subtle) { banner('WebRTC หรือ secure context ไม่พร้อมใช้งาน กรุณาใช้แอป P2P Lab บน Android หรือ Edge/Chrome รุ่นปัจจุบันบน Windows'); $('offer').disabled = true; $('join').disabled = true; }
else render();
