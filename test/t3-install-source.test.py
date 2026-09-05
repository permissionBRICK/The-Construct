"""Run the actual patched-install selection with mocked build/download boundaries."""
from pathlib import Path
import subprocess
import unittest

source = (Path(__file__).resolve().parents[1] / 'bin/install-ai-tools.sh').read_text()
start = source.index('install_t3code() {')
end = source.index('  # Resolve the installed binary', start)
function = source[start:end] + '}\n'
default = next(line for line in source.splitlines() if line.startswith('T3CODE_BUILD_SOURCE='))


class Selection(unittest.TestCase):
    def run_case(self, channel='stable', saved='', override='', fail=False):
        script = '''set -eu
step() { :; }
err() { echo "$*" >&2; }
python3() { echo prebuilt; return "$FAIL"; }
env() { echo local; }
T3CODE_LIMIT_RESUME=true
REPO_DIR=/fixture
''' + default + '\n' + function + '\ninstall_t3code\n'
        return subprocess.run(['bash', '-c', script], text=True, capture_output=True, env={
            'PATH': '/usr/bin:/bin', 'T3CODE_CHANNEL': channel, 'T3CODE_BUILD_SOURCE': saved,
            '_t3_build_source_override': override, 'FAIL': str(int(fail)),
        })

    def test_stable_defaults_to_prebuilt(self):
        result = self.run_case()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('prebuilt', result.stdout.strip())

    def test_saved_local_and_explicit_override(self):
        self.assertEqual('local', self.run_case(saved='local').stdout.strip())
        self.assertEqual('prebuilt', self.run_case(saved='local', override='prebuilt').stdout.strip())
        self.assertEqual('local', self.run_case(saved='prebuilt', override='local').stdout.strip())

    def test_nightly_remains_local(self):
        self.assertEqual('local', self.run_case(channel='nightly').stdout.strip())

    def test_download_failure_never_falls_back_to_compilation(self):
        result = self.run_case(fail=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('prebuilt', result.stdout.strip())

    def test_invalid_preference_fails(self):
        result = self.run_case(saved='typo')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('', result.stdout)

if __name__ == '__main__':
    unittest.main()
