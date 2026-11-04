// Peer data engine only. Native pinned-HTTPS rendezvous is a separate module.
export const LIMIT = Object.freeze({ signal: 65536, ttl: 600000, transfer: 1048576, chunk: 16384, records: 1000, logBytes: 1048576 });
const uuid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const hash = /^[a-f0-9]{64}$/;
const enc = new TextEncoder();
export const hex = bytes => [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, '0')).join('');
export async function sha256(bytes) { return hex(await crypto.subtle.digest('SHA-256', bytes)); }

export class LabError extends Error { constructor(code) { super(code); this.code = code; } }
function requireThat(ok, code) { if (!ok) throw new LabError(code); }
export class SessionLog {
  constructor() { this.rows = []; this.bytes = 0; this.revision = 0; }
  push(level, code, detail = '') {
    const row = { time: new Date().toISOString(), level, code, detail: detail.slice(0, 512) };
    row.size = enc.encode(JSON.stringify(row)).length;
    this.rows.push(row); this.bytes += row.size;
    while (this.rows.length > LIMIT.records || this.bytes > LIMIT.logBytes) this.bytes -= this.rows.shift().size;
    this.revision++;
  }
  clear() { this.rows = []; this.bytes = 0; this.revision++; }
  export() { return this.rows.map(({ size, ...row }) => row); }
}
export function iceServers(value = '') {
  if (!value.trim()) return [];
  const urls = value.trim().split(/\s+/);
  requireThat(urls.length <= 3, 'stun.too_many');
  for (const url of urls) {
    const m = /^stun:(\[[a-fA-F0-9:]+\]|[a-zA-Z0-9.-]+):([0-9]{1,5})$/.exec(url);
    requireThat(m && Number(m[2]) > 0 && Number(m[2]) <= 65535, 'stun.invalid');
  }
  return urls.map(urls => ({ urls }));
}
export function fingerprint(sdp) {
  const lines = [...sdp.matchAll(/^a=fingerprint:sha-256 ([A-Fa-f0-9:]+)\r?$/gm)];
  requireThat(lines.length === 1 && /^(?:[a-fA-F0-9]{2}:){31}[a-fA-F0-9]{2}$/.test(lines[0][1]), 'signal.fingerprint');
  return lines[0][1].toUpperCase();
}
export function decodeSignal(text, now = Date.now()) {
  requireThat(typeof text === 'string' && enc.encode(text).length <= LIMIT.signal, 'signal.size');
  let data;
  try { data = JSON.parse(text); } catch { throw new LabError('signal.json'); }
  requireThat(data && Object.getPrototypeOf(data) === Object.prototype, 'signal.object');
  requireThat(Object.keys(data).sort().join(',') === 'expires,id,kind,sdp,v', 'signal.fields');
  requireThat(data.v === 1 && (data.kind === 'offer' || data.kind === 'answer') && uuid.test(data.id), 'signal.format');
  requireThat(Number.isSafeInteger(data.expires) && data.expires > now && data.expires <= now + LIMIT.ttl + 30000, 'signal.expired');
  requireThat(typeof data.sdp === 'string' && data.sdp.length < LIMIT.signal, 'signal.sdp');
  const media = data.sdp.split(/\r?\n/).filter(s => s.startsWith('m='));
  requireThat(media.length === 1 && /^m=application \d+ UDP\/DTLS\/SCTP webrtc-datachannel$/.test(media[0]), 'signal.data_only');
  requireThat(!/\btyp relay\b/i.test(data.sdp), 'signal.relay_forbidden');
  requireThat(!/a=ice-lite|a=crypto:/i.test(data.sdp), 'signal.unsupported');
  fingerprint(data.sdp);
  return data;
}
export function selectedPath(stats) {
  let pair;
  for (const row of stats.values()) {
    if (row.type === 'transport' && row.selectedCandidatePairId) pair = stats.get(row.selectedCandidatePairId);
  }
  if (!pair) pair = [...stats.values()].find(r => r.type === 'candidate-pair' && r.selected === true);
  if (!pair || pair.state !== 'succeeded') return null;
  const a = stats.get(pair.localCandidateId), b = stats.get(pair.remoteCandidateId);
  if (!a || !b) return null;
  requireThat(a.candidateType !== 'relay' && b.candidateType !== 'relay', 'path.relay_forbidden');
  const known = type => ['host', 'srflx', 'prflx'].includes(type);
  if (!known(a.candidateType) || !known(b.candidateType)) return null;
  // No address/port/ICE credential in reports. Candidate types cannot prove VPN bypass.
  return { local: a.candidateType, remote: b.candidateType, protocol: a.protocol || 'unknown', noTurn: true,
    physicalBypassVerified: false };
}
export function candidateSummary(sdp = '') {
  const result = { total:0, host:0, srflx:0, prflx:0, relay:0, mdns:0, privateV4:0, publicV4:0, ipv6:0, udp:0, tcp:0 };
  for (const line of sdp.split(/\r?\n/)) {
    if (!line.startsWith('a=candidate:')) continue;
    const f = line.slice(2).trim().split(/\s+/), type = f[f.indexOf('typ')+1], address = f[4] || '';
    result.total++; if (['host','srflx','prflx','relay'].includes(type)) result[type]++;
    if (/\.local$/i.test(address)) result.mdns++;
    else if (address.includes(':')) result.ipv6++;
    else if (/^\d+\.\d+\.\d+\.\d+$/.test(address)) {
      const p = address.split('.').map(Number);
      if (p[0]===10 || p[0]===127 || (p[0]===172&&p[1]>=16&&p[1]<=31) || (p[0]===192&&p[1]===168) || (p[0]===169&&p[1]===254)) result.privateV4++;
      else result.publicV4++;
    }
    const protocol = (f[2]||'').toLowerCase(); if (protocol==='udp'||protocol==='tcp') result[protocol]++;
  }
  return result;
}
export async function pairingCode(local, remote) {
  const offer = local.kind === 'offer' ? local : remote, answer = local.kind === 'answer' ? local : remote;
  requireThat(offer.id === answer.id && offer.kind === 'offer' && answer.kind === 'answer', 'signal.session');
  const digest = await sha256(enc.encode(`pvp-p2p-lab/v1\n${offer.id}\n${fingerprint(offer.sdp)}\n${fingerprint(answer.sdp)}`));
  return digest.slice(0, 32).toUpperCase().match(/.{8}/g).join(' ');
}
async function gather(pc, signal) {
  if (pc.iceGatheringState === 'complete') return true;
  return new Promise((resolve, reject) => {
    const finish = (ok, aborted = false) => {
      clearTimeout(timer); pc.removeEventListener('icegatheringstatechange', changed); signal.removeEventListener('abort', abort);
      if (aborted) reject(new LabError('session.cancelled')); else resolve(ok);
    };
    const changed = () => { if (pc.iceGatheringState === 'complete') finish(true); };
    const abort = () => finish(false, true);
    const timer = setTimeout(() => finish(false), 12000);
    pc.addEventListener('icegatheringstatechange', changed); signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) abort(); else changed();
  });
}

