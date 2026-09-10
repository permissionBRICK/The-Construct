#!/usr/bin/env python3
"""Enroll an existing Construct guest, without running provisioning or restarting apps.

The elevated coordinator sends JSON on stdin over the existing, host-key-verified SSH
connection. Secrets never appear in process arguments or console output.
"""
import errno
import json
import os
from pathlib import Path
import re
import shlex
import socket
import ssl
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request


class EnrollmentError(ValueError):
    """A fixed, credential-free diagnostic safe to show in the setup console."""


def failure_message(error):
    # Never stringify arbitrary exceptions: URLs, HTTP bodies and subprocess
    # arguments can contain credentials. Report known categories and numeric codes.
    if isinstance(error, EnrollmentError):
        return str(error)
    if isinstance(error, UnicodeError):
        return "Enrollment input is not UTF-8. Update the Windows Construct client and retry."
    if isinstance(error, json.JSONDecodeError):
        return "Enrollment input is not valid JSON. Update the Windows Construct client and retry."
    if isinstance(error, urllib.error.HTTPError):
        return "Host identity API returned HTTP %d; verify host authentication and service logs." % error.code
    if isinstance(error, urllib.error.URLError):
        return failure_message(error.reason) if isinstance(error.reason, Exception) else "Guest could not connect to the host API."
    if isinstance(error, ssl.SSLCertVerificationError):
        return "Host TLS certificate verification failed (code %s); check its hostname and validity." % getattr(error, "verify_code", "unknown")
    if isinstance(error, ssl.SSLError):
        return "TLS negotiation with the host API failed."
    if isinstance(error, socket.gaierror):
        return "The guest cannot resolve the host address. Check guest DNS or use a reachable host address."
    if isinstance(error, TimeoutError):
        return "The guest connection to the host API timed out. Check routing and the host firewall on TCP 7462."
    if isinstance(error, ConnectionRefusedError):
        return "The host refused the guest connection on TCP 7462. Check that constructd is listening."
    if isinstance(error, subprocess.CalledProcessError):
        return "Guest heartbeat setup failed (systemctl exit %d). Check the guest systemd logs." % error.returncode
    if isinstance(error, OSError):
        return "Guest enrollment encountered an OS error (%s)." % errno.errorcode.get(error.errno, "unknown")
    return "Guest enrollment failed (%s)." % type(error).__name__


def update_config(text, values):
    lines = [line for line in text.splitlines() if line.partition("=")[0] not in values]
    return "\n".join(lines + [key + "=" + shlex.quote(str(value)) for key, value in values.items()]) + "\n"


def atomic_write(path, data, mode):
    fd, temporary = tempfile.mkstemp(prefix=".adopt-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as out:
            os.fchmod(out.fileno(), mode)
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def enroll(payload, root=Path("/"), verify=None, run=subprocess.run):
    name = payload["name"]
    if not re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", name):
        raise EnrollmentError("invalid instance name")
    if (root / "etc/machine-id").read_text().strip() != payload["machineId"]:
        raise EnrollmentError("guest identity changed; conversion stopped")
    url = payload["serviceUrl"]
    if not re.fullmatch(r"https://[A-Za-z0-9.-]+:7462", url):
        raise EnrollmentError("invalid service URL")
    if verify is None:
        context = ssl.create_default_context(cadata=payload["certificate"])
        request = urllib.request.Request(url + "/api/v1/vms/" + name + "/identity",
                                         headers={"Authorization": "Bearer " + payload["vmToken"]})
        with urllib.request.urlopen(request, context=context, timeout=20) as response:
            try:
                identity = json.load(response)
            except (json.JSONDecodeError, UnicodeError):
                raise EnrollmentError("Host identity API returned invalid JSON.") from None
        if identity.get("vmName") != name or identity.get("owner", "").lower() != payload["owner"].lower():
            raise EnrollmentError("host returned a different VM identity")
    else:
        verify(payload)
    config = root / "etc/construct/config.env"
    original = config.read_bytes()
    values = {"CONSTRUCT_SERVICE_URL": url, "CONSTRUCT_SERVICE_CA_FILE": "/etc/construct/service-ca.pem",
              "CONSTRUCT_INSTANCE_NAME": name, "CONSTRUCT_EXTERNAL_HOST": payload["publicHost"],
              "CONSTRUCT_EXTERNAL_SSH_PORT": payload["sshPort"]}
    backups = {}
    changes = {
        config.parent / "service-ca.pem": (payload["certificate"].encode(), 0o644),
        config.parent / "vm-token": (payload["vmToken"].encode(), 0o600),
        config: (update_config(original.decode(), values).encode(), 0o600),
    }
    for filename, contents in payload.get("files", {}).items():
        if filename not in ("construct", "construct-vm.sh", "construct-expose.sh", "construct-idle-report.sh"):
            raise EnrollmentError("unexpected guest helper")
        changes[root / "usr/local/bin" / filename] = (contents.encode(), 0o755)
    units = root / "etc/systemd/system"
    changes[units / "construct-idle-report.service"] = (b"[Unit]\nDescription=Construct activity heartbeat\nAfter=network-online.target\n[Service]\nType=oneshot\nUser=root\nExecStart=/usr/local/bin/construct-idle-report.sh\n", 0o644)
    changes[units / "construct-idle-report.timer"] = (b"[Unit]\nDescription=Construct activity heartbeat timer\n[Timer]\nOnBootSec=60\nOnUnitActiveSec=60\n[Install]\nWantedBy=timers.target\n", 0o644)
    try:
        for file, (data, mode) in changes.items():
            backups[file] = (file.read_bytes(), file.stat().st_mode & 0o777) if file.exists() else None
            atomic_write(file, data, mode)
        run(["systemctl", "daemon-reload"], check=True, capture_output=True)
        run(["systemctl", "enable", "--now", "construct-idle-report.timer"], check=True, capture_output=True)
    except Exception:
        if backups.get(units / "construct-idle-report.timer") is None:
            run(["systemctl", "disable", "--now", "construct-idle-report.timer"], check=False, capture_output=True)
        for file, old in backups.items():
            if old is None:
                file.unlink(missing_ok=True)
            else:
                atomic_write(file, *old)
        run(["systemctl", "daemon-reload"], check=False, capture_output=True)
        raise


if __name__ == "__main__":
    try:
        # Read strict UTF-8 bytes: Python stdin may otherwise use surrogateescape
        # and defer a damaged payload failure until helper files are encoded.
        enroll(json.loads(sys.stdin.buffer.read().decode("utf-8")))
        print("Guest enrolled; existing applications remain running.")
    except Exception as error:
        print("Guest enrollment failed: " + failure_message(error), file=sys.stderr)
        sys.exit(1)
