#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("adopt", Path(__file__).resolve().parents[1] / "bin/adopt-host.py")
adopt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adopt)


class GuestAdoptionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for directory in ["etc/construct", "etc/systemd/system", "usr/local/bin"]:
            (self.root / directory).mkdir(parents=True)
        (self.root / "etc/machine-id").write_text("a" * 32)
        self.config = self.root / "etc/construct/config.env"
        self.config.write_text("T3CODE='true'\nWORKSPACE_ROOT='/root/repos'\n")
        self.payload = dict(name="agent-vm", owner="PC\\alice", machineId="a" * 32,
                            serviceUrl="https://main-pc:7462", publicHost="main-pc", sshPort=2201,
                            certificate="certificate fixture", vmToken="secret fixture", files={"construct": "#!/bin/sh\n"})
        self.calls = []

    def run_command(self, args, **kwargs):
        self.calls.append(args)

    def test_enroll_preserves_apps_and_is_repeatable(self):
        for _ in range(2):
            adopt.enroll(self.payload, self.root, verify=lambda _: None, run=self.run_command)
        text = self.config.read_text()
        self.assertIn("T3CODE='true'", text)
        self.assertIn("WORKSPACE_ROOT='/root/repos'", text)
        self.assertEqual(text.count("CONSTRUCT_SERVICE_URL="), 1)
        self.assertEqual((self.config.parent / "vm-token").stat().st_mode & 0o777, 0o600)
        self.assertFalse(any("t3code" in " ".join(call) or "reboot" in call for call in self.calls))

    def test_wrong_guest_identity_writes_nothing(self):
        self.payload["machineId"] = "b" * 32
        with self.assertRaises(ValueError):
            adopt.enroll(self.payload, self.root, verify=lambda _: None, run=self.run_command)
        self.assertEqual(list(self.config.parent.iterdir()), [self.config])

    def test_host_verification_failure_writes_nothing(self):
        def refuse(_):
            raise ValueError("wrong host")
        with self.assertRaises(ValueError):
            adopt.enroll(self.payload, self.root, verify=refuse, run=self.run_command)
        self.assertEqual(list(self.config.parent.iterdir()), [self.config])

    def test_failed_timer_setup_restores_old_configuration_and_credentials(self):
        token = self.config.parent / "vm-token"
        token.write_text("old secret")
        original = self.config.read_bytes()
        def fail(args, **kwargs):
            if "enable" in args:
                raise subprocess.CalledProcessError(1, args)
        with self.assertRaises(subprocess.CalledProcessError):
            adopt.enroll(self.payload, self.root, verify=lambda _: None, run=fail)
        self.assertEqual(self.config.read_bytes(), original)
        self.assertEqual(token.read_text(), "old secret")
        self.assertFalse((self.config.parent / "service-ca.pem").exists())


if __name__ == "__main__":
    unittest.main()
