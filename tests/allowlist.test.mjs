import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { applyClaudePolicy, renderExtension } from '../src/allowlist.mjs';
const pins = [1, 2].map(i => ({ name: 'Approved-' + i, type: 'vless', server: 'node' + i + '.example.invalid', port: 443 }));
const fixture = () => ({ proxies: structuredClone(pins), 'proxy-groups': [{ name: 'Ordinary', type: 'select', proxies: ['DIRECT'] }],
  rules: ['MATCH,Ordinary'], listeners: [], tun: { enable: true }, mode: 'rule' });
const members = c => c['proxy-groups'].find(g => g.name === 'Claude-Safe').proxies;
test('empty approval fails closed', () => assert.deepEqual(members(applyClaudePolicy(fixture(), [])), ['REJECT']));
test('only approved leaves are selectable; unrelated configuration is preserved', () => {
  const input = fixture(), output = applyClaudePolicy(input, pins);
  assert.deepEqual(members(output), ['REJECT', 'Approved-1', 'Approved-2']);
  assert.equal(output['proxy-groups'][0]['empty-fallback'], 'REJECT');
  assert.equal(output['proxy-groups'][0]['default-selected'], 'REJECT');
  assert.equal(output.tun.enable, true); assert.equal(output.mode, 'rule');
  assert.deepEqual(output['proxy-groups'][1], { name: 'Ordinary', type: 'select', proxies: ['DIRECT'] });
  assert.equal(output.rules.at(-1), 'MATCH,Ordinary');
  assert.deepEqual(output.listeners[0], { name: 'claude-safe', type: 'mixed', listen: '127.0.0.1', port: 17987, udp: false, proxy: 'Claude-Safe' });
});
for (const failure of ['missing', 'duplicate', 'server', 'port', 'type', 'chain', 'group-collision']) {
  test('exclude a leaf on ' + failure, () => {
    const config = fixture(), node = config.proxies[0];
    if (failure === 'missing') config.proxies.shift();
    if (failure === 'duplicate') config.proxies.push({ ...node });
    if (failure === 'server') node.server = 'changed.example.invalid';
    if (failure === 'port') node.port = 444;
    if (failure === 'type') node.type = 'http';
    if (failure === 'chain') node['dialer-proxy'] = 'Ordinary';
    if (failure === 'group-collision') config['proxy-groups'].push({ name: node.name, type: 'select', proxies: ['DIRECT'] });
    assert.deepEqual(members(applyClaudePolicy(config, pins)), ['REJECT', 'Approved-2']);
  });
}
test('all absent nodes yield REJECT', () => assert.deepEqual(members(applyClaudePolicy({}, pins)), ['REJECT']));
test('reserved identities, duplicate pins and credential fields are refused', () => {
  for (const invalid of [[{ ...pins[0], name: 'DIRECT' }], [pins[0], pins[0]], [{ ...pins[0], password: 'fixture-only' }], [{ ...pins[0], port: 0 }]]) {
    assert.throws(() => applyClaudePolicy(fixture(), invalid));
  }
});
test('listener collision does not silently replace another listener', () => {
  const config = fixture(); config.listeners.push({ name: 'other', port: 17987 });
  assert.throws(() => applyClaudePolicy(config, pins));
});
test('regeneration is idempotent', () => {
  const first = applyClaudePolicy(fixture(), pins);
  assert.deepEqual(applyClaudePolicy(structuredClone(first), pins), first);
});
test('generated script runs without imports in a Verge-style VM', () => {
  const context = vm.createContext({}); vm.runInContext(renderExtension(pins), context);
  assert.deepEqual(JSON.parse(JSON.stringify(context.main(fixture(), 'fixture'))), applyClaudePolicy(fixture(), pins));
});
