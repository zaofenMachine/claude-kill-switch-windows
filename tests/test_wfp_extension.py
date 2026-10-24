import importlib.util
from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from verify_wfp_extension import verify


def item(app, identity='1', port='1'):
    return ET.fromstring('<item><filterId>'+identity+'</filterId><filterKey>fixture</filterKey>'
        '<providerData>opaque</providerData><flags><item>FWPM_FILTER_FLAG_PERSISTENT</item></flags>'
        '<action><type>FWP_ACTION_BLOCK</type></action><filterCondition><item>'
        '<fieldKey>FWPM_CONDITION_ALE_APP_ID</fieldKey><matchType>FWP_MATCH_EQUAL</matchType>'
        '<conditionValue><asString>'+app+'</asString></conditionValue></item><item>'
        '<fieldKey>FWPM_CONDITION_IP_REMOTE_PORT</fieldKey><matchType>FWP_MATCH_EQUAL</matchType>'
        '<conditionValue><uint16>'+port+'</uint16></conditionValue></item></filterCondition></item>')


class ExtensionTests(unittest.TestCase):
    def test_only_exact_app_copies_are_accepted(self):
        self.assertTrue(verify([item('probe')], [item('probe', '2'), item('engine', '3')], 'probe', ['engine'])['ok'])

    def test_changed_original_rule_is_refused(self):
        self.assertFalse(verify([item('probe')], [item('probe', port='2'), item('engine')], 'probe', ['engine'])['ok'])

    def test_missing_or_different_added_rules_are_refused(self):
        for extra in [[], [item('engine', port='2')], [item('engine'), item('engine')]]:
            self.assertFalse(verify([item('probe')], [item('probe')]+extra, 'probe', ['engine'])['ok'])

    def test_unexpected_target_is_refused(self):
        self.assertFalse(verify([item('probe')], [item('probe'), item('engine'), item('unapproved')], 'probe', ['engine'])['ok'])

    def test_global_rule_is_refused(self):
        extra=item('engine'); extra.find('filterCondition').remove(extra.find('filterCondition/item'))
        self.assertFalse(verify([item('probe')], [item('probe'), extra], 'probe', ['engine'])['ok'])

    def test_unreviewed_reference_and_existing_target_are_refused(self):
        bad=item('probe');bad.find('flags/item').text='DYNAMIC'
        with self.assertRaises(ValueError):verify([bad], [bad,item('engine')], 'probe', ['engine'])
        with self.assertRaises(ValueError):verify([item('probe'),item('engine')], [item('probe'),item('engine')], 'probe', ['engine'])


if __name__=='__main__':unittest.main()
