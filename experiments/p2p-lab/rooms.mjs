// Signaling is an owned native HTTPS client. Browser code never receives device credentials.
export class NativeBridge {
  constructor() {
    this.pending = new Map();
    window.pvpNativeResponse = response => {
      const p = this.pending.get(response.id); if(!p)return;
      this.pending.delete(response.id);clearTimeout(p.timer);
      if(response.error)p.reject(Object.assign(new Error(response.error),{code:response.error}));else p.resolve(response.value);
    };
    window.chrome?.webview?.addEventListener('message',e=>window.pvpNativeResponse(e.data));
  }
  available() { return typeof window.P2PLab?.rpc==='function'||!!window.chrome?.webview||typeof window.pvpTestRpc==='function'; }
  call(op,args={}) {
    if(window.pvpTestRpc)return window.pvpTestRpc(op,args);
    if(this.pending.size>=16)return Promise.reject({code:'native.queue_limit'});
    return new Promise((resolve,reject)=>{
      const id=crypto.randomUUID(),timer=setTimeout(()=>{this.pending.delete(id);reject({code:'native.timeout'});},op==='scan'?120000:20000);
      this.pending.set(id,{resolve,reject,timer});
      try{if(window.P2PLab?.rpc)window.P2PLab.rpc(JSON.stringify({id,op,args}));else window.chrome.webview.postMessage({id,op,args});}
      catch{clearTimeout(timer);this.pending.delete(id);reject({code:'native.unavailable'});}
    });
  }
}
export class Rooms {
  constructor(lab,bridge,changed=()=>{}) {
    this.lab=lab;this.bridge=bridge;this.changed=changed;this.status={configured:false};this.room=null;
    this.epoch=0;this.cursor=0;this.peerJoined=false;this.queue=Promise.resolve();this.incoming=Promise.resolve();this.state='idle';this.stopPoll=false;
  }
  update(state) { this.state=state;this.lab.signaling={state,configured:this.status.configured,joined:this.peerJoined,role:this.room?.role||null};this.changed(); }
  async initialize() { this.status=await this.bridge.call('status');this.lab.nativeNetwork=this.status.network;this.update(this.status.configured?'ready':'setup'); }
  async enroll(value) {
    const result=await this.bridge.call('enroll',{value});await this.initialize();
    if(result.room)await this.join(result.room);return result;
  }
  api(method,path,body={}) { return this.bridge.call('api',{method,path,body}); }
  async create(stun) {
    this.close();this.stun=stun;this.room=await this.api('POST','/v1/rooms');this.cursor=0;this.peerJoined=false;this.stopPoll=false;
    this.update('waiting_peer');void this.poll(this.epoch);await this.startOffer();return this.room;
  }
  async join(code,stun=this.stun||'') {
    if(!/^[0-9]{8}$/.test(code))throw{code:'room.code'};
    this.close();this.stun=stun;this.room=await this.api('POST',`/v1/rooms/${code}/join`);this.cursor=0;this.peerJoined=true;this.stopPoll=false;
    this.update('paired');void this.poll(this.epoch);return this.room;
  }
  connectSignals() {
    this.initialSent=false;this.candidates=[];
    this.lab.onSignal=m=>{if(this.initialSent)void this.send(m).catch(()=>{});else if(this.candidates.length<64)this.candidates.push(m);};
  }
  async initial(kind, signal) {
    await this.send({kind,signal});this.initialSent=true;
    for(const candidate of this.candidates.splice(0))await this.send(candidate);
  }
  async startOffer() {
    const epoch=this.epoch;if(this.offerEpoch===epoch)return;this.offerEpoch=epoch;
    try{this.connectSignals();await this.lab.offer(this.stun);await this.initial('offer',this.lab.local);}
    catch(e){this.lab.record('WARN','signaling.offer_failed',e.code||e.name||'failed');throw e;}
    finally{if(this.offerEpoch===epoch)this.offerEpoch=null;}
  }
  send(data) {
    const epoch=this.epoch,room=this.room;
    const message={...data,messageId:crypto.randomUUID()};
    const task=this.queue.catch(()=>{}).then(async()=>{
      for(let attempt=0;attempt<3;attempt++){
        if(epoch!==this.epoch)return;
        try{return await this.api('POST',`/v1/rooms/${room.code}/messages`,message);}
        catch(e){if(attempt===2)throw e;await new Promise(r=>setTimeout(r,500*(attempt+1)));}
      }
    });this.queue=task;return task;
  }
  async handle(message,epoch) {
    if(epoch!==this.epoch)return;const m=message.data;
    if(m.kind==='offer'&&this.room.role==='guest'){
      if(this.lab.remote?.id===m.signal.id)return;
      this.connectSignals();await this.lab.answer(JSON.stringify(m.signal),this.stun);await this.initial('answer',this.lab.local);
    } else if(m.kind==='answer'&&this.room.role==='owner'){
      if(this.lab.remote?.id===m.signal.id)return;
      if(this.lab.id!==m.signal.id)return;
      await this.lab.accept(JSON.stringify(m.signal));
    } else if(m.kind==='candidate'||m.kind==='complete')await this.lab.addCandidate(m);
    else if(m.kind==='retry'&&this.room.role==='owner')await this.startOffer();
    else if(m.kind==='leave')this.lab.record('WARN','signaling.peer_left');
  }
  async poll(epoch) {
    let failures=0;
    while(epoch===this.epoch&&!this.stopPoll&&this.room){
      try{
        const response=await this.api('GET',`/v1/rooms/${this.room.code}/poll?after=${this.cursor}`);
        if(epoch!==this.epoch)return;
        if(response.joined&&!this.peerJoined){this.peerJoined=true;this.lab.record('INFO','signaling.peer_joined');}
        this.update(this.peerJoined?'paired':'waiting_peer');
        for(const message of response.messages){
          try{await this.handle(message,epoch);}catch(e){this.lab.record('WARN','signaling.message_failed',e.code||e.name||'failed');}
        }
        this.cursor=response.cursor;failures=0;
      }catch(e){
        if(epoch!==this.epoch)return;
        this.update('unavailable');if(failures===0)this.lab.record('WARN','signaling.unavailable',e.code||e.name||'failed');
        // A controller outage must not close a working peer data channel.
        if(e.code==='room.expired'||e.code==='auth.invalid'){this.stopPoll=true;return;}
        await new Promise(r=>setTimeout(r,Math.min(15000,1000*2**Math.min(failures++,4))));
      }
    }
  }
  async retry() { if(!this.room)return;if(this.room.role==='owner')await this.startOffer();else await this.send({kind:'retry'}); }
  close() {this.epoch++;this.stopPoll=true;this.room=null;this.peerJoined=false;this.queue=Promise.resolve();this.lab.onSignal=null;this.update('idle');}
}
