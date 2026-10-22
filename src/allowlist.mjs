// This function is also serialized into a standalone Clash Verge global script.
// Keep it self-contained: no imports, filesystem access, credentials, or network I/O.
export function applyClaudePolicy(config, pins) {
  const types = ['vless', 'vmess', 'trojan', 'ss', 'ssr', 'hysteria', 'hysteria2', 'tuic', 'anytls', 'socks5', 'http'];
  if (!config || typeof config !== 'object' || Array.isArray(config) || !Array.isArray(pins)) throw new Error('Invalid configuration.');
  const reserved = ['DIRECT', 'REJECT', 'REJECT-DROP', 'PASS', 'COMPATIBLE', 'GLOBAL', 'Claude-Safe'];
  const names = new Set();
  for (const pin of pins) {
    if (!pin || Object.keys(pin).sort().join(',') !== 'name,port,server,type' ||
        typeof pin.name !== 'string' || !pin.name.trim() || /[\x00-\x1f\x7f]/.test(pin.name) ||
        reserved.includes(pin.name) || names.has(pin.name) || !types.includes(pin.type) ||
        typeof pin.server !== 'string' || !pin.server.trim() || /[\s/\\]/.test(pin.server) ||
        !Number.isInteger(pin.port) || pin.port < 1 || pin.port > 65535) {
      throw new Error('Invalid or duplicate approved leaf identity; provide only name/type/server/port.');
    }
    names.add(pin.name);
  }
  for (const field of ['proxies', 'proxy-groups', 'listeners', 'rules']) {
    if (config[field] !== undefined && !Array.isArray(config[field])) throw new Error('Unexpected configuration array.');
  }
  const proxies = config.proxies || [];
  const groups = config['proxy-groups'] || [];
  const members = ['REJECT'];
  for (const pin of pins) {
    const sameName = proxies.filter(p => p.name === pin.name);
    if (sameName.length !== 1 || groups.some(g => g.name === pin.name)) continue;
    const p = sameName[0];
    if (p.type === pin.type && p.server === pin.server && Number(p.port) === pin.port && !p['dialer-proxy']) members.push(pin.name);
  }
  const listeners = (config.listeners || []).filter(l => l.name !== 'claude-safe');
  if (listeners.some(l => Number(l.port) === 17987)) throw new Error('Dedicated port is already used by another listener.');
  config['proxy-groups'] = [{ name: 'Claude-Safe', type: 'select', proxies: members,
    'default-selected': 'REJECT', 'empty-fallback': 'REJECT' }, ...groups.filter(g => g.name !== 'Claude-Safe')];
  config.listeners = [...listeners, { name: 'claude-safe', type: 'mixed', listen: '127.0.0.1', port: 17987, udp: false, proxy: 'Claude-Safe' }];
  const domains = ['claude.ai', 'claude.com', 'anthropic.com', 'claudeusercontent.com', 'claudemcpcontent.com', 'claude.app'];
  const ownedRules = domains.map(d => 'DOMAIN-SUFFIX,' + d + ',Claude-Safe');
  config.rules = [...ownedRules, ...(config.rules || []).filter(r => !ownedRules.includes(r))];
  return config;
}

export function renderExtension(pins) {
  applyClaudePolicy({}, pins); // Validate without contacting a proxy.
  const embedded = JSON.stringify(pins).replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
  return '// Generated locally. Contains private node identities; do not publish.\n' +
    applyClaudePolicy.toString() + '\nfunction main(config, profileName) {\n  return applyClaudePolicy(config, ' + embedded + ');\n}\n';
}