export class PeerLab {
  constructor(changed = () => {}) {
    this.changed = changed; this.log = new SessionLog(); this.phase = 'idle'; this.pc = null; this.channel = null;
    this.timers = new Set(); this.totalUp = 0; this.totalDown = 0; this.upRate = 0; this.downRate = 0;
    this.history = []; this.results = []; this.path = null; this.rtt = null; this.code = ''; this.confirmed = false;
    this.remoteReady = false; this.local = null; this.remote = null; this.signalText = ''; this.tx = null; this.rx = null;
    this.checkingStats = false; this.ping = null;
    this.diagnostics = {}; this.onSignal = null; this.remoteCandidates = [];
  }
  emit() { this.changed(); }
  record(level, code, detail = '') { this.log.push(level, code, detail); this.emit(); }
  state(phase) { if (this.phase !== phase) { this.phase = phase; this.record('INFO', `state.${phase}`); } }
  later(fn, ms) { const id = setTimeout(() => { this.timers.delete(id); fn(); }, ms); this.timers.add(id); return id; }
  cancel(id) { clearTimeout(id); this.timers.delete(id); }
  active(pc) { requireThat(pc === this.pc && !this.abort.signal.aborted, 'session.cancelled'); }
  step(promise, pc) {
    // Chromium may leave an in-flight createOffer unresolved if close() races it.
    // Cancellation/deadlines must settle our operation independently of the browser.
    const signal = this.abort.signal; this.active(pc);
    return new Promise((resolve, reject) => {
      const finish = (value, error) => {
        this.cancel(timer); signal.removeEventListener('abort', abort);
        if (error) reject(error); else resolve(value);
      };
      const abort = () => finish(null, new LabError('session.cancelled'));
      const timer = this.later(() => finish(null, new LabError('peer.operation_timeout')), 20000);
      signal.addEventListener('abort', abort, {once:true});
      Promise.resolve(promise).then(value => finish(value), error => finish(null, error));
      if (signal.aborted) abort();
    });
  }
  canTest() { return this.phase === 'connected' && this.channel?.readyState === 'open' && this.path?.noTurn && this.confirmed && this.remoteReady; }
  stop(phase = 'closed', reason = '') {
    if (this.pc) this.snapshot(this.pc);
    this.abort?.abort(); for (const timer of this.timers) clearTimeout(timer); this.timers.clear();
    clearInterval(this.ticker); this.ticker = null;
    clearInterval(this.diagnosticTicker); this.diagnosticTicker = null;
    const pc = this.pc; this.pc = null; const channel = this.channel; this.channel = null;
    this.tx?.reject(new LabError(reason || 'session.closed')); this.tx = null; this.rx = null; this.sending = false; this.ping = null;
    channel?.close(); pc?.close(); this.path = null; this.confirmed = false; this.remoteReady = false;
    this.signalText = ''; this.local = null; this.remote = null; this.code = ''; this.upRate = 0; this.downRate = 0;
    this.state(phase); if (reason) this.record('WARN', reason); this.emit();
  }
  fail(code) { this.stop('failed', code); }
  newPeer(stun, id = crypto.randomUUID(), expires = Date.now() + LIMIT.ttl) {
    const servers = iceServers(stun); this.stop('idle'); this.id = id; this.expires = expires;
    this.totalUp = 0; this.totalDown = 0; this.history = []; this.results = []; this.rtt = null; this.lastPath = null;
    this.diagnostics = { discoveryErrors:[], pairs:[], network:this.nativeNetwork||null }; this.remoteCandidates = []; this.diagnosticSummary='';
    this.abort = new AbortController(); this.checkingStats = false;
    const pc = this.pc = new RTCPeerConnection({ iceServers: servers, iceTransportPolicy: 'all', iceCandidatePoolSize: 0 });
    this.record('INFO', 'peer.created', servers.length ? 'STUN discovery enabled; no TURN.' : 'LAN candidates only; no discovery service.');
    pc.addEventListener('datachannel', e => { if (this.pc === pc) this.bind(e.channel, pc); });
    pc.addEventListener('icecandidate', e => {
      if (this.pc !== pc) return;
      if (e.candidate?.type === 'relay') { this.fail('path.relay_forbidden'); return; }
      this.snapshot(pc);
      if (this.local && this.onSignal) this.onSignal(e.candidate ? {kind:'candidate', session:this.id, candidate:e.candidate.toJSON()} : {kind:'complete',session:this.id});
    });
    pc.addEventListener('icecandidateerror', e => {
      if (this.pc !== pc) return;
      const text = (e.errorText||'').toLowerCase();
      const reason = /lookup|resolve|dns/.test(text) ? 'lookup_failed' : /timed? ?out|timeout/.test(text) ? 'timeout' : 'unreachable';
      const item = {code:Number(e.errorCode)||0,reason};
      if (this.diagnostics.discoveryErrors.length < 8) this.diagnostics.discoveryErrors.push(item);
      this.record('WARN', 'discovery.unavailable', `code=${item.code}; ${reason}`);
    });
    pc.addEventListener('iceconnectionstatechange', () => { if (this.pc===pc) { this.snapshot(pc); this.record('INFO','ice.state',pc.iceConnectionState); } });
    this.diagnosticTicker = setInterval(() => void this.inspect(pc), 2000);
    pc.addEventListener('connectionstatechange', () => {
      if (this.pc !== pc) return;
      if (pc.connectionState === 'failed') this.fail('path.no_direct_path');
      else if (pc.connectionState === 'disconnected') {
        this.state('degraded'); this.later(() => { if (this.pc === pc && pc.connectionState === 'disconnected') this.fail('path.disconnected'); }, 10000);
      } else if (pc.connectionState === 'connected' && this.channel?.readyState === 'open') this.state('connected');
    });
    this.later(() => { if (this.pc === pc && !this.remote) this.fail('signal.expired'); }, Math.max(1, expires - Date.now()));
    return pc;
  }
  async offer(stun = '') {
    const pc = this.newPeer(stun); this.state('gathering');
    try {
      this.bind(pc.createDataChannel('Personal VPN P2P Lab', { ordered: true, protocol: 'pvp-lab/1' }), pc);
      const description = await this.step(pc.createOffer(), pc); this.active(pc);
      await this.step(pc.setLocalDescription(description), pc); this.active(pc);
      const complete = await gather(pc, this.abort.signal); this.active(pc);
      this.exportSignal(pc); if (!complete) this.record('WARN', 'discovery.partial', 'Only gathered candidates were included.');
      this.state('waiting'); return this.signalText;
    } catch (e) { if (this.pc === pc) this.fail(e.code || 'peer.offer_failed'); throw e; }
  }
  async answer(text, stun = '') {
    const offer = decodeSignal(text); requireThat(offer.kind === 'offer', 'signal.expected_offer');
    const pc = this.newPeer(stun, offer.id, offer.expires); this.remote = offer; this.state('gathering');
    try {
      await this.step(pc.setRemoteDescription({ type: 'offer', sdp: offer.sdp }), pc); this.active(pc);
      const description = await this.step(pc.createAnswer(), pc); this.active(pc);
      await this.step(pc.setLocalDescription(description), pc); this.active(pc);
      const complete = await gather(pc, this.abort.signal); this.active(pc); this.exportSignal(pc);
      this.code = await pairingCode(this.local, this.remote); this.active(pc);
      if (!complete) this.record('WARN', 'discovery.partial', 'Only gathered candidates were included.');
      this.connectDeadline(pc); this.emit(); return this.signalText;
    } catch (e) { if (this.pc === pc) this.fail(e.code || 'peer.answer_failed'); throw e; }
  }
  exportSignal(pc) {
    this.local = { v: 1, kind: pc.localDescription.type, id: this.id, expires: this.expires, sdp: pc.localDescription.sdp };
    this.signalText = JSON.stringify(this.local); decodeSignal(this.signalText);
    this.snapshot(pc);
  }
  async accept(text) {
    const answer = decodeSignal(text); const pc = this.pc;
    requireThat(pc && this.local?.kind === 'offer' && !this.remote, 'signal.not_waiting');
    requireThat(answer.kind === 'answer' && answer.id === this.id && answer.expires <= this.expires, 'signal.session');
    try {
      await this.step(pc.setRemoteDescription({ type: 'answer', sdp: answer.sdp }), pc); this.active(pc); this.remote = answer;
      this.code = await pairingCode(this.local, this.remote); this.active(pc); this.connectDeadline(pc); this.emit();
      await this.flushCandidates(pc);
    } catch (e) { if (this.pc === pc) this.fail(e.code || 'peer.accept_failed'); throw e; }
  }
  connectDeadline(pc) {
    if (this.channel?.readyState !== 'open') this.state('connecting');
    this.later(() => { if (this.pc === pc && this.channel?.readyState !== 'open') this.fail('path.no_direct_path'); }, 45000);
  }
  snapshot(pc) {
    this.diagnostics.ice = pc.iceConnectionState; this.diagnostics.gathering = pc.iceGatheringState;
    this.diagnostics.connection = pc.connectionState;
    this.diagnostics.local = candidateSummary(pc.localDescription?.sdp || '');
    this.diagnostics.remote = candidateSummary(pc.remoteDescription?.sdp || '');
    this.diagnostics.sctp = pc.sctp?.state || 'unavailable';
  }
  async inspect(pc) {
    if (this.pc!==pc || this.inspecting===pc) return; this.inspecting=pc;
    try {
      const stats = await pc.getStats(); if (this.pc!==pc) return;
      this.snapshot(pc);
      this.diagnostics.pairs = [...stats.values()].filter(s=>s.type==='candidate-pair').slice(0,16).map(s=>({state:s.state,nominated:!!s.nominated,requestsSent:s.requestsSent||0,responsesReceived:s.responsesReceived||0,requestsReceived:s.requestsReceived||0,responsesSent:s.responsesSent||0}));
      const summary=JSON.stringify({ice:this.diagnostics.ice,local:this.diagnostics.local,remote:this.diagnostics.remote,pairs:this.diagnostics.pairs.map(p=>({state:p.state,requests:p.requestsSent>0,responses:p.responsesReceived>0}))});
      if(summary!==this.diagnosticSummary){this.diagnosticSummary=summary;this.record('INFO','ice.diagnostic',`local=${this.diagnostics.local.total} (mDNS=${this.diagnostics.local.mdns}, srflx=${this.diagnostics.local.srflx}); remote=${this.diagnostics.remote.total}; pairs=${this.diagnostics.pairs.length}; responses=${this.diagnostics.pairs.reduce((n,p)=>n+p.responsesReceived,0)}`);}
    } catch { if(this.pc===pc)this.record('WARN','ice.stats_unavailable'); }
    finally { if(this.inspecting===pc)this.inspecting=null; }
  }
  async addCandidate(message) {
    if (!this.pc || message.session!==this.id) return;
    requireThat(this.remoteCandidates.length<64,'candidate.limit');
    this.remoteCandidates.push(message.kind==='complete'?null:message.candidate);
    await this.flushCandidates(this.pc);
  }
  async flushCandidates(pc) {
    if(!pc.remoteDescription)return;
    while(this.remoteCandidates.length){const c=this.remoteCandidates.shift();if(c)requireThat(typeof c.candidate==='string'&&!/\btyp relay\b/i.test(c.candidate),'candidate.invalid');await this.step(pc.addIceCandidate(c),pc);this.active(pc);}
  }
  bind(channel, pc) {
    if (this.channel || channel.label !== 'Personal VPN P2P Lab' || channel.protocol !== 'pvp-lab/1' || !channel.ordered) {
      channel.close(); this.fail('channel.unsupported'); return;
    }
    this.channel = channel; channel.binaryType = 'arraybuffer';
    channel.addEventListener('open', () => {
      if (this.pc !== pc) return;
      this.state('connected'); this.lastTick = { at: performance.now(), up: 0, down: 0 };
      void this.tick(pc); this.ticker = setInterval(() => void this.tick(pc), 1000);
    });
    channel.addEventListener('close', () => { if (this.pc === pc) this.fail('channel.closed'); });
    channel.addEventListener('error', () => { if (this.pc === pc) this.fail('channel.error'); });
    channel.addEventListener('message', e => {
      if (this.pc !== pc) return;
      Promise.resolve(this.message(e.data, pc)).catch(error => { if (this.pc === pc) this.fail(error.code || 'protocol.invalid'); });
    });
  }
  async tick(pc) {
    if (this.checkingStats || this.pc !== pc) return; this.checkingStats = true;
    try {
      const path = selectedPath(await pc.getStats()); if (this.pc !== pc) return;
      if (path && !this.path) this.record('INFO', 'path.no_turn', 'Selected WebRTC candidates exclude TURN; physical VPN bypass is unverified.');
      this.path = path; if (path) this.lastPath = path;
      const now = performance.now(), seconds = Math.max(0.001, (now - this.lastTick.at) / 1000);
      this.upRate = (this.totalUp - this.lastTick.up) / seconds; this.downRate = (this.totalDown - this.lastTick.down) / seconds;
      this.lastTick = { at: now, up: this.totalUp, down: this.totalDown };
      this.history.push([this.upRate, this.downRate]); if (this.history.length > 60) this.history.shift();
      if (this.canTest() && !this.ping) this.sendPing(); this.emit();
    } catch (e) { if (this.pc === pc) this.fail(e.code || 'path.stats_failed'); }
    finally { if (this.pc === pc) this.checkingStats = false; }
  }
  control(data) { requireThat(this.channel?.readyState === 'open', 'channel.not_open'); this.channel.send(JSON.stringify(data)); }
  confirm() {
    requireThat(this.code && this.path?.noTurn && this.channel?.readyState === 'open', 'identity.not_ready');
    this.confirmed = true; this.control({ type: 'ready' }); this.record('INFO', 'identity.confirmed');
  }
  sendPing() {
    const ping = this.ping = { id: crypto.randomUUID(), at: performance.now() };
    ping.timer = this.later(() => { if (this.ping === ping) this.fail('peer.unresponsive'); }, 8000);
    this.control({ type: 'ping', id: ping.id });
  }
  async message(data, pc) {
    if (typeof data !== 'string') {
      requireThat(this.canTest() && this.rx && data instanceof ArrayBuffer && data.byteLength > 0 && data.byteLength <= LIMIT.chunk, 'transfer.unexpected_data');
      const rx = this.rx; requireThat(rx.offset + data.byteLength <= LIMIT.transfer, 'transfer.overflow');
      rx.buffer.set(new Uint8Array(data), rx.offset); rx.offset += data.byteLength; this.totalDown += data.byteLength;
      if (rx.offset === LIMIT.transfer) {
        const digest = await sha256(rx.buffer); if (this.pc !== pc || this.rx !== rx) return;
        requireThat(digest === rx.digest, 'transfer.integrity_failed'); this.cancel(rx.timer); this.rx = null;
        this.control({ type: 'result', id: rx.id, digest, bytes: LIMIT.transfer });
        this.result('receive', performance.now() - rx.at); this.emit();
      }
      return;
    }
    requireThat(data.length < 1500, 'protocol.size');
    this.controlCount = (this.controlWindow && performance.now() - this.controlWindow < 1000) ? this.controlCount + 1 : 1;
    if (this.controlCount === 1) this.controlWindow = performance.now();
    requireThat(this.controlCount <= 32, 'protocol.rate');
    let m; try { m = JSON.parse(data); } catch { throw new LabError('protocol.json'); }
    requireThat(m && typeof m === 'object', 'protocol.object');
    if (m.type === 'ready') { this.remoteReady = true; this.emit(); return; }
    requireThat(this.canTest(), 'identity.not_confirmed');
    if (m.type === 'ping') { requireThat(uuid.test(m.id), 'protocol.id'); this.control({ type: 'pong', id: m.id }); }
    else if (m.type === 'pong') {
      requireThat(this.ping && m.id === this.ping.id, 'protocol.pong');
      this.rtt = performance.now() - this.ping.at; this.cancel(this.ping.timer); this.ping = null; this.emit();
    } else if (m.type === 'start') {
      requireThat(!this.rx && uuid.test(m.id) && m.bytes === LIMIT.transfer && hash.test(m.digest), 'transfer.metadata');
      const rx = this.rx = { id: m.id, digest: m.digest, buffer: new Uint8Array(LIMIT.transfer), offset: 0, at: performance.now() };
      rx.timer = this.later(() => { if (this.rx === rx) this.fail('transfer.receive_timeout'); }, 25000);
      this.control({ type: 'accept', id: m.id }); this.record('INFO', 'transfer.receiving');
    } else if (m.type === 'accept') {
      const tx = this.tx; requireThat(tx && tx.id === m.id && !tx.accepted, 'transfer.accept');
      tx.accepted = true; this.pump(tx);
    } else if (m.type === 'result') {
      const tx = this.tx;
      requireThat(tx && tx.id === m.id && tx.offset === LIMIT.transfer && m.bytes === LIMIT.transfer && m.digest === tx.digest, 'transfer.result');
      this.cancel(tx.timer); this.tx = null; const result = this.result('send', performance.now() - tx.at); tx.resolve(result);
    } else throw new LabError('protocol.type');
  }
  result(direction, ms) {
    const result = { direction, bytes: LIMIT.transfer, sha256Verified: true, elapsedMs: Math.round(ms), goodputMbps: Number((LIMIT.transfer * 8 / Math.max(1, ms) / 1000).toFixed(2)) };
    this.results.push(result); if (this.results.length > 20) this.results.shift();
    this.record('INFO', 'transfer.verified', `${direction}: 1 MiB, SHA-256 OK, ${result.goodputMbps} Mbps including verification.`);
    return result;
  }
  async transfer() {
    requireThat(this.canTest() && !this.sending, 'transfer.not_ready'); this.sending = true; this.emit();
    const pc = this.pc;
    try {
      const bytes = new Uint8Array(LIMIT.transfer);
      for (let i = 0; i < bytes.length; i += 65536) crypto.getRandomValues(bytes.subarray(i, i + 65536));
      const digest = await sha256(bytes); this.active(pc);
      return await new Promise((resolve, reject) => {
        const tx = this.tx = { id: crypto.randomUUID(), bytes, digest, offset: 0, at: performance.now(), resolve, reject, accepted: false };
        tx.timer = this.later(() => { if (this.tx === tx) this.fail('transfer.send_timeout'); }, 25000);
        this.control({ type: 'start', id: tx.id, bytes: LIMIT.transfer, digest }); this.record('INFO', 'transfer.sending');
      });
    } finally { if (this.pc === pc) { this.sending = false; this.emit(); } }
  }
  pump(tx) {
    if (this.tx !== tx) return;
    try {
      requireThat(this.canTest(), 'transfer.path_lost');
      while (tx.offset < LIMIT.transfer && this.channel.bufferedAmount < 262144) {
        const end = Math.min(tx.offset + LIMIT.chunk, LIMIT.transfer);
        this.channel.send(tx.bytes.slice(tx.offset, end)); this.totalUp += end - tx.offset; tx.offset = end;
      }
      if (tx.offset < LIMIT.transfer) this.later(() => this.pump(tx), 20);
    } catch (e) { this.fail(e.code || 'transfer.send_failed'); }
  }
  report() {
    return { format: 'personal-vpn-p2p-lab/1', revision:'lab.2', generatedAt: new Date().toISOString(), phase: this.phase,
      environment: globalThis.navigator?.userAgent || 'unknown', routeProof: 'WebRTC candidates only; underlying VPN/mesh path is unverified',
      physicalBypassVerified: false, selectedPath: this.path || this.lastPath || null, roundTripMs: this.rtt === null ? null : Math.round(this.rtt),
      testBytesSent: this.totalUp, testBytesReceived: this.totalDown, results: this.results, diagnostics:this.diagnostics, signaling:this.signaling||null, logs: this.log.export() };
  }
}
