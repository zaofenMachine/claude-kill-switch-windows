import hashlib
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PolicyHashTests(unittest.TestCase):
    def test_all_browser_policy_guards_match_exact_template_bytes(self):
        expected = hashlib.sha256((ROOT / 'templates/firefox-policies.json').read_bytes()).hexdigest().upper()
        source = (ROOT / 'src/ClaudeSafeBrowser.cs').read_text(encoding='utf-8-sig')
        self.assertEqual(re.findall(r'PolicyHash = "([A-F0-9]{64})"', source), [expected])
        registration = (ROOT / 'scripts/Register-ClaudeBrowser.ps1').read_text(encoding='utf-8-sig')
        guards = re.findall(r"'([A-F0-9]{64})'", registration)
        self.assertEqual(guards, [expected, expected])


if __name__ == '__main__':
    unittest.main()
