#!/usr/bin/env python3
"""Project port forwards: the runtime-config merge and bin/project-forwards.sh.
Local only: a stub stands in for `construct expose`, and the end-to-end case drives
the real CLI in local mode against a temporary spool. No host service is contacted.
Run: python3 test/project-forwards.test.py
"""
import json
import os
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'bin/project-forwards.sh'
GENERATOR = ROOT / 'bin/generate-runtime-config.sh'
# An agent shell on a service-managed VM exports these; the CLI reads the environment
# before its config file, so they would point the end-to-end case at the real service.
VM_VARS = ['CONSTRUCT_SERVICE_URL', 'CONSTRUCT_INSTANCE_NAME', 'CONSTRUCT_SERVICE_CA_FILE',
           'CONSTRUCT_EXTERNAL_HOST', 'CONSTRUCT_EXTERNAL_SSH_PORT', 'CONSTRUCT_EXPOSE_DEFAULT_TARGET']

STUB = '''#!/usr/bin/env bash
printf '%s\\n' "$(printf '%q ' "$@")" >>"${STUB_CALLS}"
rc="$(jq -r --arg p "$1" '.[$p] // 0' <<<"${STUB_CODES:-{\\}}")"
scheme=http; [[ -n "${STUB_HTTPS:-}" && " $* " == *" --https-if-available "* ]] && scheme=https
if [[ "${rc}" == 0 ]]; then printf '%s://localhost:%s/\\n' "${scheme}" "$1"; else printf 'stub diagnostic for %s\\n' "$1" >&2; fi
exit "${rc}"
'''


class ProjectForwardsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='construct-forwards-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.agent = self.root / 'agent'
        (self.agent / 'runtime').mkdir(parents=True)
        self.store = self.agent / 'projects'
        self.store.mkdir()
        self.generated = self.agent / 'runtime/generated.json'
        self.config = self.root / 'config.env'
        self.config.write_text(f'AGENT_HOME={shlex.quote(str(self.agent))}\n')
        self.calls = self.root / 'calls'
        self.calls.touch()
        self.stub = self.root / 'expose-stub.sh'
        self.stub.write_text(STUB)
        env = {k: v for k, v in os.environ.items() if k not in VM_VARS}
        self.env = dict(env, CONFIG_FILE=str(self.config), AGENT_HOME=str(self.agent), REPO_DIR=str(ROOT),
                        ALLOW_HOST_PACKAGES='false', STUB_CALLS=str(self.calls),
                        CONSTRUCT_EXPOSE_SCRIPT=str(self.stub))

    def profiles(self, **forwards_by_profile):
        for name, forwards in forwards_by_profile.items():
            profile = dict(name=name)
            if forwards is not None:
                profile['forwards'] = forwards
            (self.store / (name + '.json')).write_text(json.dumps(profile))
        subprocess.run(['bash', str(GENERATOR)], env=dict(self.env, PROJECTS=','.join(forwards_by_profile)),
                       check=True, capture_output=True)
        return json.loads(self.generated.read_text())['forwards']

    def run_script(self, **env):
        return subprocess.run(['bash', str(SCRIPT)], env=dict(self.env, **env), capture_output=True, text=True)

    def test_merge_keeps_profile_identity_and_requests_a_port_once_per_target(self):
        merged = self.profiles(
            z=[dict(port=5173, label='vite'), dict(port=9000, to='host')],
            a=[dict(port=5173, to='client', label='other'), dict(port=5173, to='host')],
            none=None)
        self.assertEqual(merged, [
            dict(profile='z', port=5173, to='client', label='vite'),
            dict(profile='z', port=9000, to='host', label=''),
            dict(profile='a', port=5173, to='host', label=''),
        ])

    def test_merge_makes_https_forwards_host_forwards_and_keeps_names_apart(self):
        merged = self.profiles(
            z=[dict(port=5173, https=True, name='app', label='vite'), dict(port=5173, https=True, upstreamTls=True),
               dict(port=5173, to='host')],
            a=[dict(port=5173, https=True, to='host', name='app'), dict(port=5173, https=True, name='other'),
               dict(port=8443, https=False, to='host')])
        self.assertEqual(merged, [
            dict(profile='z', port=5173, to='host', label='vite', https=True, name='app', upstreamTls=False),
            dict(profile='z', port=5173, to='host', label='', https=True, name='', upstreamTls=True),
            dict(profile='z', port=5173, to='host', label=''),
            dict(profile='a', port=5173, to='host', label='', https=True, name='other', upstreamTls=False),
            dict(profile='a', port=8443, to='host', label=''),
        ])

    def test_profiles_without_forwards_merge_to_an_empty_list(self):
        self.assertEqual(self.profiles(plain=None), [])
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('No project declares a port forward', result.stdout)
        self.assertEqual(self.calls.read_text(), '')

    def test_each_forward_is_requested_get_or_create_without_waiting(self):
        self.profiles(web=[dict(port=5173, label='vite dev'), dict(port=9000, to='host')])
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.calls.read_text().splitlines(), [
            "5173 --to client --reuse --wait 0 --label vite\\ dev ",
            "9000 --to host --reuse --wait 0 ",
        ])
        self.assertIn('5173 (client, vite dev; web): http://localhost:5173/', result.stdout)

    def test_an_https_forward_is_requested_if_available_with_its_name_and_upstream_tls(self):
        self.profiles(web=[dict(port=5173, https=True, name='app', upstreamTls=True, label='vite dev'),
                           dict(port=5174, https=True)])
        result = self.run_script(STUB_HTTPS='1')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.calls.read_text().splitlines(), [
            "5173 --to host --reuse --wait 0 --label vite\\ dev --https-if-available --name app --upstream-tls ",
            "5174 --to host --reuse --wait 0 --https-if-available ",
        ])
        self.assertIn('5173 (https app, vite dev; web): https://localhost:5173/', result.stdout)
        self.assertIn('5174 (https; web): https://localhost:5174/', result.stdout)
        self.assertNotIn('plain host forward', result.stdout)

    def test_an_https_forward_the_host_cannot_serve_says_it_is_plain(self):
        self.profiles(web=[dict(port=5173, https=True)])
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('5173 (https; web): http://localhost:5173/', result.stdout)
        self.assertIn('the host offers no HTTPS forward for this VM, so this is a plain host forward', result.stdout)

    def test_queued_is_success_and_a_failure_warns_without_stopping_the_rest(self):
        self.profiles(web=[dict(port=1001), dict(port=1002, to='host'), dict(port=1003), dict(port=1004)])
        result = self.run_script(STUB_CODES=json.dumps({'1001': 6, '1002': 7, '1003': 8}))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(len(self.calls.read_text().splitlines()), 4)
        self.assertIn('1001 (client; web): queued', result.stdout)
        self.assertIn('1002 (host; web): refused by the host service', result.stderr)
        self.assertIn('    stub diagnostic for 1002', result.stderr)
        self.assertIn('1003 (client; web): not opened (construct expose exited 8)', result.stderr)
        self.assertNotIn('stub diagnostic for 1001', result.stderr)
        self.assertIn('1004 (client; web): http://localhost:1004/', result.stdout)

    def test_real_cli_in_local_mode_spools_each_client_forward_once(self):
        # The real `construct expose` in local mode: client forwards go to the spool
        # (queued, exit 6), a host forward is stateless, and a second provision
        # reuses the queued requests instead of adding new ones.
        spool = self.root / 'forwards'
        self.profiles(web=[dict(port=5173, label='vite dev'), dict(port=8080, to='host'), dict(port=3000),
                           dict(port=8443, https=True, name='app')])
        env = dict(CONSTRUCT_EXPOSE_SCRIPT=str(ROOT / 'bin/construct-expose.sh'), CONSTRUCT_FORWARDS_DIR=str(spool),
                   CONSTRUCT_EXTERNAL_HOST='vm-1.example.test')
        for _ in range(2):
            result = self.run_script(**env)
            self.assertEqual(result.returncode, 0, result.stderr)
        requests = [json.loads(p.read_text()) for p in sorted((spool / 'requests').glob('*.json'))]
        self.assertEqual(sorted((r['vmPort'], r['label'], r['target']) for r in requests),
                         [(3000, '', 'client'), (5173, 'vite dev', 'client')])
        self.assertIn('8080 (host; web): http://vm-1.example.test:8080/', result.stdout)
        # Without a host service an HTTPS entry is the plain host forward, not an error.
        self.assertIn('8443 (https app; web): http://vm-1.example.test:8443/', result.stdout)


if __name__ == '__main__':
    unittest.main(verbosity=1)
