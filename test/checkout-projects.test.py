#!/usr/bin/env python3
"""Local Git integration checks; no network, service changes, or CI downloads.
Run: python3 test/checkout-projects.test.py
"""
import json
import os
from pathlib import Path
import shlex
import shutil
import signal
import subprocess
import tempfile
import time
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'bin/checkout-projects.sh'
GIT = shutil.which('git')


class CheckoutTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='construct-checkout-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.workspace = self.root / 'workspace'
        self.runtime = self.root / 'agent/runtime'
        self.runtime.mkdir(parents=True)
        self.events = self.root / 'events.jsonl'
        self.events.touch()
        self.config = self.root / 'config.env'
        self.config.write_text(f'AGENT_HOME={shlex.quote(str(self.runtime.parent))}\n'
                               f'WORKSPACE_ROOT={shlex.quote(str(self.workspace))}\n')
        self.env = dict(os.environ, CONFIG_FILE=str(self.config),
                        GIT_CONFIG_GLOBAL='/dev/null', GIT_CONFIG_NOSYSTEM='1',
                        CHECKOUT_JOBS='0', TEST_EVENTS=str(self.events),
                        TEST_DELAY='0', REAL_GIT=GIT)
        for key in list(self.env):
            if key.startswith('GIT_CONFIG_') and key not in ('GIT_CONFIG_GLOBAL', 'GIT_CONFIG_NOSYSTEM'):
                del self.env[key]
        bindir = self.root / 'bin'
        bindir.mkdir()
        wrapper = bindir / 'git'
        wrapper.write_text('''#!/usr/bin/env python3
import json, os, subprocess, sys, time
args = sys.argv[1:]
operation = args[2] if args[:1] == ['-C'] else args[0]
def event(kind):
    fd = os.open(os.environ['TEST_EVENTS'], os.O_WRONLY | os.O_APPEND)
    os.write(fd, (json.dumps(dict(kind=kind, pid=os.getpid(), op=operation, args=args))+'\\n').encode())
    os.close(fd)
event('start')
if operation in ('clone', 'fetch'):
    time.sleep(float(os.environ['TEST_DELAY']))
if operation == 'rev-parse':
    time.sleep(float(os.environ.get('TEST_INSPECTION_DELAY', '0')))
rc = subprocess.call([os.environ['REAL_GIT'], *args])
event('end')
sys.exit(rc)
''')
        wrapper.chmod(0o755)
        self.env['PATH'] = str(bindir) + os.pathsep + os.environ['PATH']
        self.source = self.root / 'source'
        self.git('init', '-b', 'main', str(self.source))
        self.commit('initial')

    def git(self, *args):
        return subprocess.check_output([GIT, *map(str, args)], env=self.env,
                                       stderr=subprocess.STDOUT, text=True).strip()

    def commit(self, text):
        (self.source / 'file').write_text(text)
        self.git('-C', self.source, 'add', 'file')
        self.git('-C', self.source, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                 'commit', '-m', text)
        return self.git('-C', self.source, 'rev-parse', 'HEAD')

    def profiles(self, directories):
        (self.runtime / 'generated.json').write_text(json.dumps({'repos': [
            {'url': str(self.source), 'directory': d} for d in directories]}))

    def run_checkout(self, jobs=0, delay=0, expected=0):
        self.events.write_text('')
        result = subprocess.run(['bash', str(SCRIPT)], env=dict(self.env, CHECKOUT_JOBS=str(jobs),
                                TEST_DELAY=str(delay)), text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=30)
        self.assertEqual(result.returncode, expected, result.stdout)
        return result.stdout

    def records(self):
        return [json.loads(line) for line in self.events.read_text().splitlines()]

    def peak(self):
        active, peak = set(), 0
        for event in self.records():
            if event['op'] not in ('clone', 'fetch'):
                continue
            if event['kind'] == 'start':
                active.add(event['pid'])
            else:
                active.remove(event['pid'])
            peak = max(peak, len(active))
        self.assertFalse(active)
        return peak

    def test_parallel_clone_and_optional_limits(self):
        self.profiles(['a', 'b with spaces', 'c', 'd'])
        self.run_checkout(delay=.25)
        self.assertEqual(self.peak(), 4)
        for name in ['a', 'b with spaces', 'c', 'd']:
            self.assertEqual((self.workspace / name / 'file').read_text(), 'initial')
        self.run_checkout(jobs=2, delay=.15)
        self.assertEqual(self.peak(), 2)
        self.run_checkout(jobs=1, delay=.05)
        self.assertEqual(self.peak(), 1)

    def test_fetch_once_fast_forward_and_preserve_dirty_or_diverged(self):
        self.profiles(['clean', 'dirty', 'diverged'])
        self.run_checkout()
        old = self.git('-C', self.source, 'rev-parse', 'HEAD')
        (self.workspace / 'dirty/file').write_text('local edit')
        self.git('-C', self.workspace / 'diverged', '-c', 'user.name=Test', '-c',
                 'user.email=test@example.invalid', 'commit', '--allow-empty', '-m', 'local commit')
        diverged = self.git('-C', self.workspace / 'diverged', 'rev-parse', 'HEAD')
        new = self.commit('upstream change')
        output = self.run_checkout()
        self.assertEqual(self.git('-C', self.workspace / 'clean', 'rev-parse', 'HEAD'), new)
        self.assertEqual(self.git('-C', self.workspace / 'dirty', 'rev-parse', 'HEAD'), old)
        self.assertEqual((self.workspace / 'dirty/file').read_text(), 'local edit')
        self.assertEqual(self.git('-C', self.workspace / 'diverged', 'rev-parse', 'HEAD'), diverged)
        self.assertIn('not fast-forwarded', output)
        starts = [e['op'] for e in self.records() if e['kind'] == 'start']
        self.assertEqual(starts.count('fetch'), 3)
        self.assertNotIn('pull', starts)

    def test_failure_does_not_skip_other_repos(self):
        self.profiles(['a', 'b', 'c'])
        generated = self.runtime / 'generated.json'
        data = json.loads(generated.read_text())
        data['repos'][1]['url'] = str(self.root / 'missing')
        generated.write_text(json.dumps(data))
        output = self.run_checkout(expected=1)
        self.assertIn('1 repo(s) failed', output)
        self.assertTrue((self.workspace / 'a/file').exists())
        self.assertTrue((self.workspace / 'c/file').exists())

    def test_workers_finished_before_wait_keep_their_exit_status(self):
        self.profiles(['existing'])
        self.run_checkout()
        self.profiles(['fast', 'broken', 'existing'])
        generated = self.runtime / 'generated.json'
        data = json.loads(generated.read_text())
        data['repos'][1]['url'] = str(self.root / 'missing')
        generated.write_text(json.dumps(data))
        self.env['TEST_INSPECTION_DELAY'] = '.3'
        output = self.run_checkout(expected=1)
        self.assertIn('1 repo(s) failed', output)
        self.assertNotIn('unbound variable', output)
        self.assertTrue((self.workspace / 'fast/file').exists())

    def test_empty_and_malformed_config(self):
        self.profiles([])
        self.assertIn('No repos to check out', self.run_checkout())
        (self.runtime / 'generated.json').write_text('{')
        result = subprocess.run(['bash', str(SCRIPT)], env=self.env, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.records(), [])

    def test_shared_and_nested_paths_serialize(self):
        self.profiles(['same', './same', 'same/child'])
        self.run_checkout(delay=.1)
        self.assertEqual(self.peak(), 1)
        self.assertTrue((self.workspace / 'same/child/file').exists())
        (self.workspace / 'alias').symlink_to(self.workspace / 'same', target_is_directory=True)
        self.profiles(['same', 'alias'])
        self.run_checkout(delay=.1)
        self.assertEqual(self.peak(), 1)

    def test_linked_worktrees_share_git_storage(self):
        self.profiles(['a'])
        self.run_checkout()
        self.git('-C', self.workspace / 'a', 'worktree', 'add', '-b', 'other', self.workspace / 'b')
        self.profiles(['a', 'b'])
        self.run_checkout(delay=.1)
        self.assertEqual(self.peak(), 1)
        self.assertFalse((self.workspace / 'b/.git').is_dir())

    def test_stale_remote_refspec_is_repaired(self):
        self.profiles(['a'])
        self.run_checkout()
        self.git('-C', self.workspace / 'a', 'config', 'remote.origin.fetch',
                 '+refs/heads/deleted:refs/remotes/origin/deleted')
        output = self.run_checkout()
        self.assertIn('restoring normal branch discovery', output)
        self.assertEqual(self.git('-C', self.workspace / 'a', 'config', 'remote.origin.fetch'),
                         '+refs/heads/*:refs/remotes/origin/*')

    def test_cancellation_terminates_workers(self):
        self.profiles(['a', 'b'])
        proc = subprocess.Popen(['bash', str(SCRIPT)], env=dict(self.env, TEST_DELAY='60'),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            deadline = time.monotonic() + 5
            workers = []
            while time.monotonic() < deadline:
                workers = [e['pid'] for e in self.records() if e['op'] == 'clone']
                if len(workers) == 2:
                    break
                time.sleep(.02)
            self.assertEqual(len(workers), 2)
            proc.send_signal(signal.SIGTERM)
            output, _ = proc.communicate(timeout=5)
            self.assertEqual(proc.returncode, 143, output)
            for pid in workers:
                stat = Path(f'/proc/{pid}/stat')
                # A killed orphan can briefly await reaping by the container's init.
                self.assertTrue(not stat.exists() or stat.read_text().split()[2] == 'Z')
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.communicate()


if __name__ == '__main__':
    unittest.main(verbosity=2)
