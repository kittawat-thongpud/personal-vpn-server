import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decodeSignal, iceServers, SessionLog, LIMIT, selectedPath, pairingCode, PeerLab, candidateSummary } from './core.mjs';
const id = '12345678-1234-1234-1234-123456789012';
const sdp = fp => `v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 ${Array(32).fill(fp).join(':')}\r\n`;
const signal = (kind='offer', fp='AB') => ({ v:1, id, expires:Date.now()+60000, kind, sdp:sdp(fp) });
test('manual signaling rejects expired, oversized, wrong shape, relay and media inputs', () => {
  assert.equal(decodeSignal(JSON.stringify(signal())).id, id);
  for (const value of [null, [], {...signal(), expires:0}, {...signal(), extra:'turn:relay'}, {...signal(), v:2}, {...signal(), sdp:sdp('ZZ')},
    {...signal(), sdp:sdp('AB')+'a=candidate:1 1 udp 1 1.2.3.4 42 typ relay\r\n'}, {...signal(), sdp:sdp('AB')+'m=audio 9 RTP/SAVPF 0\r\n'}])
    assert.throws(() => decodeSignal(JSON.stringify(value)));
  assert.throws(() => decodeSignal('x'.repeat(LIMIT.signal+1)));
});
test('no TURN credentials or arbitrary service URLs can enter ICE configuration', () => {
  assert.deepEqual(iceServers(), []);
  assert.deepEqual(iceServers('stun:stun.cloudflare.com:3478'), [{urls:'stun:stun.cloudflare.com:3478'}]);
  for (const url of ['turn:host:3478','turns:host:443','https://host:443','stun:user@host:3478','stun:host:0','stun:host:65536']) assert.throws(() => iceServers(url));
});
test('path proof follows the selected transport, never a stale successful pair or physical-bypass inference', () => {
  const stats = new Map([
    ['old', {type:'candidate-pair',state:'succeeded',nominated:true,localCandidateId:'a',remoteCandidateId:'b'}],
    ['a', {candidateType:'relay',protocol:'udp',address:'secret'}], ['b', {candidateType:'host',protocol:'udp'}],
    ['selected', {type:'candidate-pair',state:'succeeded',localCandidateId:'c',remoteCandidateId:'b'}],
    ['c', {candidateType:'srflx',protocol:'udp',address:'private'}], ['transport',{type:'transport',selectedCandidatePairId:'selected'}]
  ]);
  assert.deepEqual(selectedPath(stats), {local:'srflx',remote:'host',protocol:'udp',noTurn:true,physicalBypassVerified:false});
  stats.delete('transport'); assert.equal(selectedPath(stats), null);
  stats.set('transport',{type:'transport',selectedCandidatePairId:'old'}); assert.throws(() => selectedPath(stats));
});
test('pairing identity is role-independent and binds both certificates and session', async () => {
  const a=signal(),b=signal('answer','CD');
  assert.equal(await pairingCode(a,b),await pairingCode(b,a));
  assert.notEqual(await pairingCode(a,b),await pairingCode(a,signal('answer','EF')));
  await assert.rejects(pairingCode(a,{...b,id:'87654321-1234-1234-1234-123456789012'}));
});
test('bounded in-memory backend logs discard oldest records and clear without persistence', () => {
  const log=new SessionLog();
  for(let i=0;i<2000;i++)log.push('INFO',`backend.${i}`,'x'.repeat(5000));
  assert.equal(log.rows.length,1000); assert.ok(log.bytes<=LIMIT.logBytes);
  assert.equal(log.rows[0].code,'backend.1000'); assert.equal(log.export()[0].size,undefined);
  log.clear();assert.equal(log.bytes,0);assert.equal(log.rows.length,0);
});
test('stop settles negotiation even when the platform promise never resolves', async () => {
  const lab = new PeerLab(); const pc = {close(){}}; lab.pc = pc; lab.abort = new AbortController();
  const pending = lab.step(new Promise(()=>{}),pc); lab.stop();
  await assert.rejects(pending, error=>error.code==='session.cancelled');
  assert.equal(lab.timers.size,0); assert.equal(lab.pc,null);
});
test('candidate diagnostics retain categories without addresses or credentials',()=>{
  const value=candidateSummary('a=candidate:1 1 udp 1 abc-secret.local 42 typ host\r\na=candidate:2 1 udp 1 10.23.4.5 44 typ host\r\na=candidate:3 1 udp 1 203.0.113.4 55 typ srflx');
  assert.equal(value.total,3);assert.equal(value.mdns,1);assert.equal(value.privateV4,1);assert.equal(value.srflx,1);
  assert.doesNotMatch(JSON.stringify(value),/secret|10\.23|203\.0/);
});
