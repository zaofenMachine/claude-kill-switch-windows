import {test} from 'node:test';
import assert from 'node:assert/strict';
import {applyClaudePolicy} from '../src/allowlist.mjs';
import {detachVerge} from '../src/detach-verge.mjs';
const base = {mode:'rule', tun:{enable:true},'mixed-port':7897,proxies:[],rules:['MATCH,Other'],listeners:[{name:'other',port:2222}], 'proxy-groups':[{name:'Other',type:'select',proxies:['DIRECT']}]};
test('detach preserves unrelated configuration and never mutates input', () => {
  const source = applyClaudePolicy(structuredClone(base), []);
  const snapshot = structuredClone(source);
  assert.deepEqual(detachVerge(source),base);
  assert.deepEqual(source,snapshot);
});
test('unknown group reference prevents migration', () => {
  const source=applyClaudePolicy(structuredClone(base),[]);
  source.rules.unshift('DOMAIN,custom.example,Claude-Safe');
  assert.throws(()=>detachVerge(source));
  source.rules.shift(); source['proxy-groups'][1].proxies.push('Claude-Safe');
  assert.throws(()=>detachVerge(source));
});
test('altered or duplicate dedicated listeners are refused', () => {
  for(const change of [c=>c.listeners.at(-1).udp=true,c=>c.listeners.push({...c.listeners.at(-1)}),c=>c.listeners.at(-1).listen='0.0.0.0']) {
    const source=applyClaudePolicy(structuredClone(base),[]);change(source);assert.throws(()=>detachVerge(source));
  }
});
