# Browser console for Hyper-V guests

`construct vm console NAME --web` opens a VM-scoped browser viewer through Apache
Guacamole 1.6.0 and Hyper-V VMConnect. It works at boot and in ISO installers,
without guest networking or a guest remote-desktop agent. The guest VM continues
to run directly on the Windows Hyper-V host.

## Installation

The browser console is enabled by default on the Windows host. Normal primary
VM provisioning installs the Linux gateway and its pinned guacd container;
no separate project profile or enable command is needed.

```bash
construct vm console NAME --web
```

Existing hosts without a `Constructd:BrowserConsoleEnabled` setting gain the
default at the next host service update. An explicit `false` remains disabled,
including when rerunning the host installer. Administrators can still use
`service/host/Enable-ConstructBrowserConsole.ps1 -Disable` (and restart the
service) to disable it, or omit `-Disable` to re-enable it.

For an existing primary, reprovision to install the gateway, or run
`bash /path/to/construct/console-viewer/install.sh` with Docker running. TCP
2179 must be reachable from the primary. The gateway obtains the VMConnect
certificate fingerprint through the authenticated host API.

The command uses `construct expose` to print a working client-forwarded link.
The viewer listens on port 6080. Links default to 24 hours (1440 minutes).
Use `--minutes` for a shorter lifetime, from 5 to 1440 minutes. This does not
extend the guest's VM lease. Opening a new link after a
page refresh is intentional: the fragment is removed from browser history once
redeemed. Reconnect works from the current page while its link remains valid.

The gateway is installed under `/opt/construct/console-viewer` and managed by
`construct-console-viewer.service`. guacd is a separate, digest-pinned container,
bound **only to 127.0.0.1:4822**, without guest-supplied connection settings.
The optional `construct-browser-console` profile remains available for older
provisioners; current service-managed primaries install the gateway automatically.

Optional machine-local settings in `/etc/construct/console-viewer.env`:

```sh
CONSTRUCT_CONSOLE_PORT='6080'
CONSTRUCT_CONSOLE_KEYBOARD_LAYOUT='en-us-qwerty'
# For a direct HTTPS deployment, provide both certificate and key:
# CONSTRUCT_CONSOLE_TLS_CERT='/etc/construct/console-cert.pem'
# CONSTRUCT_CONSOLE_TLS_KEY='/etc/construct/console-key.pem'
```

Use the client-forwarded localhost link, or configure HTTPS for direct remote
access. The link grants control of exactly one VM until it expires. The optional
settings must be provisioned from machine-local configuration, not fixed values
in a profile shared between different hosts.

## Session and credential boundaries

- Only the root-only Unix control socket can mint links. The browser never sees
  the primary VM token or Windows password. Link secrets arrive in a URL fragment
  and are exchanged for an HttpOnly, SameSite cookie scoped to that connection.
- The gateway creates an ordinary host console session and calls the new
  `POST /api/v1/vms/{name}/console/sessions/{sid}/connection` endpoint. All existing
  console authorization, principal binding, parent fences, and session caps apply.
  Primary-token gateways may connect to themselves, their own children, and
  host-shared children they are authorized to operate. The connection endpoint
  uses the same sharing rules as screenshots/input; it requires a session owned
  by the requesting principal. Private VMs remain inaccessible to other gateways.
- The host creates a random-password local account, with no group membership,
  and grants VMConnect access only to the selected native VM GUID. Credentials
  are sent over the pinned host API to the trusted primary gateway, then over
  pinned TLS to VMConnect. They are never logged or written to a credential file.
- The gateway renews the 60-second host session every 20 seconds and closes the
  stream if authorization/renewal fails or the link expires. Disconnect deletes
  the host session, revokes VMConnect permission, and removes the account.
- Accounts expire 30 seconds after their last session expiry. A host cleanup loop
  reaps revoked/expired sessions and expired orphan accounts every 30 seconds.
  Account expiry alone is not a forced disconnect for an existing RDP connection;
  the trusted gateway enforces stream lifetime. The primary VM is therefore part
  of the trusted console infrastructure, not an untrusted public gateway.
- File transfer, clipboard transfer, and audio are disabled for this initial
  console. Keyboard uses Guacamole's RDP key mapping rather than WMI TypeText.
  Mouse behavior still depends on the guest integration/boot environment.

## Validation

```bash
/usr/bin/python3 -m unittest discover -s console-viewer -p 'test_*.py' -v
dotnet test service/tests/Constructd.Tests/Constructd.Tests.csproj
```

Tests cover ticket/origin rejection, VM targeting, secret exclusion from browser
traffic, forbidden protocol instructions, active-stream expiry, disconnect
cleanup, host authorization and credential lifecycle. See the dated field-test
notes for the real STANDPC browser test.

Optional local rendering checks use Playwright with Chromium:
`python console-viewer/test_rendering.py -v`. Set `CONSTRUCT_TEST_CHROMIUM` to an
existing Chromium executable if needed. These exercise native image decoding,
frame cleanup, failed images, cancellation, reconnect and the non-WebCodecs
fallback without a host or VM. Ordinary Python test discovery skips them when
Playwright is unavailable.

## Third-party assets

`static/guacamole-1.6.0.min.js` is Apache's `guacamole-common-js/all.min.js`, extracted
from the official 1.6.0 WAR with one marked, readable Construct patch to
`Display.drawStream`. Upstream leaves decoded `VideoFrame` objects open and decode
rejections block its display queue. The patch closes frames after drawing or
cancellation, closes decoders, stops feeding closed streams, and surfaces errors
through `display.onerror`. The viewer cancels queued rendering on disconnect.
Apache license/notice files are included. Keep this patch when replacing the
bundle unless upstream has fixed these paths; run the rendering checks above.
WAR: https://archive.apache.org/dist/guacamole/1.6.0/binary/guacamole-1.6.0.war
SHA-256: `b41ceb1e2df010b54db563e0b00edb8d5fe9f073c6168462e4c978df0fc6e716`
Original JS SHA-256: `cc89f710ecc544477dbe6bfea453fab752dafa1b1ab9770f523676e7b744b44a`
Patched JS SHA-256: `89657877ac1c06f958f6f8f83b0b1811f464389c5c054c0f55d634639aa64fd5`
guacd image: `guacamole/guacd@sha256:8974eaa9ba32f713daf311e7cc8cd7e4cdfba1edea39eed75524e78ef4b08f4f`
