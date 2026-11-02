"""Exercise the publication gate against real staged blobs in temporary repositories."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCANNER = Path(__file__).resolve().parents[1] / 'scripts' / 'check-publication.py'


class PublicationTests(unittest.TestCase):
    def scan(self, name, content='{}'):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(['git', 'init', '-q', directory], check=True, capture_output=True)
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding='utf-8')
            subprocess.run(['git', 'add', '--', name], cwd=root, check=True, capture_output=True)
            return subprocess.run([sys.executable, str(SCANNER)], cwd=root,
                                  text=True, capture_output=True)

    def test_manager_private_catalogs_blocked_even_outside_private_directory(self):
        for name in ('subscriptions.json', 'settings.json', 'source.json',
                     'desktop-targets.json', 'custom-data/SUBSCRIPTIONS.JSON'):
            with self.subTest(name=name):
                result = self.scan(name)
                self.assertEqual(result.returncode, 1)
                self.assertIn('private-or-binary-artifact', result.stdout)

    def test_benchmark_history_is_private(self):
        for name in ('custom-data/benchmarks/latest.json', 'custom-data/Benchmarks/latest.json'):
            with self.subTest(name=name):
                result = self.scan(name)
                self.assertEqual(result.returncode, 1)
                self.assertIn('private-or-binary-artifact', result.stdout)

    def test_source_documentation_can_name_private_files(self):
        result = self.scan('docs/subscriptions.md', 'Keep subscriptions.json and source.json locally.')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_finding_does_not_print_staged_secret_content(self):
        marker = 'fixture-sensitive-content'
        result = self.scan('subscriptions.json', marker)
        self.assertEqual(result.returncode, 1)
        self.assertNotIn(marker, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
