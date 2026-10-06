"""bin/construct-t3-resume.py against a stub T3 API, plus the export/restore hand-off.

The T3 server is an in-process HTTP stub; systemctl and t3 are process doubles
on PATH; git is real (worktree recreation). No live T3, systemd unit or
/var/lib path is touched: every path the helper uses is redirected into a temp dir.
"""
import http.server
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tarfile
import tempfile
import textwrap
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / 'bin' / 'construct-t3-resume.py'
MODEL = {'instanceId': 'claudeAgent', 'model': 'claude-opus-5-5', 'options': [{'id': 'effort', 'value': 'high'}]}


def thread(tid, *, session=None, turn=None, liveness=None, project='p1', **extra):
    data = {
        'id': tid, 'projectId': project, 'title': 'Thread ' + tid, 'modelSelection': MODEL,
        'runtimeMode': 'full-access', 'interactionMode': 'default', 'branch': None, 'worktreePath': None,
        'archivedAt': None, 'latestUserMessageAt': '2026-01-01T00:00:00.000Z',
        'hasPendingApprovals': False, 'hasPendingUserInput': False,
        'session': {'status': session} if session else None,
        'latestTurn': {'state': turn} if turn else None,
    }
    if liveness:
        data['backgroundLiveness'] = liveness
    data.update(extra)
    return data


class StubT3(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, code, body=None):
        raw = b'' if body is None else json.dumps(body).encode()
        self.send_response(code)
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def authorized(self):
        state = self.server.state
        if self.headers.get('Authorization') != 'Bearer ' + state['token']:
            self.reply(401, {'error': 'unauthorized'})
            return False
        return True

    def do_GET(self):
        state = self.server.state
        if self.path != '/api/orchestration/shell' or not self.authorized():
            return None if self.path == '/api/orchestration/shell' else self.reply(404)
        if state.get('v2'):
            return self.reply(400, {'_tag': 'HttpApiDecodeError'})
        if state.get('html'):
            raw = b'<!doctype html><html></html>'
            self.send_response(200)
            self.send_header('Content-Length', str(len(raw)))
            self.end_headers()
            return self.wfile.write(raw)
        if state.get('stopped_flag') and Path(state['stopped_flag']).exists():
            state['shell_after_stop'] = True
        state['shell_reads'] += 1
        return self.reply(200, state['shell'])

    def do_POST(self):
        state = self.server.state
        if self.path != '/api/orchestration/dispatch':
            return self.reply(404)
        if not self.authorized():
            return None
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        if state['dispatch_failures'] > 0:
            state['dispatch_failures'] -= 1
            return self.reply(500, {'error': 'busy'})
        state['dispatched'].append(body)
        return self.reply(200, {'sequence': len(state['dispatched'])})


