import importlib.util
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


prepare = load('prepare_firewall')
audit = load('compare_wfp')


class FirewallTests(unittest.TestCase):
    def test_no_global_rules_and_port_complement(self):
        targets = [r'C:\Apps\Protected\client.exe', r'C:\Apps\Browser\firefox.exe']
        root = prepare.build_profile(targets)
        rules = root.findall('rules_custom/item')
        self.assertEqual(len(rules), 4)
        for rule in rules:
            self.assertEqual(rule.attrib['apps'], '|'.join(targets))
            self.assertEqual(rule.attrib['is_block'], 'true')
            self.assertEqual(rule.attrib['dir'], '0')
        self.assertEqual(rules[0].attrib['rule'], '0.0.0.0-127.0.0.0;127.0.0.2-255.255.255.255')
        self.assertEqual(rules[1].attrib['rule'], '::/0')
        self.assertEqual(rules[2].attrib['rule'], '1-17986;17988-65535')
        self.assertEqual(rules[3].attrib['protocol'], '17')

    def test_unreviewed_or_ambiguous_targets_rejected(self):
        for value in [[], ['client.exe'], [r'C:\Apps\*.exe'], [r'C:\Apps\a.exe|b.exe'],
                      [r'C:\Apps\a.exe', r'c:\apps\A.exe'], [None]]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                prepare.build_profile(value)

    def filter(self):
        return ET.fromstring('<item><filterId>1</filterId><filterKey>old</filterKey><providerData>opaque</providerData>'
                             '<flags><item>FWPM_FILTER_FLAG_PERSISTENT</item></flags><action><type>FWP_ACTION_BLOCK</type></action>'
                             '<filterCondition><item><fieldKey>FWPM_CONDITION_ALE_APP_ID</fieldKey>'
                             '<matchType>FWP_MATCH_EQUAL</matchType><conditionValue>fixture</conditionValue></item></filterCondition></item>')

    def test_recreated_ids_are_not_treated_as_condition_changes(self):
        before, after = self.filter(), self.filter()
        after.find('filterId').text = '99'
        after.find('filterKey').text = 'new'
        after.find('providerData').text = 'different opaque data'
        self.assertTrue(audit.compare([before], [after])['ok'])

    def test_changed_conditions_and_lost_persistence_fail(self):
        for field in ['filterCondition/item/conditionValue', 'flags/item']:
            before, after = self.filter(), self.filter()
            after.find(field).text = 'changed'
            self.assertFalse(audit.compare([before], [after])['ok'])

    def test_empty_baseline_is_not_a_pass(self):
        self.assertFalse(audit.compare([], [])['ok'])


if __name__ == '__main__':
    unittest.main()
