"""Inspect exact staged/index contents, including in a clean CI checkout.

This is a small denylist, not a complete secret detector. Never print matched values.
"""
from pathlib import PurePosixPath
import re
import subprocess
import sys

paths = subprocess.check_output(['git', 'ls-files', '-z']).decode('utf-8').split('\0')
blocked_dirs = {'private', 'runtime', 'reports', 'backups', 'downloads', 'research', '.deps', 'node_modules', '__pycache__'}
blocked_suffixes = {'.exe', '.dll', '.zip', '.7z', '.msix', '.msixbundle', '.pfx', '.p12', '.pem', '.key', '.db', '.sqlite', '.png', '.jpg', '.log'}
patterns = {
    'personal-windows-path': re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+', re.I),
    'github-token': re.compile(r'(?:gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{25,})'),
    'api-secret': re.compile(r'sk-(?:ant-)?[A-Za-z0-9_-]{25,}'),
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'credential-in-url': re.compile(r'https?://[^\s/]+:[^\s/@]+@'),
    'subscription-token-query': re.compile(r'https?://[^\s"<>]+[?&](?:token|access_token|api_key)=[A-Za-z0-9_-]{12,}', re.I),
}
failures = []
count = 0
for name in paths:
    if not name:
        continue
    count += 1
    path = PurePosixPath(name)
    if any(part in blocked_dirs for part in path.parts) or path.suffix.lower() in blocked_suffixes or path.name.startswith('.env') or '.local.' in path.name:
        failures.append((name, 'private-or-binary-artifact'))
        continue
    raw = subprocess.check_output(['git', 'show', ':' + name])
    try:
        text = raw.decode('utf-8-sig')
    except UnicodeDecodeError:
        failures.append((name, 'non-text-file'))
        continue
    for label, pattern in patterns.items():
        if pattern.search(text):
            failures.append((name, label))
for name, label in failures:
    print(f'{name}: {label}')
print(f'Checked {count} tracked files; findings: {len(failures)}. Manual review is also required.')
sys.exit(1 if failures else 0)
