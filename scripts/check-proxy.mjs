// Read-only, account-free check. This is not a WFP or account-safety audit.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { api } from '../src/mihomo-api.mjs';
import { renderExtension } from '../src/allowlist.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
try {
  const approval = JSON.parse(fs.readFileSync(path.join(root, 'private/approved-nodes.json'), 'utf8').replace(/^\uFEFF/, ''));
  renderExtension(approval.pins);
  if (!/^[A-Z]{2}$/.test(approval.expectedCountry)) throw new Error('Country missing.');
  const data = await api('GET', '/proxies'), group = data.proxies['Claude-Safe'];
  const names = new Set(approval.pins.map(p => p.name));
  if (!group || group.type !== 'Selector' || group.all.some(n => n !== 'REJECT' && !names.has(n))) throw new Error('Unexpected group membership.');
  if (group.now === 'REJECT') {
    console.log(JSON.stringify({ status: 'BLOCKED_BY_SELECTION', networkRequestMade: false }));
    process.exitCode = 2;
  } else {
    if (!names.has(group.now) || !group.all.includes(group.now)) throw new Error('Unapproved selection.');
    const { stdout } = await promisify(execFile)('curl.exe', ['--silent', '--show-error', '--fail', '--max-time', '8',
      '--proxy', 'http://127.0.0.1:17987', '--noproxy', '', 'https://www.cloudflare.com/cdn-cgi/trace'],
      { timeout: 10000, windowsHide: true, maxBuffer: 65536 });
    const fields = Object.fromEntries(stdout.trim().split(/\r?\n/).map(s => s.split(/=(.*)/s).slice(0, 2)));
    if (!fields.ip || !fields.loc) throw new Error('Incomplete trace.');
    const matches = fields.loc === approval.expectedCountry;
    console.log(JSON.stringify({ status: matches ? 'PROXY_EXIT_CONFIRMED' : 'COUNTRY_MISMATCH', country: fields.loc,
      exitIpSha256: crypto.createHash('sha256').update(fields.ip).digest('hex'), wfpAudited: false }));
    if (!matches) process.exitCode = 1;
  }
} catch {
  console.error('Proxy check failed. Review the approved identities, selected node, listener and connectivity locally.');
  process.exitCode = 1;
}