class Fixture:
    def __init__(self):
        self.dir = Path(tempfile.mkdtemp(prefix='t3-resume-test.'))
        self.bin = self.dir / 'bin'
        self.bin.mkdir()
        self.pending = self.dir / 'state' / 't3-resume.json'
        self.token = self.dir / 't3-resume-token'
        self.boot = self.dir / 'boot_id'
        self.invocation = self.dir / 'invocation'
        self.marker = self.dir / 'provisioning'
        self.reboot_required = self.dir / 'reboot-required'
        self.calls = self.dir / 'calls.log'
        self.boot.write_text('boot-1\n')
        self.invocation.write_text('inv-1\n')
        self.state = {'token': 'TOKEN-1', 'shell': {'projects': [], 'threads': []}, 'dispatched': [],
                      'dispatch_failures': 0, 'shell_reads': 0}
        self.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), StubT3)
        self.server.state = self.state
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.stub('systemctl', '''
            import os, sys
            args = sys.argv[1:]
            with open(os.environ['CALLS'], 'a') as f: f.write('systemctl ' + ' '.join(args) + '\\n')
            if args[:1] == ['show']:
                prop = args[args.index('-p') + 1]
                if prop == 'InvocationID': print(open(os.environ['INVOCATION_FILE']).read().strip())
                elif prop == 'ActiveEnterTimestampMonotonic': print('1')
                sys.exit(0)
            if args[:1] == ['is-active']: sys.exit(0 if os.environ.get('T3_ACTIVE') == '1' else 3)
            if args[:1] == ['is-enabled']: sys.exit(1 if os.environ.get('T3_DISABLED') == '1' else 0)
            if args[:1] == ['stop'] and os.environ.get('STOPPED_FLAG'): open(os.environ['STOPPED_FLAG'], 'w').close()
            sys.exit(0)
        ''')
        self.stub('t3', '''
            import os, sys
            with open(os.environ['CALLS'], 'a') as f: f.write('t3 ' + ' '.join(sys.argv[1:]) + '\\n')
            print(open(os.environ['T3_NEXT_TOKEN']).read().strip())
        ''')
        self.next_token = self.dir / 'next-token'
        self.next_token.write_text('TOKEN-1\n')

    def stub(self, name, body):
        path = self.bin / name
        path.write_text('#!/usr/bin/env python3\n' + textwrap.dedent(body))
        path.chmod(0o755)

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        shutil.rmtree(self.dir, ignore_errors=True)

    def env(self, **extra):
        env = {
            'PATH': f'{self.bin}:/usr/bin:/bin', 'HOME': str(self.dir),
            'CONSTRUCT_T3_RESUME_FILE': str(self.pending), 'CONSTRUCT_T3_RESUME_TOKEN_FILE': str(self.token),
            'CONSTRUCT_T3_API': f'http://127.0.0.1:{self.server.server_address[1]}',
            'CONSTRUCT_PROVISION_MARKER': str(self.marker), 'CONSTRUCT_REBOOT_REQUIRED_FILE': str(self.reboot_required),
            'CONSTRUCT_BOOT_ID_FILE': str(self.boot), 'CONFIG_FILE': str(self.dir / 'absent-config.env'),
            'CONSTRUCT_T3_RESUME_SETTLE': '0', 'CONSTRUCT_T3_RESUME_POLL': '0.05',
            'CONSTRUCT_T3_RESUME_API_WAIT': '1', 'CONSTRUCT_T3_RESUME_PROVISION_WAIT': '1',
            'CONSTRUCT_T3_RESUME_SNAPSHOT_WAIT': '0.3',
            'CALLS': str(self.calls), 'INVOCATION_FILE': str(self.invocation), 'T3_NEXT_TOKEN': str(self.next_token),
        }
        env.update(extra)
        return env

    def run(self, *args, **env):
        return subprocess.run(['python3', str(HELPER), *args], capture_output=True, text=True, env=self.env(**env))

    def calls_text(self):
        return self.calls.read_text() if self.calls.exists() else ''

    def pending_data(self):
        return json.loads(self.pending.read_text()) if self.pending.exists() else None


