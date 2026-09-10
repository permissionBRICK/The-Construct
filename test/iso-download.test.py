#!/usr/bin/env python3
"""Real HTTP fault injection against the PowerShell/.NET downloader; no internet needed."""
import hashlib
import http.server
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
DATA = bytes(range(256)) * 32768
SHA = hashlib.sha256(DATA).hexdigest()

class Origin(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    lock = threading.Lock()
    requests = []
    active = 0
    peak = 0
    faults = set()

    def log_message(self, *_): pass

    def do_GET(self):
        mode = self.path.strip('/')
        if mode == 'redirect':
            self.send_response(302); self.send_header('Location', '/parallel'); self.send_header('Content-Length', '0'); self.end_headers(); return
        match = re.fullmatch(r'bytes=(\d+)-(\d+)', self.headers.get('Range', ''))
        ranged = match and mode not in ('single', 'unknown')
        lo, hi = map(int, match.groups()) if ranged else (0, len(DATA)-1)
        probe = ranged and lo == hi == 0
        with self.lock:
            self.requests.append((mode, lo, hi, self.headers.get('If-Range')))
            fault = mode in ('stall', 'short', 'always-stall') and not probe and (mode == 'always-stall' or mode not in self.faults)
            if fault: self.faults.add(mode)
        self.send_response(206 if ranged else 200)
        if ranged:
            self.send_header('Content-Range', f'bytes {lo + (1 if mode == "bad-range" and not probe else 0)}-{hi}/{len(DATA)}')
        if mode != 'no-validator': self.send_header('ETag', '"changed"' if mode == 'changed' and not probe else '"fixture-v1"')
        if mode != 'unknown': self.send_header('Content-Length', str(hi-lo+1))
        else: self.send_header('Connection', 'close'); self.close_connection = True
        self.end_headers()
        if probe:
            try: self.wfile.write(DATA[:1])
            except (BrokenPipeError, ConnectionResetError): pass
            return
        with self.lock:
            type(self).active += 1
            type(self).peak = max(self.peak, self.active)
        try:
            for at in range(lo, hi+1, 65536):
                self.wfile.write(DATA[at:min(hi+1, at+65536)]); self.wfile.flush()
                if fault:
                    if mode in ('stall', 'always-stall'): time.sleep(2.5)
                    self.close_connection = True
                    return
                time.sleep(.004)
        except (BrokenPipeError, ConnectionResetError): pass
        finally:
            with self.lock: type(self).active -= 1

class DownloadTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Origin)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls): cls.server.shutdown(); cls.server.server_close()

    def download(self, mode, *, succeeds=True, checksum=SHA, retries=3):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory)/'sample.iso'
            dest.write_bytes(b'previous complete ISO')
            script = Path(directory)/'run.ps1'
            quote = lambda s: "'"+str(s).replace("'", "''")+"'"
            script.write_text("$ErrorActionPreference='Stop'\n. "+quote(ROOT/'lib/Construct.Download.ps1')+"\n"+
                f"Receive-ConstructIso -Uri 'http://127.0.0.1:{self.server.server_port}/{mode}' -OutFile {quote(dest)} -IdleTimeoutSeconds 1 -Retries {retries} -ExpectedSha256 '{checksum}'\n")
            result = subprocess.run([os.environ.get('POWERSHELL_EXE', 'pwsh'),'-NoProfile','-File',str(script)],capture_output=True,text=True,timeout=45)
            self.assertEqual(result.returncode == 0, succeeds, result.stdout+result.stderr)
            self.assertEqual(dest.read_bytes(), DATA if succeeds else b'previous complete ISO')
            self.assertEqual(list(Path(directory).glob('*.part')), [])
            self.assertIn('streams', result.stdout)
            return result.stdout

    def test_eight_streams(self):
        before = len(Origin.requests)
        self.download('parallel')
        requests = [r for r in Origin.requests[before:] if r[0]=='parallel' and r[1]!=r[2]]
        self.assertEqual(len(requests),8)
        self.assertTrue(all(r[3]=='"fixture-v1"' for r in requests))
        self.assertGreaterEqual(Origin.peak, 8)

    def test_stall_resumes_at_saved_offset(self):
        output=self.download('stall')
        requests=[r for r in Origin.requests if r[0]=='stall' and r[1]!=r[2]]
        self.assertTrue(any(r[1] % (len(DATA)//8) != 0 for r in requests))
        self.assertIn('1 retries',output)

    def test_truncated_stream_resumes(self): self.download('short')
    def test_no_range_support(self):
        self.download('single')
        requests = [r for r in Origin.requests if r[0]=='single']
        self.assertEqual(len(requests), 2) # range probe, then one complete response

    def test_unknown_length(self): self.download('unknown')
    def test_redirect(self): self.download('redirect')
    def test_missing_validator_falls_back_safely(self): self.download('no-validator')
    def test_bad_range_never_published(self): self.download('bad-range',succeeds=False)
    def test_changed_resource_never_published(self): self.download('changed',succeeds=False)
    def test_bad_checksum_preserves_previous_iso(self): self.download('checksum',succeeds=False,checksum='0'*64)
    def test_cancellation_closes_streams_and_preserves_destination(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory)/'cancel.iso'
            dest.write_bytes(b'previous complete ISO')
            quote = lambda value: "'"+str(value).replace("'", "''")+"'"
            script = Path(directory)/'cancel.ps1'
            script.write_text("$ErrorActionPreference='Stop'\nAdd-Type -AssemblyName System.Net.Http\n$source = " + quote(ROOT/'lib/Construct.Download.cs') + "\nif ($PSVersionTable.PSVersion.Major -le 5) { Add-Type -Path $source -ReferencedAssemblies System.Net.Http } else { Add-Type -Path $source -IgnoreWarnings -WarningAction SilentlyContinue }\n" +
                "$t = [Construct.Download.Transfer]::new('http://127.0.0.1:"+str(self.server.server_port)+"/always-stall', "+quote(dest)+", 8, 5, 3, '')\n" +
                "try { $limit = [DateTime]::UtcNow.AddSeconds(10); while ($t.Downloaded -eq 0 -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 20 }; if ($t.Downloaded -eq 0) { throw 'No transfer started' }; $t.Cancel(); try { $null = $t.Completion.GetAwaiter().GetResult(); throw 'Cancellation ignored' } catch { if ($_.Exception.Message -eq 'Cancellation ignored') { throw } } } finally { $t.Dispose() }\n")
            result = subprocess.run([os.environ.get('POWERSHELL_EXE', 'pwsh'),'-NoProfile','-File',str(script)],capture_output=True,text=True,timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
            self.assertEqual(dest.read_bytes(), b'previous complete ISO')
            self.assertEqual(list(Path(directory).glob('*.part')), [])

    def test_retry_exhaustion_preserves_previous_iso(self): self.download('always-stall',succeeds=False,retries=1)

if __name__ == '__main__': unittest.main(verbosity=2)
