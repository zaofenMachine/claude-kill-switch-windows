"""Generate a simplewall staging profile only. Never install WFP filters."""
import json
import os
from pathlib import Path, PureWindowsPath
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SETTINGS = {
    'BlockOutboundConnections': 'false', 'BlockInboundConnections': 'false',
    'UseStealthMode': 'false', 'AllowLoopbackConnections': 'false', 'AllowIPv6': 'false',
    'InstallBoottimeFilters': 'false', 'IsInternalRulesDisabled': 'true',
    'BlocklistSpyState': '0', 'BlocklistUpdateState': '0', 'BlocklistExtraState': '0',
    'IsNotificationsEnabled': 'false', 'IsCheckUpdates': 'false', 'IsStartMinimized': 'true', 'ProfileType': '0',
}


def build_profile(targets):
    if not isinstance(targets, list) or not targets:
        raise ValueError('Review a nonempty target list first.')
    if any(not isinstance(p, str) or not PureWindowsPath(p).is_absolute() or
           not p.lower().endswith('.exe') or '|' in p or '*' in p or '?' in p or
           any(ord(c) < 32 for c in p) for p in targets):
        raise ValueError('Targets must be exact absolute Windows executable paths.')
    if len({p.lower() for p in targets}) != len(targets):
        raise ValueError('Duplicate targets.')
    root = ET.Element('root', {'version': '5', 'type': '3'})
    apps = ET.SubElement(root, 'apps')
    for target in targets:
        ET.SubElement(apps, 'item', {'path': target, 'is_enabled': 'false', 'is_silent': 'true'})
    ET.SubElement(root, 'rules_config')
    rules = ET.SubElement(root, 'rules_custom')
    definitions = [
        ('IPv4 except local proxy address', '0.0.0.0-127.0.0.0;127.0.0.2-255.255.255.255', 0, 2),
        ('IPv6 all', '::/0', 0, 23),
        ('TCP except dedicated port', '1-17986;17988-65535', 6, 0),
        ('UDP all', '', 17, 0),
    ]
    for name, remote, protocol, family in definitions:
        ET.SubElement(rules, 'item', {'name': 'ClaudeKS: ' + name, 'rule': remote, 'dir': '0',
            'protocol': str(protocol), 'version': str(family), 'is_block': 'true',
            'is_enabled': 'true', 'apps': '|'.join(targets)})
    ET.indent(root)
    return root


def main():
    if os.name != 'nt':
        raise ValueError('Generate deployment profiles on Windows.')
    targets = json.loads((ROOT / 'private/targets.json').read_text(encoding='utf-8-sig'))['targets']
    root = build_profile(targets)
    if any(not Path(p).is_file() for p in targets):
        raise ValueError('A reviewed target executable is missing.')
    output = ROOT / 'private/generated/simplewall'
    output.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(root).write(output / 'profile.xml', encoding='utf-8', xml_declaration=True)
    (output / 'simplewall.ini').write_text('[simplewall]\r\n' + '\r\n'.join(f'{k}={v}' for k, v in SETTINGS.items()) + '\r\n', encoding='utf-16', newline='')
    (output / 'plan.json').write_text(json.dumps({'targets': targets, 'allowedEndpoint': '127.0.0.1:17987', 'ruleCount': 4, 'activated': False}, indent=2), encoding='utf-8')
    print(json.dumps({'generated': True, 'targetCount': len(targets), 'rules': 4, 'activated': False}))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, TypeError):
        raise SystemExit('Generation failed. Review private/targets.json and existing executable paths; no filters installed.')