class HelperTests(unittest.TestCase):
    def setUp(self):
        self.fx = Fixture()
        self.addCleanup(self.fx.close)

    def busy_shell(self):
        self.fx.state['shell'] = {'projects': [{'id': 'p1', 'workspaceRoot': str(self.fx.dir)}], 'threads': [
            thread('run', session='running', turn='running'),
            thread('starting', session='starting'),
            thread('mon', session='ready', turn='completed', liveness='monitoring'),
            thread('work', session='ready', turn='completed', liveness='working'),
            thread('idle', session='ready', turn='completed'),
            thread('asks', session='running', turn='running', hasPendingUserInput=True),
            thread('approve', session='running', turn='running', hasPendingApprovals=True),
            thread('gone', session='running', turn='running', archivedAt='2026-01-01T00:00:00.000Z'),
            thread('never', session=None, turn=None),
        ]}

    def test_snapshot_records_busy_threads_only(self):
        self.busy_shell()
        result = self.fx.run('snapshot')
        self.assertEqual(0, result.returncode, result.stderr)
        data = self.fx.pending_data()
        self.assertEqual({'run', 'starting', 'mon', 'work'}, set(data['threads']))
        self.assertEqual('running', data['threads']['run']['state'])
        self.assertEqual('monitoring', data['threads']['mon']['state'])
        self.assertEqual('working', data['threads']['work']['state'])
        entry = data['threads']['run']
        self.assertEqual(('boot-1', 'inv-1', 'reprovision', False),
                         (entry['bootId'], entry['t3InvocationId'], entry['reason'], entry['interrupted']))
        self.assertEqual(0o600, stat.S_IMODE(self.fx.pending.stat().st_mode))
        self.assertIn('t3 auth session issue --ttl 365d --token-only --label construct-t3-resume', self.fx.calls_text())
        self.assertEqual('TOKEN-1', self.fx.token.read_text().strip())
        self.assertEqual(0o600, stat.S_IMODE(self.fx.token.stat().st_mode))

    def test_snapshot_keeps_entries_of_an_unfinished_run(self):
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        self.fx.invocation.write_text('inv-2\n')
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running'),
                                             thread('b', session='running', turn='running')]
        self.assertEqual(0, self.fx.run('snapshot').returncode)
        data = self.fx.pending_data()
        self.assertEqual('inv-1', data['threads']['a']['t3InvocationId'])
        self.assertEqual('inv-2', data['threads']['b']['t3InvocationId'])

    def test_snapshot_without_busy_threads_writes_nothing(self):
        self.fx.state['shell']['threads'] = [thread('idle', session='ready', turn='completed')]
        result = self.fx.run('snapshot')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.fx.pending.exists())
        self.assertIn('no T3 Code thread is busy', result.stdout)

    def test_an_unsupported_t3_server_is_skipped(self):
        # An orchestration-v2 server answers the v1 shell route with 400, or with another shape.
        self.fx.state['v2'] = True
        result = self.fx.run('snapshot')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('does not offer the HTTP orchestration API', result.stdout)
        self.assertFalse(self.fx.pending.exists())
        self.fx.state['v2'] = False
        self.fx.state['shell'] = {'threads': [{'id': 'x', 'state': 'running'}]}
        result = self.fx.run('snapshot')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('unknown shape', result.stdout)
        # T3 answers paths it has no route for with its web app.
        self.fx.state['html'] = True
        result = self.fx.run('snapshot')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('the reply is not JSON', result.stdout)

    def test_snapshot_only_warns_when_t3_does_not_answer(self):
        # Provisioning goes on: a missing record must not turn into a failed step.
        result = self.fx.run('snapshot', CONSTRUCT_T3_API='http://127.0.0.1:9')
        self.assertEqual(0, result.returncode)
        self.assertIn('WARNING: could not read the T3 Code threads', result.stderr)
        self.assertFalse(self.fx.pending.exists())
        self.fx.token.unlink()
        result = self.fx.run('snapshot', PATH='/usr/bin:/bin', CONSTRUCT_T3_RESUME_SNAPSHOT_WAIT='0')
        self.assertEqual(0, result.returncode)
        self.assertIn('could not mint a T3 API session', result.stderr)

    def test_resume_on_a_server_switched_to_v2_reports_the_threads(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        self.fx.state['v2'] = True
        result = self.fx.run('resume')
        self.assertEqual(1, result.returncode)
        self.assertIn('1 thread(s) not resumed', result.stderr)
        self.assertEqual([], self.fx.state['dispatched'])
        self.assertFalse(self.fx.pending.exists())

    def test_arm_drops_threads_when_t3_kept_running(self):
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        result = self.fx.run('arm')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('no thread was interrupted', result.stdout)
        self.assertFalse(self.fx.pending.exists())
        self.assertNotIn('systemctl start', self.fx.calls_text())

    def test_arm_starts_the_service_after_a_t3_restart(self):
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        self.fx.invocation.write_text('inv-2\n')
        result = self.fx.run('arm')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('systemctl start --no-block construct-t3-resume.service', self.fx.calls_text())
        self.assertIn('a', self.fx.pending_data()['threads'])

    def test_arm_waits_for_a_pending_reboot(self):
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        self.fx.reboot_required.write_text('*** System restart required ***\n')
        result = self.fx.run('arm')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('after the reboot that ends this provision', result.stdout)
        self.assertEqual('boot-1', self.fx.pending_data()['resumeAfterBoot'])
        self.assertNotIn('systemctl start', self.fx.calls_text())

    def test_arm_holds_for_the_reboot_the_host_announced(self):
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        result = self.fx.run('arm', '--reboot-follows')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('boot-1', self.fx.pending_data()['resumeAfterBoot'])
        self.assertNotIn('systemctl start', self.fx.calls_text())
        # After the reboot the boot id differs, so the hold is over.
        self.fx.boot.write_text('boot-2\n')
        self.fx.state['shell']['threads'] = [thread('a', session='error', turn='interrupted')]
        self.assertEqual(0, self.fx.run('resume').returncode)
        self.assertEqual(['a'], [c['threadId'] for c in self.fx.state['dispatched']])

    def test_a_switched_off_t3_drops_the_snapshot(self):
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        self.fx.invocation.write_text('\n')
        result = self.fx.run('arm', T3_DISABLED='1')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('T3 Code is switched off', result.stdout)
        self.assertFalse(self.fx.pending.exists())
        self.assertNotIn('systemctl start', self.fx.calls_text())
        # The same at boot, for a snapshot a restore left behind.
        self.fx.run('snapshot')
        result = self.fx.run('resume', T3_DISABLED='1')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(self.fx.pending.exists())
        self.assertEqual([], self.fx.state['dispatched'])

    def test_arm_without_snapshot_is_silent(self):
        result = self.fx.run('arm')
        self.assertEqual((0, ''), (result.returncode, result.stdout))

    def snapshot_then_restart(self, threads):
        self.fx.state['shell']['threads'] = threads
        self.assertEqual(0, self.fx.run('snapshot').returncode)
        self.fx.invocation.write_text('inv-2\n')

    def test_resume_messages_interrupted_threads(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running'),
                                    thread('m', session='ready', turn='completed', liveness='monitoring')])
        # After the restart T3 reports the sessions as errored; the background state is gone.
        self.fx.state['shell']['threads'] = [
            thread('a', session='error', turn='interrupted', runtimeMode='auto-accept-edits', interactionMode='plan'),
            thread('m', session='ready', turn='completed'),
        ]
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        sent = {c['threadId']: c for c in self.fx.state['dispatched']}
        self.assertEqual({'a', 'm'}, set(sent))
        command = sent['a']
        self.assertEqual('thread.turn.start', command['type'])
        self.assertEqual(('user', []), (command['message']['role'], command['message']['attachments']))
        self.assertEqual(('auto-accept-edits', 'plan', MODEL),
                         (command['runtimeMode'], command['interactionMode'], command['modelSelection']))
        self.assertTrue(command['message']['text'].startswith('[Construct reprovision] This VM was reprovisioned while'))
        self.assertIn('working on a turn', command['message']['text'])
        self.assertIn('Restarting the T3 Code server', command['message']['text'])
        self.assertIn('watching background tasks', sent['m']['message']['text'])
        self.assertFalse(self.fx.pending.exists())

    def test_resume_after_reboot_says_so(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        self.fx.boot.write_text('boot-2\n')
        self.fx.state['shell']['threads'] = [thread('a', session='error', turn='interrupted')]
        self.assertEqual(0, self.fx.run('resume').returncode)
        text = self.fx.state['dispatched'][0]['message']['text']
        self.assertIn('reprovisioned and rebooted', text)
        self.assertIn('The reboot ended that work', text)

    def test_resume_skips_threads_the_user_took_over(self):
        self.snapshot_then_restart([thread(t, session='running', turn='running') for t in 'abcde'])
        self.fx.state['shell']['threads'] = [
            thread('a', session='running', turn='running', latestUserMessageAt='2999-01-01T00:00:00.000Z'),
            thread('b', session='running', turn='running', hasPendingUserInput=True),
            thread('c', session='ready', turn='completed', archivedAt='2026-02-01T00:00:00.000Z'),
            # d was deleted; e is running again because T3 continued it itself.
            thread('e', session='running', turn='running'),
        ]
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(['e'], [c['threadId'] for c in self.fx.state['dispatched']])
        self.assertIn('someone sent it a message', result.stdout)
        self.assertIn('waiting for an approval or an answer', result.stdout)
        self.assertIn('no longer exists or was archived', result.stdout)

    def test_resume_holds_until_the_next_boot(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        self.fx.reboot_required.write_text('x\n')
        self.fx.run('arm')
        self.fx.state['shell']['threads'] = [thread('a', session='error', turn='interrupted')]
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode)
        self.assertEqual([], self.fx.state['dispatched'])
        self.assertTrue(self.fx.pending.exists())
        self.assertEqual(0, self.fx.run('resume', '--now').returncode)
        self.assertEqual(1, len(self.fx.state['dispatched']))

    def test_a_waiting_resume_honours_a_hold_set_meanwhile(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        self.fx.marker.write_text(f'{os.getpid()}\n')
        waiting = subprocess.Popen(['python3', str(HELPER), 'resume'], stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True,
                                   env=self.fx.env(CONSTRUCT_T3_RESUME_PROVISION_WAIT='20'))
        # The provision it waits for ends with a reboot announced by the host.
        self.assertEqual(0, self.fx.run('arm', '--reboot-follows').returncode)
        self.fx.marker.unlink()
        out, err = waiting.communicate(timeout=30)
        self.assertEqual(0, waiting.returncode, out + err)
        self.assertIn('the resume now waits for the next boot', out)
        self.assertEqual([], self.fx.state['dispatched'])
        self.assertTrue(self.fx.pending.exists())

    def test_resume_drops_old_entries(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        data = self.fx.pending_data()
        data['threads']['a']['takenAt'] = '2026-01-01T00:00:00.000Z'
        self.fx.pending.write_text(json.dumps(data))
        self.fx.state['shell']['threads'] = [thread('a', session='error', turn='interrupted',
                                                    latestUserMessageAt='2025-12-31T00:00:00.000Z')]
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('the snapshot is older than 24 hours', result.stdout)
        self.assertEqual([], self.fx.state['dispatched'])

    def test_failed_dispatches_stay_for_the_next_run(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running'),
                                    thread('b', session='running', turn='running')])
        self.fx.state['shell']['threads'] = [thread('a', session='error', turn='interrupted'),
                                             thread('b', session='error', turn='interrupted')]
        self.fx.state['dispatch_failures'] = 3
        result = self.fx.run('resume')
        self.assertEqual(1, result.returncode)
        self.assertEqual(1, len(self.fx.state['dispatched']))
        failed = set(self.fx.pending_data()['threads'])
        self.assertEqual({'a', 'b'} - {self.fx.state['dispatched'][0]['threadId']}, failed)
        self.assertEqual(0, self.fx.run('resume').returncode)
        self.assertEqual(2, len(self.fx.state['dispatched']))
        self.assertFalse(self.fx.pending.exists())

    def test_resume_waits_for_a_running_provision(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        self.fx.marker.write_text(f'{os.getpid()}\n')
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode)
        self.assertIn('a provision is still running', result.stdout)
        self.assertEqual([], self.fx.state['dispatched'])
        self.assertTrue(self.fx.pending.exists())
        # A marker whose process is gone (a killed run) does not block the resume.
        self.fx.marker.write_text('999999999\n')
        self.assertEqual(0, self.fx.run('resume').returncode)
        self.assertEqual(1, len(self.fx.state['dispatched']))

    def test_resume_keeps_the_snapshot_when_t3_does_not_answer(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        result = self.fx.run('resume', CONSTRUCT_T3_API='http://127.0.0.1:9')
        self.assertEqual(1, result.returncode)
        self.assertIn('did not answer', result.stderr)
        self.assertTrue(self.fx.pending.exists())

    def test_resume_mints_a_new_session_after_a_401(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        # A reinstall replaced T3's database: the stored session is unknown now.
        self.fx.state['token'] = 'TOKEN-2'
        self.fx.next_token.write_text('TOKEN-2\n')
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(1, len(self.fx.state['dispatched']))
        self.assertEqual('TOKEN-2', self.fx.token.read_text().strip())

    def test_resume_retries_a_failed_dispatch(self):
        self.snapshot_then_restart([thread('a', session='running', turn='running')])
        self.fx.state['dispatch_failures'] = 2
        result = self.fx.run('resume')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(1, len(self.fx.state['dispatched']))

    def git(self, *args, cwd):
        subprocess.run(['git', *args], cwd=cwd, check=True, capture_output=True,
                       env={**os.environ, 'GIT_AUTHOR_NAME': 't', 'GIT_AUTHOR_EMAIL': 't@example.invalid',
                            'GIT_COMMITTER_NAME': 't', 'GIT_COMMITTER_EMAIL': 't@example.invalid'})

    def test_reinstall_import_recreates_worktrees(self):
        repo = self.fx.dir / 'repo'
        repo.mkdir()
        self.git('init', '-q', '-b', 'main', cwd=repo)
        self.git('commit', '-q', '--allow-empty', '-m', 'init', cwd=repo)
        self.git('branch', 'feature', cwd=repo)
        worktree = self.fx.dir / 'worktrees' / 'feature'
        exported = self.fx.dir / 'exported.json'
        self.fx.state['shell'] = {'projects': [{'id': 'p1', 'workspaceRoot': str(repo)},
                                               {'id': 'p2', 'workspaceRoot': str(self.fx.dir / 'not-cloned')}],
                                  'threads': [thread('wt', session='running', turn='running', branch='feature',
                                                     worktreePath=str(worktree)),
                                              thread('lost', session='running', turn='running', project='p2')]}
        self.assertEqual(0, self.fx.run('snapshot', '--reason', 'reinstall', '--out', str(exported)).returncode)
        self.assertFalse(Path(str(exported) + '.lock').exists())
        self.assertFalse(self.fx.pending.exists())

        # The fresh VM: same thread ids, another boot. The provision after the restore
        # holds the resume for the reboot the host announced.
        self.fx.boot.write_text('fresh-1\n')
        result = self.fx.run('import', str(exported))
        self.assertEqual(0, result.returncode, result.stderr)
        data = self.fx.pending_data()
        self.assertIsNone(data['resumeAfterBoot'])
        self.assertTrue(all(e['interrupted'] for e in data['threads'].values()))
        self.assertIn('will be resumed after the reboot', self.fx.run('arm', '--reboot-follows').stdout)
        self.assertEqual(0, self.fx.run('resume').returncode)
        self.assertEqual([], self.fx.state['dispatched'])

        self.fx.boot.write_text('fresh-2\n')
        self.fx.state['shell']['threads'] = [
            thread('wt', session='error', turn='interrupted', branch='feature', worktreePath=str(worktree)),
            thread('lost', session='error', turn='interrupted', project='p2')]
        result = self.fx.run('resume')
        self.assertEqual(1, result.returncode)
        self.assertIn('not-cloned does not exist', result.stderr)
        self.assertTrue((worktree / '.git').exists())
        self.assertEqual(['wt'], [c['threadId'] for c in self.fx.state['dispatched']])
        text = self.fx.state['dispatched'][0]['message']['text']
        self.assertTrue(text.startswith('[Construct reinstall] This VM was reinstalled while this thread was working on a turn.'))
        self.assertIn(f'Its git worktree {worktree} was missing and has been recreated from branch feature.', text)

    def test_status(self):
        self.assertIn('No T3 Code threads are waiting', self.fx.run('status').stdout)
        self.fx.state['shell']['threads'] = [thread('a', session='running', turn='running')]
        self.fx.run('snapshot')
        self.assertIn('1 T3 Code thread(s) will be resumed', self.fx.run('status').stdout)


class ExportRestoreTests(unittest.TestCase):
    """The backup carries the snapshot taken before export-config.sh stops T3, and
    restore-config.sh holds it for the reboot that ends the reinstall."""

    def setUp(self):
        self.fx = Fixture()
        self.addCleanup(self.fx.close)

    def test_round_trip(self):
        fx = self.fx
        home = fx.dir / 'home'
        (home / '.t3' / 'userdata').mkdir(parents=True)
        (home / '.t3' / 'userdata' / 'state.sqlite').write_text('db')
        fx.state['shell']['threads'] = [thread('busy', session='running', turn='running'),
                                        thread('idle', session='ready', turn='completed')]
        fx.state['stopped_flag'] = str(fx.dir / 't3-stopped')
        out = fx.dir / 'backup.tar.gz'
        env = fx.env(EXPORT_HOME=str(home), INCLUDE_AUTH='true', INCLUDE_HISTORY='false', OUT=str(out),
                     REPO_DIR=str(fx.dir / 'repo'), PROJECTS_STORE=str(fx.dir / 'projects'),
                     WORKSPACE_ROOT=str(fx.dir / 'workspace'), T3_ACTIVE='1', STOPPED_FLAG=fx.state['stopped_flag'],
                     VSCODE_SERVE_WEB_TOKEN_FILE=str(fx.dir / 'absent'), T3CODE_TLS_DIR=str(fx.dir / 'absent-tls'))
        result = subprocess.run(['bash', str(ROOT / 'bin' / 'export-config.sh')], capture_output=True, text=True, env=env)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('+ t3-resume.json', result.stdout)
        self.assertFalse(fx.state.get('shell_after_stop'), 'the snapshot must come before T3 stops')
        self.assertIn('systemctl stop t3code-serve', fx.calls_text())
        with tarfile.open(out) as archive:
            names = archive.getnames()
            snapshot = json.load(archive.extractfile('./t3-resume.json'))
        self.assertNotIn('./t3-resume.json.lock', names)
        self.assertEqual(['busy'], list(snapshot['threads']))
        self.assertEqual('reinstall', snapshot['threads']['busy']['reason'])

        fx.boot.write_text('fresh-1\n')
        restored = fx.dir / 'restored'
        env.update(BACKUP_TGZ=str(out), EXPORT_HOME=str(restored), REPO_DIR=str(ROOT), T3_ACTIVE='0')
        result = subprocess.run(['bash', str(ROOT / 'bin' / 'restore-config.sh')], capture_output=True, text=True, env=env)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        data = fx.pending_data()
        self.assertEqual(['busy'], list(data['threads']))
        self.assertIsNone(data['resumeAfterBoot'])
        self.assertTrue(data['threads']['busy']['interrupted'])

    def test_no_snapshot_without_the_event_store(self):
        fx = self.fx
        home = fx.dir / 'home'
        (home / '.t3' / 'userdata').mkdir(parents=True)
        fx.state['shell']['threads'] = [thread('busy', session='running', turn='running')]
        out = fx.dir / 'backup.tar.gz'
        env = fx.env(EXPORT_HOME=str(home), INCLUDE_AUTH='false', INCLUDE_HISTORY='false', OUT=str(out),
                     REPO_DIR=str(fx.dir / 'repo'), PROJECTS_STORE=str(fx.dir / 'projects'),
                     WORKSPACE_ROOT=str(fx.dir / 'workspace'), T3_ACTIVE='1',
                     VSCODE_SERVE_WEB_TOKEN_FILE=str(fx.dir / 'absent'), T3CODE_TLS_DIR=str(fx.dir / 'absent-tls'))
        result = subprocess.run(['bash', str(ROOT / 'bin' / 'export-config.sh')], capture_output=True, text=True, env=env)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        with tarfile.open(out) as archive:
            self.assertNotIn('./t3-resume.json', archive.getnames())


class ProvisionWiringTests(unittest.TestCase):
    """provision.sh's _finish_provision, sourced alone, with the helper replaced by a recorder."""

    def finish(self, *, arm, reboot=None, snapshot=True):
        with tempfile.TemporaryDirectory() as directory:
            tmp = Path(directory)
            (tmp / 'repo' / 'bin').mkdir(parents=True)
            calls = tmp / 'calls'
            (tmp / 'repo' / 'bin' / 'construct-t3-resume.py').write_text(
                f'import sys\nopen({str(calls)!r}, "a").write(" ".join(sys.argv[1:]) + "\\n")\n')
            pending = tmp / 't3-resume.json'
            if snapshot:
                pending.write_text('{"threads": {"a": {}}}')
            env = {'PATH': '/usr/bin:/bin', 'CONSTRUCT_STEP_RUNNER_ONLY': 'true', 'PROVISION_PATH': str(ROOT / 'bin' / 'provision.sh'),
                   'REPO_DIR': str(tmp / 'repo'), 'CONSTRUCT_T3_RESUME_FILE': str(pending), 'PERSIST': str(tmp)}
            if reboot is not None:
                env['REBOOT_AFTER_PROVISION'] = reboot
            body = ('source "$PROVISION_PATH"; _PERSISTENT_LOG_DIR="$PERSIST"; '
                    + ('_T3_RESUME_ARM=true; ' if arm else '') + '_finish_provision 0')
            result = subprocess.run(['bash', '-c', body], capture_output=True, text=True, env=env)
            return result, calls.read_text() if calls.exists() else ''

    def test_every_exit_arms(self):
        result, calls = self.finish(arm=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('arm\n', calls)
        self.assertIn('Scheduling the resume of interrupted T3 Code threads', result.stdout)

    def test_the_host_reboot_is_passed_on(self):
        self.assertEqual('arm --reboot-follows\n', self.finish(arm=True, reboot='true')[1])
        self.assertEqual('arm\n', self.finish(arm=True, reboot='false')[1])

    def test_nothing_new_without_a_snapshot_or_outside_a_run(self):
        result, calls = self.finish(arm=True, snapshot=False)
        self.assertEqual(('', 0), (calls, result.returncode))
        self.assertNotIn('T3 Code', result.stdout)
        self.assertEqual('', self.finish(arm=False)[1])

    def test_the_main_run_and_the_deferred_phase_arm(self):
        source = (ROOT / 'bin' / 'provision.sh').read_text()
        phase = source[source.index('if [[ "${PROVISION_PHASE:-}" == "project-commands" ]]; then'):]
        self.assertIn('_T3_RESUME_ARM=true', phase[:phase.index('_finish_provision 0')])
        main = source[source.index('_finish_provision 0\nfi\n'):]
        self.assertIn('if [[ "${DEFER_PROJECT_COMMANDS:-false}" != "true" ]]; then\n  _T3_RESUME_ARM=true',
                      main[:main.index('check_disk_space\n')])
        snapshot = source.index('snapshot --reason reprovision')
        self.assertLess(source.index('run_step critical "Checking free disk space"'), snapshot)
        self.assertLess(snapshot, source.index('Installing T3 Code web GUI'))
        self.assertLess(snapshot, source.index('Running core host bootstrap'))

    def test_host_and_installer_wiring(self):
        host = (ROOT / 'Provision-AgentVM.ps1').read_text()
        self.assertIn("Invoke-Scp -LocalPath (Join-Path $PSScriptRoot 'bin\\construct-t3-resume.py') "
                      "-RemotePath $ExportT3ResumeScript", host)
        self.assertEqual(3, host.count('$ExportScanScript $ExportConfigScript $ExportT3ResumeScript'))
        self.assertIn('$rebootAfterArg = if ($script:UseRootKey) { "false" } else { "true" }', host)
        self.assertIn('$envPrefix += " REBOOT_AFTER_PROVISION=\'$rebootAfterArg\'"', host)
        self.assertIn("env PROVISION_PHASE=project-commands REBOOT_AFTER_PROVISION='$rebootAfterArg'", host)
        installer = (ROOT / 'bin' / 'install-ai-tools.sh').read_text()
        function = installer[installer.index('install_t3code() {'):]
        early_return = function.index('t3_can_skip_restart "${_wanted_t3_build}"')
        self.assertLess(function.index('/usr/local/bin/construct-t3-resume'), early_return)
        self.assertLess(function.index('systemctl enable --quiet construct-t3-resume.service'), early_return)
        unit = (ROOT / 'systemd' / 'construct-t3-resume.service').read_text()
        self.assertIn('Type=exec', unit)
        self.assertIn('ExecStart=/usr/bin/python3 -I /usr/local/bin/construct-t3-resume resume', unit)
        self.assertIn('ConditionPathExists=/var/lib/construct/t3-resume.json', unit)


if __name__ == '__main__':
    unittest.main()
