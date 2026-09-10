# Installer ISO downloads and panel preparation

The initial Ubuntu ISO download and Redownload path in `Auto-Install.ps1` use
`lib/Construct.Download.ps1` and its C# streaming engine. No BITS service or extra
runtime installation is required; Windows PowerShell 5.1 and PowerShell 7 work.

The downloader probes with `Range: bytes=0-0`, then divides the file among eight
streams when the origin supplies valid ranges and a strong ETag or Last-Modified
validator. Every range and validator is checked. A five-second body idle timeout
closes the response; a stream resumes at its last written byte after a one-second
pause, up to six retries. Header acquisition has a separate 15-second minimum
limit. A server without safe range support uses one stream; retries restart that
stream. Redirects use the normal HTTP client behavior. TLS validation remains on.

The console displays aggregate bytes, percentage, current throughput, ETA, active
streams, and retry count. Progress updates at most four times per second, with a
plain text status every two seconds for consoles that hide PowerShell progress.
Checksum verification occurs before publication when the Ubuntu checksum is
available, respecting the existing SkipChecksum option. A unique temporary file
is published only on success; failure and cancellation preserve any previous ISO
and remove the partial download. Resuming across separate installer processes is
not implemented.

Both VS Code panel surfaces immediately disable lifecycle buttons and animate the
clicked action while configuration preparation runs. The existing confirmation
still follows preparation. Completion, blocked preparation, and errors clear the
loading state; no provisioning or destructive action is started by the spinner.

Validation: `python test/iso-download.test.py` exercises eight concurrent streams,
resume after stalls/truncation, redirects, single-stream and unknown-length
responses, changed resources, malformed ranges, checksum failures, exhausted
retries, and cancellation against a local HTTP fixture. The CI workflow runs it
under PowerShell 7 on Linux and Windows PowerShell 5.1. Native STANDPC PowerShell
5.1 tests also passed parallel downloads, stall recovery, single-stream fallback,
unknown length, bad ranges, and changed validators. UI smoke tests passed 318
checks; lifecycle launcher checks passed 265 cases.
