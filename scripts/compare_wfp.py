"""Compare owned filters against a baseline already audited on THIS machine."""
import collections
import copy
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

PROVIDER = 'b0d553e2-c6a0-4a9a-aeb8-c752483ed62f'


def owned(file):
    raw = Path(file).read_bytes()
    text = raw.decode('utf-16') if raw[:2] in (b'\xff\xfe', b'\xfe\xff') else raw.decode('utf-8-sig')
    root = ET.fromstring('<root>' + re.sub(r'<\?xml[^?]*\?>', '', text) + '</root>')
    return [f for f in root.findall('.//filters/item') if PROVIDER in (f.findtext('providerKey') or '').lower()]


def signature(item):
    item = copy.deepcopy(item)
    for name in ('filterKey', 'filterId', 'providerData'):
        child = item.find(name)
        if child is not None:
            item.remove(child)
    for child in item.iter():
        child.text = (child.text or '').strip()
        child.tail = ''
    return ET.tostring(item)


def compare(before, after):
    blocks = [f for f in after if f.findtext('action/type') == 'FWP_ACTION_BLOCK']
    scoped = all(any(c.findtext('fieldKey') == 'FWPM_CONDITION_ALE_APP_ID' for c in f.findall('filterCondition/item')) for f in blocks)
    persistent = all('FWPM_FILTER_FLAG_PERSISTENT' in [n.text for n in f.findall('flags/item')] for f in blocks)
    matches = collections.Counter(map(signature, before)) == collections.Counter(map(signature, after))
    return {'ok': bool(blocks) and scoped and persistent and matches, 'matchesBaseline': matches,
            'blockCount': len(blocks), 'allBlocksAppScoped': scoped, 'allBlocksPersistent': persistent}


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit('Usage: compare_wfp.py audited-baseline.xml current.xml')
    try:
        result = compare(owned(sys.argv[1]), owned(sys.argv[2]))
    except (OSError, UnicodeError, ET.ParseError):
        raise SystemExit('Cannot parse local WFP export; raw data was not printed.')
    print(json.dumps(result))
    sys.exit(0 if result['ok'] else 1)
