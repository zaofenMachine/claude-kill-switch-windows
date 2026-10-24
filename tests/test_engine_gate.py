import importlib.util
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('engine_gate', Path(__file__).resolve().parents[1] / 'scripts/engine_gate.py')
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
ROOT = r'C:\Fixture\PackageCache\claude-code'
APP = ROOT + r'\1.2.3\build\claude.exe'
BASE = '<AppLockerPolicy Version="1"><RuleCollection Type="Appx" EnforcementMode="Enabled"><FilePublisherRule Id="fixture" /></RuleCollection></AppLockerPolicy>'


class EngineGateTests(unittest.TestCase):
    def test_existing_appx_collection_is_preserved(self):
        result = ET.fromstring(gate.build_gate(BASE, [ROOT], [APP]))
        original = ET.fromstring(BASE)
        for tree in [result, original]:
            for node in tree.iter():
                node.text = (node.text or '').strip(); node.tail = ''
        self.assertEqual(ET.tostring(result.find('RuleCollection')), ET.tostring(original.find('RuleCollection')))

    def test_deny_root_exact_exception_and_other_apps_allow(self):
        result = ET.fromstring(gate.build_gate(BASE, [ROOT], [APP]))
        rules = result.findall("RuleCollection[@Type='Exe']/FilePathRule")
        self.assertEqual(len(rules), 2)
        self.assertEqual(rules[0].attrib['Action'], 'Allow')
        self.assertEqual(rules[0].find('Conditions/FilePathCondition').attrib['Path'], '*')
        self.assertEqual(rules[1].attrib['Action'], 'Deny')
        self.assertEqual(rules[1].find('Conditions/FilePathCondition').attrib['Path'], ROOT + r'\*')
        self.assertEqual(rules[1].find('Exceptions/FilePathCondition').attrib['Path'], APP)

    def test_blocked_stage_has_no_release_exceptions(self):
        result = ET.fromstring(gate.build_gate(BASE, [ROOT], [APP], blocked=True))
        self.assertEqual(result.findall('.//Exceptions'), [])

    def test_logical_and_physical_roots_are_separately_gated(self):
        logical = r'C:\Fixture\Roaming\claude-code'
        result = ET.fromstring(gate.build_gate(BASE, [ROOT, logical], [APP]))
        denies = result.findall("RuleCollection[@Type='Exe']/FilePathRule[@Action='Deny']")
        self.assertEqual(len(denies), 2)
        self.assertIsNone(denies[1].find('Exceptions'))

    def test_existing_exe_collection_is_never_overwritten(self):
        for mode in ['Enabled', 'AuditOnly', 'NotConfigured']:
            xml = '<AppLockerPolicy Version="1"><RuleCollection Type="Exe" EnforcementMode="'+mode+'" /></AppLockerPolicy>'
            with self.assertRaises(ValueError): gate.build_gate(xml, [ROOT], [APP])

    def test_policy_comparison_ignores_order_but_detects_changed_exceptions(self):
        text=gate.build_gate(BASE, [ROOT], [APP])
        reordered=ET.fromstring(text);reordered[:]=list(reversed(list(reordered)))
        self.assertEqual(gate.policy_signature(text),gate.policy_signature(ET.tostring(reordered,encoding='unicode')))
        reordered.find("RuleCollection[@Type='Exe']/FilePathRule[@Action='Deny']/Exceptions/FilePathCondition").set('Path',APP+'-changed')
        self.assertNotEqual(gate.policy_signature(text),gate.policy_signature(ET.tostring(reordered,encoding='unicode')))

    def test_unsafe_paths_are_rejected(self):
        for roots, paths in [([ROOT[:3]], [APP]), ([ROOT], [r'C:\Other\claude.exe']), ([ROOT], [ROOT+r'\*\claude.exe']),
                             ([ROOT], [ROOT+r'\..\other.exe']), ([ROOT], [APP+' ']), ([ROOT, ROOT], [APP]),
                             ([ROOT], [APP, APP.upper()]), ([r'%APPDATA%\claude-code'], [APP])]:
            with self.subTest(roots=roots, paths=paths), self.assertRaises(ValueError): gate.build_gate(BASE, roots, paths)


if __name__ == '__main__': unittest.main()
