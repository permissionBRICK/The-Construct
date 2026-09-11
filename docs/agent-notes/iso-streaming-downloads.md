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
retries, and cancellation against a local HTTP fixture with generated data.
Keep these tests local and manual: Christoph requested removal of the GitHub
downloader workflow on 2026-09-10 because recurring CI is unnecessary for this
component. Run with Python 3 and PowerShell installed; set `POWERSHELL_EXE=pwsh`
for PowerShell 7 or `POWERSHELL_EXE=powershell` for Windows PowerShell 5.1.
Native STANDPC PowerShell
5.1 tests also passed parallel downloads, stall recovery, single-stream fallback,
unknown length, bad ranges, and changed validators. UI smoke tests passed 318
checks; lifecycle launcher checks passed 265 cases.

A real HTTPS download on STANDPC under Windows PowerShell 5.1 fetched the
69,206,016-byte Alpine 3.24.1 ISO in 5.3 seconds, displayed eight active streams,
and verified SHA256 `e73a6241bd5f3c5c2d4d38c02cc52c378c0415a7c888bd292066bf36e0f41a39`.
The temporary test ISO was deleted afterward. This is a functionality check;
no comparison against the previous downloader or company-network throughput was
performed.

## Ubuntu minimal-install source: real range verification

On 2026-09-10, the installer's release-directory lookup selected
`https://releases.ubuntu.com/24.04/ubuntu-24.04.5-live-server-amd64.iso`.
Construct's `ubuntu-server-minimal` option selects content from this server ISO;
it does not use a separate miniature download. The origin returned a total size
of 4,080,486,400 bytes and a strong ETag to the one-byte probe.

Windows PowerShell 5.1 on STANDPC then issued eight simultaneous 1 MiB requests
at offsets 0, 510060800, 1020121600, 1530182400, 2040243200, 2550304000,
3060364800, and 3570425600. Every response was HTTP 206, carried the exact requested
Content-Range, matched the probe's ETag, and delivered 1,048,576 bytes. All requests
started within 14 ms; the first completed after 1,932 ms and the last after 2,077 ms.
Thus all eight overlapped. This verifies real Ubuntu-origin parallel range support
from Windows using 8 MiB of traffic; it is not a full-image throughput benchmark.

Before the workflow was removed, fault-injection CI passed all 12 cases on both
Linux/PowerShell 7 and Windows/PowerShell 5.1 (run 34513759658).
The host release build also passed.
Activation on main-pc remains through Update Construct; its relay did not respond
to the final discovery probe.

## Remote host downloads (2026-09-11)

WS009's first remote create stopped reporting after `downloading the source ISO
from releases.ubuntu.com`. Screenshots confirmed the host catalog had no source,
no current image, and a zero-byte reserved output. There was no live access to
measure download speed or establish a network bottleneck. The service's former
`HttpIsoDownloader` made one HTTP request and silently copied the response body;
its HttpClient timeout covered header acquisition, with no body idle timeout.
The client's flashing PowerShell progress was its JSON job polling, not ISO bytes.

The host now compiles the same `lib/Construct.Download.cs` engine used by the local
installer. The adapter reports progress every two seconds, uses eight streams,
five-second body idle deadlines and six retries, and propagates job cancellation.
Existing source checksum validation and atomic cache publication remain in place.
Source selection, hashing and waiting behind another build report their stages.
Client API polls suppress PowerShell's unrelated transfer popup. The media panel
labels empty unpublished catalog reservations `not yet published`.

The original local installer intentionally deletes its downloaded stock Ubuntu
ISO after building VM-specific media. Conversion reuses a stock ISO if it remains
and matches the configured release checksum; it does not import a VM-specific
patched image as generic host media. The first remote create therefore may need
one host-side download and build. Later creates reuse matching published media;
rebuilds reuse the stock source cached on the host unless Redownload is requested.
No Ubuntu ISO is downloaded to or uploaded from the remote install client.

Validation includes real loopback HTTP tests of the host adapter for parallel
downloads, single-stream fallback, stalled-range resumption, invalid ranges and
cancellation/cleanup; the existing local downloader fault suite also passes.
Activation requires updating the Windows host service. An already running create
continues with its original downloader until it finishes or is cancelled.
