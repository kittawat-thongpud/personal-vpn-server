import { test, expect } from '@playwright/test';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp,readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join,resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import https from 'node:https';
import net from 'node:net';

test('owned HTTPS controller pairs two real browsers automatically and never carries the test payload',async({browser})=>{
  const state=await mkdtemp(join(tmpdir(),'p2p-lab-room-'));
  const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const port=listener.address().port;await new Promise(r=>listener.close(r));
  const url=`https://127.0.0.1:${port}`,script=resolve('signal-server/server.py');
  const init=spawnSync('python',[script,'init','--state',state,'--endpoint',url],{stdio:'pipe'});expect(init.status,init.stderr.toString()).toBe(0);
  const ca=await readFile(join(state,'server.crt'));const agent=new https.Agent({ca,keepAlive:false});
  const server=spawn('python',[script,'serve','--state',state,'--port',String(port)],{stdio:'ignore'});
  const request=(method,path,body={},token='')=>new Promise((resolve,reject)=>{
    const data=JSON.stringify(body);const req=https.request(url+path,{agent,method,headers:{'Content-Type':'application/json',...(token?{Authorization:`Bearer ${token}`}:{})}},res=>{let data='';res.on('data',b=>data+=b);res.on('end',()=>{const result=JSON.parse(data);if(res.statusCode>=300)reject({code:result.error});else resolve(result);});});
    req.on('error',reject);if(method==='POST')req.write(data);req.end();
  });
  const contexts=[];
  try{
    await expect.poll(async()=>{try{return(await request('GET','/health')).ok;}catch{return false;}},{timeout:10000}).toBe(true);
    const invite=(await readFile(join(state,'owner-invitation.txt'),'utf8')).trim();
    const devices=[null,null];const pages=[];
    for(let i=0;i<2;i++){
      const ctx=await browser.newContext();contexts.push(ctx);const page=await ctx.newPage();pages.push(page);
      await page.exposeBinding('pvpTestRpc',async(_source,op,args)=>{
        if(op==='status')return{configured:!!devices[i],role:devices[i]?.role||'',scanSupported:false,network:{vpnActive:false}};
        if(op==='enroll'){
          const data=JSON.parse(Buffer.from(args.value.slice(5),'base64url').toString());
          devices[i]=await request('POST','/v1/enroll',{ticket:data.ticket});return{room:data.room};
        }
        if(op==='api')return request(args.method,args.path,args.body,devices[i]?.token||'');
        if(op==='qr')return'data:image/png;base64,';
        throw{code:'test.unsupported'};
      });
      await page.goto(pathToFileURL(resolve('dist/Personal-VPN-P2P-Lab.html')).href);
    }
    const [a,b]=pages;
    await a.locator('#controller-invite').fill(invite);await a.locator('#enroll-controller').click();await expect(a.locator('#create-room')).toBeVisible();
    await a.locator('#create-room').click();await expect(a.locator('#room-code')).toHaveText(/^[0-9]{8}$/);
    const guestInvite=await a.evaluate(()=>pvpRooms.room.invite);await b.evaluate(value=>pvpRooms.enroll(value),guestInvite);
    for(const p of pages)await expect(p.locator('#confirm')).toBeEnabled({timeout:20000});
    await a.locator('#confirm').click();await b.locator('#confirm').click();for(const p of pages)await expect(p.locator('#transfer')).toBeEnabled();
    await a.locator('#transfer').click();await expect(a.locator('#result')).toContainText('SHA-256 OK');
    await b.locator('#transfer').click();await expect.poll(()=>a.evaluate(()=>lab.totalDown)).toBe(1048576);
    const report=await a.evaluate(()=>lab.report());expect(report.signaling.joined).toBe(true);expect(report.diagnostics.local.total).toBeGreaterThan(0);
    expect(JSON.stringify(report)).not.toMatch(/a=ice-pwd|pvp2:|"token"|"sdp"|candidate:/);
    // A controller outage must not tear down an established peer data channel.
    server.kill();await new Promise(r=>setTimeout(r,500));await a.locator('#transfer').click();await expect.poll(()=>a.evaluate(()=>lab.totalUp)).toBe(2097152);
    await expect.poll(()=>a.evaluate(()=>lab.results.filter(r=>r.direction==='send'&&r.sha256Verified).length)).toBe(2);
    for(const p of pages)await p.evaluate(()=>{pvpRooms.close();lab.stop();});
  }finally{for(const context of contexts)await context.close();server.kill();agent.destroy();}
});
