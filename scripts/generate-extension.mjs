import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderExtension } from '../src/allowlist.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const input = path.join(root, 'private', 'approved-nodes.json');
const output = path.join(root, 'private', 'generated', 'Script.js');
try {
  const approval = JSON.parse(fs.readFileSync(input, 'utf8').replace(/^\uFEFF/, ''));
  if (!/^[A-Z]{2}$/.test(approval.expectedCountry)) throw new Error('expectedCountry must be a two-letter code.');
  const source = renderExtension(approval.pins);
  fs.mkdirSync(path.dirname(output), { recursive: true });
  fs.writeFileSync(output, source, 'utf8');
  console.log(JSON.stringify({ generated: true, approvedCount: approval.pins.length, defaultSelection: 'REJECT', liveConfigurationChanged: false }));
} catch (error) {
  // Do not include parsed documents, credentials, or arbitrary exception messages.
  console.error('Generation failed. Check private/approved-nodes.json against the documented schema. No live configuration was changed.');
  process.exitCode = 1;
}
