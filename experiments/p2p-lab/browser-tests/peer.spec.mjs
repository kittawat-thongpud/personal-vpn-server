import { test, expect } from '@playwright/test';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
const url=pathToFileURL(resolve('dist/Personal-VPN-P2P-Lab.html')).href;
test('real isolated browser peers: verified bidirectional payload, selected path, close and bounded logs', async ({ browser }) => {
  const ca=await browser.newContext(),cb=await browser.newContext();
  const a=await ca.newPage(),b=await cb.newPage();const errors=[];const requests=[];
  for(const p of [a,b]){p.on('pageerror',e=>errors.push(e.message));p.on('request',r=>{if(!r.url().startsWith('file:'))requests.push(r.url());});await p.goto(url);}
  try{
    await a.locator('#offer').click();await expect(a.locator('#outgoing')).not.toHaveValue('',{timeout:20000});
    const offer=await a.locator('#outgoing').inputValue();
    await b.locator('#join').click();await b.locator('#incoming').fill(offer);await b.locator('#apply').click();
    await expect(b.locator('#outgoing')).not.toHaveValue('',{timeout:20000});const answer=await b.locator('#outgoing').inputValue();
    await a.locator('#incoming').fill(answer);await a.locator('#apply').click();
    for(const p of [a,b]){await expect(p.locator('#confirm')).toBeEnabled({timeout:15000});await expect(p.locator('#transfer')).toBeDisabled();}
    expect(await a.locator('#pair-code').textContent()).toBe(await b.locator('#pair-code').textContent());
    await a.locator('#confirm').click();await b.locator('#confirm').click();
    for(const p of [a,b])await expect(p.locator('#transfer')).toBeEnabled();
    await a.locator('#transfer').click();await expect(a.locator('#result')).toContainText('SHA-256 OK');await expect(a.locator('#transfer')).toBeEnabled();
    await b.locator('#transfer').click();await expect(b.locator('#result')).toContainText('SHA-256 OK');
    await expect.poll(()=>a.evaluate(()=>lab.results.filter(r=>r.sha256Verified).length)).toBe(2);
    await expect.poll(()=>a.evaluate(()=>lab.rtt)).not.toBeNull();
    const report=await a.evaluate(()=>lab.report());
    expect(report.testBytesSent).toBe(1048576);expect(report.testBytesReceived).toBe(1048576);
    expect(report.selectedPath.noTurn).toBe(true);expect(report.physicalBypassVerified).toBe(false);
    expect(JSON.stringify(report)).not.toMatch(/a=ice-pwd|a=ice-ufrag|a=fingerprint|candidate:|sdp"/);
    expect(requests).toEqual([]);expect(errors).toEqual([]);
    await a.screenshot({path:'test-results/connected.png'});
    await a.locator('#stop').click();await expect(a.locator('#outgoing')).toHaveValue('');
    await expect.poll(()=>b.evaluate(()=>lab.phase)).toBe('failed');
    expect(await a.evaluate(()=>({pc:lab.pc,timers:lab.timers.size,tx:lab.tx,rx:lab.rx}))).toEqual({pc:null,timers:0,tx:null,rx:null});
    await a.locator('#logs-nav').click();await a.locator('#clear').click();await expect(a.locator('#logs li')).toHaveCount(0);
  }finally{await ca.close();await cb.close();}
});
test('stopping ICE gathering cannot reopen a stale session; untrusted invite never executes code', async ({page})=>{
  await page.goto(url);
  await page.evaluate(async()=>{const pending=lab.offer();lab.stop();try{await pending;}catch{};});
  expect(await page.evaluate(()=>({phase:lab.phase,pc:lab.pc,signal:lab.signalText}))).toEqual({phase:'closed',pc:null,signal:''});
  await page.locator('#join').click();await page.locator('#incoming').fill('<script>window.pwned=1</script>');await page.locator('#apply').click();
  await expect(page.locator('#error')).toContainText('signal.json');expect(await page.evaluate(()=>window.pwned)).toBeUndefined();
});
