"""Verify a simplewall update preserves every original filter and adds only
exact app-scoped copies of an already audited reference application's blocks.
"""
import collections
import copy
import json
from pathlib import Path
import sys
from compare_wfp import owned, signature


def app_id(f):
    conditions = [c for c in f.findall('filterCondition/item') if c.findtext('fieldKey') == 'FWPM_CONDITION_ALE_APP_ID']
    if len(conditions) != 1 or conditions[0].findtext('matchType') != 'FWP_MATCH_EQUAL':
        return None
    return (conditions[0].findtext('.//asString') or '').lower()


def shape(f):
    f = copy.deepcopy(f)
    container = f.find('filterCondition')
    for condition in list(container):
        if condition.findtext('fieldKey') == 'FWPM_CONDITION_ALE_APP_ID': container.remove(condition)
    return signature(f)


def verify(before, after, reference_app, added_apps):
    added = {p.lower() for p in added_apps}
    if not added or len(added) != len(added_apps): raise ValueError('Distinct added app IDs required.')
    if any(app_id(f) in added for f in before): raise ValueError('An added target already exists in baseline.')
    reference = [f for f in before if app_id(f) == reference_app.lower()]
    if not reference or any(f.findtext('action/type') != 'FWP_ACTION_BLOCK' or
                           'FWPM_FILTER_FLAG_PERSISTENT' not in [n.text for n in f.findall('flags/item')] for f in reference):
        raise ValueError('Reference must contain audited persistent blocks only.')
    original_after = [f for f in after if app_id(f) not in added]
    preserved = collections.Counter(map(signature, before)) == collections.Counter(map(signature, original_after))
    expected = collections.Counter(map(shape, reference))
    details = []
    for target in sorted(added):
        filters = [f for f in after if app_id(f) == target]
        details.append({'matchesReference': collections.Counter(map(shape, filters)) == expected, 'blockCount': len(filters)})
    return {'ok': preserved and all(d['matchesReference'] for d in details),
            'originalFiltersPreserved': preserved, 'addedTargets': len(added),
            'referenceBlockCount': len(reference), 'addedFilterResults': details}


if __name__ == '__main__':
    if len(sys.argv) != 4: raise SystemExit('Usage: verify_wfp_extension.py before.xml after.xml local-spec.json')
    try:
        spec = json.loads(Path(sys.argv[3]).read_text(encoding='utf-8-sig'))
        result = verify(owned(sys.argv[1]), owned(sys.argv[2]), spec['referenceAppId'], spec['addedAppIds'])
    except Exception:
        raise SystemExit('WFP extension verification failed; inspect local snapshots, no raw app IDs printed.')
    print(json.dumps(result))
    sys.exit(0 if result['ok'] else 1)
