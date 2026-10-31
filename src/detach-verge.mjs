// Remove only the exact objects owned by the former Claude extension.
// No filesystem or network operations. Unknown references require manual review.
export function detachVerge(input) {
  const config = structuredClone(input);
  const group = 'Claude-Safe';
  const domains = ['claude.ai', 'claude.com', 'anthropic.com', 'claudeusercontent.com', 'claudemcpcontent.com', 'claude.app'];
  const ownedRules = new Set(domains.map(d => `DOMAIN-SUFFIX,${d},${group}`));
  if (!Array.isArray(config.listeners) || !Array.isArray(config['proxy-groups']) || !Array.isArray(config.rules)) throw new Error('Unexpected source configuration.');
  const listeners = config.listeners.filter(l => l.name === 'claude-safe');
  if (listeners.length !== 1 || listeners[0].type !== 'mixed' || listeners[0].listen !== '127.0.0.1' || listeners[0].port !== 17987 || listeners[0].proxy !== group || listeners[0].udp !== false) throw new Error('Dedicated listener differs from audited ownership.');
  if (config.listeners.some(l => l.name !== 'claude-safe' && l.port === 17987)) throw new Error('Dedicated port has another owner.');
  const groups = config['proxy-groups'].filter(g => g.name === group);
  if (groups.length !== 1 || groups[0].type !== 'select' || !groups[0].proxies.includes('REJECT')) throw new Error('Dedicated group differs from audited ownership.');
  config.listeners = config.listeners.filter(l => l.name !== 'claude-safe');
  config['proxy-groups'] = config['proxy-groups'].filter(g => g.name !== group);
  config.rules = config.rules.filter(r => !ownedRules.has(r));
  // Any remaining nested reference is outside the objects this tool owns.
  function referenced(value) {
    if (typeof value === 'string') return value === group || value.split(',').map(s => s.trim()).includes(group);
    if (Array.isArray(value)) return value.some(referenced);
    return value && typeof value === 'object' && Object.entries(value).some(([k,v]) => k === group || referenced(v));
  }
  if (referenced(config)) throw new Error('Other rules or groups still reference Claude-Safe. Review locally.');
  return config;
}
