import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
const root = new URL('./', import.meta.url);
// HTML normalizes line endings before CSP hashing; make Windows checkouts match.
const read = async name => (await readFile(new URL(name, root), 'utf8')).replace(/\r\n/g, '\n');
const core = (await read('core.mjs')).replace(/^export /gm, '');
const rooms = (await read('rooms.mjs')).replace(/^export /gm, '');
const app = (await read('app.mjs')).replace(/^import .*;\r?\n/gm, '');
const script = `'use strict';\n{\n${core}\n${rooms}\n${app}\n}\n`;
if (/<\/script/i.test(script)) throw Error('Unsafe inline script terminator');
const style = await read('style.css');
const digest = text => createHash('sha256').update(text).digest('base64');
const csp = `default-src 'none'; script-src 'sha256-${digest(script)}'; style-src 'sha256-${digest(style)}'; connect-src 'none'; img-src data:; base-uri 'none'; form-action 'none'; object-src 'none'`;
const html = (await read('index.template.html')).replace('{{CSP}}', csp).replace('{{STYLE}}', () => style).replace('{{SCRIPT}}', () => script);
const out = new URL('dist/', root); await mkdir(out, {recursive:true});
await writeFile(new URL('Personal-VPN-P2P-Lab.html', out), html);
await writeFile(new URL('README.md', out), await read('README.md'));
console.log('Created offline HTML. No runtime library, server or external asset.');
