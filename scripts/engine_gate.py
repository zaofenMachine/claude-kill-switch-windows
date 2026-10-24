"""Prepare an EXE path gate for separately downloaded Desktop engines.

Never installs policy. Requires a current policy export; refuses an existing EXE
collection rather than weakening someone else's implicit-deny policy.
"""
import json
from pathlib import Path, PureWindowsPath
import re
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NAMESPACE = uuid.UUID('b6abb82c-9909-4f10-9eab-62d06203bd5f')


def policy_signature(xml):
    """Compare complete policies without relying on attribute/collection order."""
    root = ET.fromstring(xml)
    def normalize(node):
        node.text = (node.text or '').strip(); node.tail = ''
        attributes = sorted(node.attrib.items()); node.attrib.clear(); node.attrib.update(attributes)
        for child in node: normalize(child)
        node[:] = sorted(node, key=lambda child: ET.tostring(child))
    normalize(root)
    return ET.tostring(root)


def windows_path(value):
    if not isinstance(value, str) or not re.match(r'^[A-Za-z]:\\', value):
        raise ValueError('An absolute local Windows path is required.')
    p = PureWindowsPath(value)
    if any(c in value for c in '*?%|') or any(ord(c) < 32 for c in value):
        raise ValueError('Wildcards, variables and control characters are not accepted.')
    if any(part in ('.', '..') or part.endswith(('.', ' ')) or ':' in part for part in p.parts[1:]):
        raise ValueError('Ambiguous path.')
    return p


def validate(roots, approved):
    if not isinstance(roots, list) or not roots or not isinstance(approved, list):
        raise ValueError('Explicit engine roots and approved executable paths required.')
    roots = [windows_path(p) for p in roots]
    approved = [windows_path(p) for p in approved]
    if len(set(roots)) != len(roots) or len(set(approved)) != len(approved):
        raise ValueError('Duplicate roots or approved paths.')
    if any(p.name.lower() != 'claude-code' or len(p.parts) < 4 for p in roots):
        raise ValueError('Gate roots must be narrowly scoped claude-code directories.')
    if any(p.suffix.lower() != '.exe' or sum(root in p.parents for root in roots) != 1 for p in approved):
        raise ValueError('Every approved executable must belong to exactly one engine root.')
    if any(a in b.parents or b in a.parents for i, a in enumerate(roots) for b in roots[i+1:]):
        raise ValueError('Overlapping roots are ambiguous.')
    return roots, approved


def build_gate(existing_xml, roots, approved, blocked=False):
    roots, approved = validate(roots, approved)
    policy = ET.fromstring(existing_xml)
    if policy.tag != 'AppLockerPolicy' or any(c.attrib.get('Type') == 'Exe' for c in policy.findall('RuleCollection')):
        raise ValueError('Existing EXE policy must be reviewed/merged manually.')
    collection = ET.SubElement(policy, 'RuleCollection', {'Type': 'Exe', 'EnforcementMode': 'Enabled'})
    def rule(name, action, condition_path, exceptions=()):
        rule_id = str(uuid.uuid5(NAMESPACE, name + '\n' + condition_path.lower()))
        item = ET.SubElement(collection, 'FilePathRule', {'Id': rule_id, 'Name': name,
            'Description': 'ClaudeKS engine path gate; exact exceptions require installed WFP protection',
            'UserOrGroupSid': 'S-1-1-0', 'Action': action})
        conditions = ET.SubElement(item, 'Conditions')
        ET.SubElement(conditions, 'FilePathCondition', {'Path': condition_path})
        if exceptions:
            container = ET.SubElement(item, 'Exceptions')
            for path in exceptions:
                ET.SubElement(container, 'FilePathCondition', {'Path': str(path)})
    # Safe only because there was NO existing EXE collection; don't broaden an existing policy.
    rule('ClaudeKS: preserve other executables', 'Allow', '*')
    for root in roots:
        rule('ClaudeKS: block unprotected engine paths', 'Deny', str(root / '*'),
             [] if blocked else [p for p in approved if root in p.parents])
    ET.indent(policy)
    return ET.tostring(policy, encoding='unicode')


def main():
    request = json.loads((ROOT / 'private/engine-approval.json').read_text(encoding='utf-8-sig'))
    existing = (ROOT / 'private/applocker-before.xml').read_text(encoding='utf-8-sig')
    effective = (ROOT / 'private/applocker-effective-before.xml').read_text(encoding='utf-8-sig')
    if policy_signature(existing) != policy_signature(effective):
        raise ValueError('Local and effective policies differ; managed policy requires separate review.')
    roots, approved = request['roots'], request['approvedExecutables']
    if not approved or any(not Path(p).is_file() for p in approved):
        raise ValueError('Review at least one existing executable before generating release exceptions.')
    out = ROOT / 'private/generated'
    out.mkdir(parents=True, exist_ok=True)
    for blocked, name in [(True, 'engine-gate-blocked.xml'), (False, 'engine-gate-approved.xml')]:
        (out / name).write_text(build_gate(existing, roots, approved, blocked), encoding='utf-8')
    print(json.dumps({'prepared': True, 'installed': False, 'approvedCount': len(approved)}))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, TypeError, ET.ParseError):
        raise SystemExit('Gate generation refused. Review roots, actual executable paths and existing EXE policies locally.')
