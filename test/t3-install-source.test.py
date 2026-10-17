"""Run the actual patched-install selection with mocked build/download boundaries."""
from pathlib import Path
import json
import os
import subprocess
import tempfile
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
T3CODE_PATCHED=true
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

    def test_nightly_defaults_to_prebuilt(self):
        self.assertEqual('prebuilt', self.run_case(channel='nightly').stdout.strip())

    def test_download_failure_never_falls_back_to_compilation(self):
        result = self.run_case(fail=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('prebuilt', result.stdout.strip())

    def test_invalid_preference_fails(self):
        result = self.run_case(saved='typo')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('', result.stdout)


class NativeSettings(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.settings = self.root / 'home/userdata/settings.json'
        self.marker = self.root / 'etc/t3-native-limit-resume'
        self.config = self.root / 'config.env'
        self.config.write_text('')

    def run_migration(self, patched='true', skip=False):
        installer = Path(__file__).resolve().parents[1] / 'bin/install-ai-tools.sh'
        script = 'source "$INSTALLER"\n'
        if skip:
            # Run the actual migration call and unchanged-build branch with its
            # filesystem reads redirected and external actions stubbed.
            begin = source.index('  migrate_t3_native_limit_resume\n', source.index('install_t3code() {'))
            end = source.index('  elif [[ "${_t3_stock_unchanged}"', begin)
            branch = source[begin:end].replace('/etc/construct', str(self.root / 'etc'))
            (self.root / 'etc').mkdir(exist_ok=True)
            (self.root / 'etc/t3code-desktop-status').write_text('T3CODE_BUILD_KEY=same\n')
            (self.root / 'etc/t3code-installed-build').write_text('same\n')
            script += '''systemctl() { return 0; }
prune_t3_installs() { :; }
_t3_pub_before=''; _t3_pub_after=''
unchanged_build() {
''' + branch + '\n  fi\n  echo UNEXPECTED_RESTART\n}\nunchanged_build\n'
        else:
            script += 'migrate_t3_native_limit_resume\n'
        return subprocess.run(['bash', '-eu', '-c', script], capture_output=True, text=True, env={
            'PATH': os.environ['PATH'], 'INSTALLER': str(installer),
            'CONSTRUCT_AI_TOOLS_FUNCS_ONLY': 'true', 'CONFIG_FILE': str(self.config),
            'T3CODE_PATCHED': patched, 'T3CODE_HOME': str(self.root / 'home'),
            'T3CODE_NATIVE_LIMIT_RESUME_MARKER': str(self.marker),
        }, check=True)

    def test_merges_once_and_respects_later_owner_change(self):
        self.settings.parent.mkdir(parents=True)
        self.settings.write_text('{"theme":"dark","nested":{"keep":42},"snoozeLimitedThreads":false}')
        self.run_migration()
        data = json.loads(self.settings.read_text())
        self.assertEqual({'theme': 'dark', 'nested': {'keep': 42},
                          'autoResumeLimitedThreads': True, 'snoozeLimitedThreads': True}, data)
        self.assertTrue(self.marker.exists())
        data['autoResumeLimitedThreads'] = False
        self.settings.write_text(json.dumps(data))
        before = self.settings.read_bytes()
        self.run_migration()
        self.assertEqual(before, self.settings.read_bytes())
        self.assertEqual([self.settings], list(self.settings.parent.iterdir()))

    def test_missing_settings_created(self):
        self.run_migration()
        self.assertEqual({'autoResumeLimitedThreads': True, 'snoozeLimitedThreads': True},
                         json.loads(self.settings.read_text()))
        self.assertTrue(self.marker.exists())

    def test_invalid_json_untouched_and_retried(self):
        self.settings.parent.mkdir(parents=True)
        self.settings.write_text('{broken')
        result = self.run_migration()
        self.assertIn('WARNING', result.stderr)
        self.assertEqual('{broken', self.settings.read_text())
        self.assertFalse(self.marker.exists())
        self.settings.write_text('{}')
        self.run_migration()
        self.assertTrue(self.marker.exists())

    def test_stock_untouched(self):
        self.run_migration(patched='false')
        self.assertFalse(self.settings.exists())
        self.assertFalse(self.marker.exists())

    def test_unchanged_build_path_runs_migration(self):
        result = self.run_migration(skip=True)
        self.assertIn('skipping its reinstall/restart', result.stdout)
        self.assertNotIn('UNEXPECTED_RESTART', result.stdout)
        self.assertTrue(self.marker.exists())

    def test_legacy_preference_and_new_precedence(self):
        helper = Path(__file__).resolve().parents[1] / 'bin/t3code-patched.sh'
        def resolve(saved='', **environment):
            self.config.write_text(saved)
            return subprocess.check_output(['bash', '-eu', '-c',
                'source "$HELPER"; resolve_t3code_patched "$CONFIG_FILE"'], text=True,
                env={'PATH': os.environ['PATH'], 'HELPER': str(helper),
                     'CONFIG_FILE': str(self.config), **environment}).strip()
        self.assertEqual('true', resolve('T3CODE_LIMIT_RESUME=true\n'))
        self.assertEqual('false', resolve('T3CODE_LIMIT_RESUME=false\n'))
        self.assertEqual('true', resolve(T3CODE_LIMIT_RESUME='true'))
        self.assertEqual('false', resolve('T3CODE_PATCHED=false\nT3CODE_LIMIT_RESUME=true\n'))
        self.assertEqual('false', resolve('T3CODE_PATCHED=false\n', T3CODE_LIMIT_RESUME='true'))
        self.assertEqual('false', resolve(T3CODE_PATCHED='false', T3CODE_LIMIT_RESUME='true'))
        self.assertEqual('true', resolve('T3CODE_PATCHED=false\n', T3CODE_PATCHED='true'))

    def test_installer_keeps_resolved_preference_when_loading_saved_config(self):
        installer = Path(__file__).resolve().parents[1] / 'bin/install-ai-tools.sh'
        for saved, environment, expected in [
            ('T3CODE_LIMIT_RESUME=true\n', {}, 'true'),
            ('T3CODE_PATCHED=false\n', {'T3CODE_LIMIT_RESUME': 'true'}, 'false'),
            ('T3CODE_PATCHED=true\n', {'T3CODE_PATCHED': 'false'}, 'false'),
        ]:
            self.config.write_text('AI_TOOLS=\n' + saved)
            result = subprocess.check_output(['bash', '-eu', '-c',
                'source "$INSTALLER"; printf "%s" "$T3CODE_PATCHED"'], text=True,
                env={'PATH': os.environ['PATH'], 'INSTALLER': str(installer),
                     'CONFIG_FILE': str(self.config), 'AI_CONSOLE_INTEGRATION': 'false', **environment})
            self.assertEqual(expected, result)

    def test_provision_rewrite_drops_legacy_line(self):
        repo = Path(__file__).resolve().parents[1]
        provision = (repo / 'bin/provision.sh').read_text()
        begin = provision.index('  cfg T3CODE_PATCHED ')
        end = provision.index('  cfg OPENCODE_BACKGROUND_WATCHER', begin)
        self.config.write_text('KEEP=value\nT3CODE_LIMIT_RESUME=true\n')
        subprocess.run(['bash', '-eu', '-c', '''
cfg() { bash "$REPO/bin/config-set.sh" "$CONFIG_FILE" "$1" "$2"; }
rewrite() {
''' + provision[begin:end] + '}\nrewrite\n'], check=True,
            env={'PATH': os.environ['PATH'], 'REPO': str(repo), 'CONFIG_FILE': str(self.config), 'T3CODE_PATCHED': 'true'})
        self.assertEqual('KEEP=value\nT3CODE_PATCHED=true\n', self.config.read_text())

    def test_vm_probe_reads_legacy_preference(self):
        repo = Path(__file__).resolve().parents[1]
        # Redirect all Construct reads and stub the CLI/service commands so the
        # actual probe never contacts an installed agent or a running server.
        script = (repo / 'extension/vm/probe.sh').read_text().replace('/etc/construct', str(self.root))
        stub = self.root / 'bin'
        stub.mkdir()
        for command in ('t3', 'claude', 'codex', 'opencode', 'systemctl'):
            path = stub / command
            path.write_text('#!/bin/sh\nexit 0\n')
            path.chmod(0o755)
        for saved, expected in [('T3CODE_LIMIT_RESUME=true\n', 'true'),
                                ('T3CODE_PATCHED=false\nT3CODE_LIMIT_RESUME=true\n', 'false'),
                                ('', '')]:
            self.config.write_text(saved)
            result = subprocess.check_output(['sh'], input=script, text=True,
                env={'PATH': str(stub) + ':/usr/bin:/bin'})
            self.assertIn('T3CODE_PATCHED\t' + expected, result.splitlines())

if __name__ == '__main__':
    unittest.main()
