#!/usr/bin/env python3
"""Local-only project command concurrency checks. No provisioning or downloads.
Run: python3 test/provision-commands.test.py
"""
import json
import os
import re
from pathlib import Path
import shlex
import signal
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNNER = ROOT / 'bin/run-provision-commands.sh'


class ProvisionCommandsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='construct-commands-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.workspace = self.root / 'repos'
        self.workspace.mkdir()
        self.agent = self.root / 'agent'
        (self.agent / 'runtime').mkdir(parents=True)
        self.generated = self.agent / 'runtime/generated.json'
        self.events = self.root / 'events.jsonl'
        self.events.touch()
        config = self.root / 'config.env'
        config.write_text(f'AGENT_HOME={shlex.quote(str(self.agent))}\n'
                          f'WORKSPACE_ROOT={shlex.quote(str(self.workspace))}\n')
        self.apt_conf = self.root / 'apt.conf.d'
        self.env = dict(os.environ, CONFIG_FILE=str(config), TEST_EVENTS=str(self.events),
                        REPO_DIR=str(ROOT), ALLOW_HOST_PACKAGES='false', PROVISION_JOBS='0',
                        CONSTRUCT_APT_CONF_DIR=str(self.apt_conf))
        self.helper = self.root / 'record.py'
        self.helper.write_text('''import json, os, sys, time
from pathlib import Path
label, duration = sys.argv[1:]
def record(kind):
    fd = os.open(os.environ['TEST_EVENTS'], os.O_WRONLY | os.O_APPEND)
    os.write(fd, (json.dumps(dict(kind=kind, label=label, pid=os.getpid(), cwd=os.getcwd(),
        projects=os.environ['AGENT_PROJECTS'], repos=json.loads(os.environ['AGENT_REPOS_JSON'])))+'\\n').encode())
    os.close(fd)
record('start')
time.sleep(float(duration))
record('end')
''')

    def command(self, label, duration=.15):
        return shlex.join(['python3', str(self.helper), label, str(duration)])

    def plan(self, profiles):
        commands = []
        for name, directory, items in profiles:
            if directory and directory != 'missing':
                (self.workspace / directory).mkdir(parents=True, exist_ok=True)
            for cmd in items:
                entry = dict(dir=directory, command=cmd)
                if name is not None:
                    entry['profile'] = name
                commands.append(entry)
        self.generated.write_text(json.dumps(dict(projects=['one', 'two'], repos=[], sdks={},
                                                   provisionCommands=commands)))

    def run_commands(self, jobs=0, expected=0):
        self.events.write_text('')
        result = subprocess.run(['bash', str(RUNNER)], env=dict(self.env, PROVISION_JOBS=str(jobs)),
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        return result.stdout + result.stderr

    def records(self):
        return [json.loads(line) for line in self.events.read_text().splitlines()]

    def peak(self):
        active, peak = set(), 0
        for event in self.records():
            if event['kind'] == 'start':
                active.add(event['label'])
            else:
                active.remove(event['label'])
            peak = max(peak, len(active))
        self.assertFalse(active, 'runner returned before commands finished')
        return peak

    def test_apt_waits_for_the_shared_dpkg_lock_during_parallel_groups(self):
        self.plan([('one', 'a', [self.command('a1')])])
        self.run_commands()
        self.assertEqual((self.apt_conf / '90construct-lock-timeout').read_text(), 'DPkg::Lock::Timeout "900";\n')

    def test_parallel_profiles_keep_internal_order_and_environment(self):
        self.plan([(p, p, [self.command(p+'1', .4), self.command(p+'2')]) for p in ['a', 'b']])
        self.run_commands()
        self.assertEqual(self.peak(), 2)
        for profile in ['a', 'b']:
            events = [e for e in self.records() if e['label'].startswith(profile)]
            self.assertEqual([(e['label'], e['kind']) for e in events],
                             [(profile+'1', 'start'), (profile+'1', 'end'),
                              (profile+'2', 'start'), (profile+'2', 'end')])
            self.assertTrue(all(e['cwd'] == str(self.workspace / profile) for e in events))
            self.assertTrue(all(e['projects'] == 'one,two' and e['repos'] == [] for e in events))

    def test_command_numbers_follow_completion_order(self):
        # Plan order: slow1, slow2, fast. The fast group finishes first and must print as 1/3;
        # the slow group's two commands follow as 2/3 and 3/3, never their plan positions.
        self.plan([('slow', 'slow', [self.command('slow1', .6), self.command('slow2')]),
                   ('fast', 'fast', [self.command('fast')])])
        output = re.sub(r'\x1b\[[0-9;]*m', '', self.run_commands())
        self.assertEqual(re.findall(r'\[(\d+)/3\]', output), ['1', '1', '2', '2', '3', '3'])
        self.assertLess(output.index('[1/3]'), output.index('slow1'))

    def test_concurrency_limit_and_sequential_profile_order(self):
        self.plan([(p, p, [self.command(p, .3)]) for p in ['z', 'b', 'a']])
        self.run_commands(jobs=2)
        self.assertEqual(self.peak(), 2)
        self.run_commands(jobs=1)
        self.assertEqual(self.peak(), 1)
        self.assertEqual([e['label'] for e in self.records() if e['kind'] == 'start'], ['z', 'b', 'a'])

    def test_failure_continues_same_profile_and_other_profiles(self):
        self.plan([('a', 'a', ['exit 42', self.command('after-failure')]),
                   ('b', 'b', [self.command('other')])])
        output = self.run_commands(expected=1)
        self.assertIn('command exited 42', output)
        self.assertIn('1 of 2 project command groups failed', output)
        self.assertEqual({e['label'] for e in self.records()}, {'after-failure', 'other'})
        self.peak()

    def test_working_directory_is_reset_and_multiline_commands_survive(self):
        self.plan([('a', 'with spaces', ['cd /\nprintf "line one\\nline two\\n"', self.command('a')])])
        output = self.run_commands()
        self.assertIn('line one\nline two', output)
        self.assertEqual(self.records()[0]['cwd'], str(self.workspace / 'with spaces'))

    def test_shared_nested_and_symlink_paths_serialize(self):
        self.plan([('a', 'same', [self.command('a')]), ('b', 'same/child', [self.command('b')]),
                   ('c', 'alias', [self.command('c')])])
        (self.workspace / 'alias').rmdir()
        (self.workspace / 'alias').symlink_to(self.workspace / 'same', target_is_directory=True)
        self.run_commands()
        self.assertEqual(self.peak(), 1)

    def test_missing_repo_and_legacy_configs_run_exclusively(self):
        self.plan([('a', 'a', [self.command('a')]), ('missing', 'missing', [self.command('b')])])
        self.run_commands()
        self.assertEqual(self.peak(), 1)
        self.assertEqual(self.records()[-1]['cwd'], str(self.workspace))
        self.plan([(None, 'a', [self.command('a')]), (None, 'b', [self.command('b')])])
        self.run_commands()
        self.assertEqual(self.peak(), 1)

    def test_empty_invalid_config_and_invalid_limit(self):
        self.plan([])
        self.assertIn('No provisioning commands', self.run_commands())
        self.generated.write_text('{')
        result = subprocess.run(['bash', str(RUNNER)], env=self.env, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.records(), [])
        self.plan([('a', 'a', [self.command('a')])])
        self.assertIn('PROVISION_JOBS must be', self.run_commands(jobs='bad', expected=1))
        self.assertEqual(self.records(), [])

    def test_generator_retains_profile_identity(self):
        store = self.agent / 'projects'
        store.mkdir()
        for name in ['z', 'a']:
            (store / (name+'.json')).write_text(json.dumps(dict(name=name,
                repos=[dict(url='https://example.invalid/'+name+'.git')],
                provisionCommands=['echo first', 'echo second'])))
        subprocess.run(['bash', str(ROOT / 'bin/generate-runtime-config.sh')],
                       env=dict(self.env, PROJECTS='z,a'), check=True, capture_output=True)
        commands = json.loads(self.generated.read_text())['provisionCommands']
        self.assertEqual([(c['profile'], c['dir'], c['command']) for c in commands],
                         [(p, p, c) for p in ['z', 'a'] for c in ['echo first', 'echo second']])

    def test_checkout_and_following_stage_barriers(self):
        # Exercise the actual checkout/command stage from provision.sh with only
        # run_step replaced; never invoke the full provision or service restarts.
        source = (ROOT / 'bin/provision.sh').read_text()
        stage = source[source.index('if [[ "${CHECKOUT_PROJECTS}" == "true" ]]; then',
                                    source.index('# 6. Optionally')):source.index('# 7. (Re)start')]
        self.plan([('a', 'a', [f'test -f {shlex.quote(str(self.root / "checked-out"))} && '+self.command('a', .3)]),
                   ('b', 'b', [self.command('b', .3)])])
        barrier = '''set -euo pipefail
run_step() {
 local title="$2"; shift 2
 if [[ "$title" == "Checking out project repos" ]]; then
   sleep .1; touch "$CHECKOUT_MARKER"
 else
   "$@"
 fi
}
'''+stage+f'\ntouch {shlex.quote(str(self.root / "following-stage"))}\n'
        env = dict(self.env, WORKSPACE_ROOT=str(self.workspace), AGENT_HOME=str(self.agent),
                   CHECKOUT_PROJECTS='true', _clone_creds_file='',
                   CHECKOUT_MARKER=str(self.root / 'checked-out'))
        subprocess.run(['bash', '-c', barrier], env=env, capture_output=True, check=True, timeout=10)
        self.assertTrue((self.root / 'following-stage').exists())
        self.assertEqual(self.peak(), 2)

    def test_cancel_stops_active_commands(self):
        self.plan([(p, p, [self.command(p, 60)]) for p in ['a', 'b']])
        process = subprocess.Popen(['bash', str(RUNNER)], env=self.env,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 5
            while len(self.records()) < 2 and time.monotonic() < deadline:
                time.sleep(.02)
            workers = [e['pid'] for e in self.records()]
            self.assertEqual(len(workers), 2)
            process.send_signal(signal.SIGTERM)
            output, _ = process.communicate(timeout=5)
            self.assertEqual(process.returncode, 143, output)
            for pid in workers:
                stat = Path(f'/proc/{pid}/stat')
                self.assertTrue(not stat.exists() or stat.read_text().split()[2] == 'Z')
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()


if __name__ == '__main__':
    unittest.main(verbosity=2)
