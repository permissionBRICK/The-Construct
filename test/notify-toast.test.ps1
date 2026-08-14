#Requires -Version 5.1
<#
    Behavioural tests for the toast script that extension/src/notify.js generates
    (buildToastScript). Run:

        pwsh -NoProfile -File test/notify-toast.test.ps1

    The real script talks to WinRT, which only exists on Windows — so this parses the
    generated script (a syntax error would be invisible until it reached a desktop)
    and then runs it against C# STUBS of the three WinRT types it touches. That is
    enough to exercise every branch of the part that actually broke in the field: how
    a notifier is chosen, what happens when its Setting is not 'Enabled', when the
    custom AppUserModelId is rejected, and which exit code the host gets told.

    The stubbing is deliberately minimal: the only edit to the generated script is
    dropping the three WinRT type-load lines (assembly-qualified literals that cannot
    resolve off Windows). Everything else — the selection loop, the Show() calls, the
    exit codes — is the real generated code.
#>
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0
function ok($name, $cond, $detail) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else { $script:fail++; Write-Host ("  FAIL  $name" + $(if ($detail) { "   << $detail" } else { "" })) -ForegroundColor Red }
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host "node is required to generate the toast script — skipping"
    exit 0
}

# ── the script under test, straight from the module the extension ships ──────────
$gen = @'
const n = require(process.argv[2]);
process.stdout.write(n.buildToastScript({ level: "info", title: "t", body: "b", source: "root@vm" }));
'@
$genFile = Join-Path ([System.IO.Path]::GetTempPath()) ("toastgen-" + [guid]::NewGuid() + ".js")
Set-Content -LiteralPath $genFile -Value $gen -Encoding UTF8
$src = & node $genFile (Join-Path $repoRoot "extension/src/notify.js")
Remove-Item -LiteralPath $genFile -Force
if ($src -is [array]) { $src = $src -join "`n" }

# ── it must at least parse: a syntax error here is invisible until a real toast ──
$errs = $null; $toks = $null
[System.Management.Automation.Language.Parser]::ParseInput($src, [ref]$toks, [ref]$errs) | Out-Null
ok "the generated script parses" ($errs.Count -eq 0) ($errs -join "; ")

# ── stubs ────────────────────────────────────────────────────────────────────────
# CreateToastNotifier/Setting/Show behaviour is driven per scenario through env vars,
# so one stub source serves every case. Show() prints to STDOUT; the script itself
# only ever writes stderr, so the two never collide.
$stubs = @'
# The per-app mute flag lives in a registry hive this host does not have; shadow the
# one cmdlet that reads it so the "user switched us off in Settings" path is testable.
function Get-ItemProperty {
    [CmdletBinding()] param([string]$Path, [string]$Name)
    if ($env:STUB_MUTED -eq "1" -and $Path -like "*Notifications*Settings*") { return [pscustomobject]@{ Enabled = 0 } }
    return $null
}
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
namespace Windows.Data.Xml.Dom {
  public class XmlDocument {
    public string Xml;
    public void LoadXml(string s) {
      if (s == null || s.IndexOf("<toast") != 0) throw new Exception("payload is not a toast");
      Xml = s;
    }
  }
}
namespace Windows.UI.Notifications {
  public static class Stub {
    public static List<string> Split(string v) {
      var o = new List<string>();
      if (!String.IsNullOrEmpty(v)) foreach (var p in v.Split('|')) if (p.Length > 0) o.Add(p);
      return o;
    }
    // "id=Setting" pairs; an id that is absent behaves like an unknown app id.
    public static string SettingOf(string id) {
      foreach (var pair in Split(Environment.GetEnvironmentVariable("STUB_SETTINGS"))) {
        int i = pair.IndexOf('=');
        if (i > 0 && pair.Substring(0, i) == id) return pair.Substring(i + 1);
      }
      return "Missing";
    }
    public static bool Listed(string name, string id) {
      return Split(Environment.GetEnvironmentVariable(name)).Contains(id);
    }
  }
  public class ToastNotification {
    public object Xml;
    public ToastNotification(object xml) { Xml = xml; }
  }
  public class ToastNotifier {
    public string Id;
    public ToastNotifier(string id) { Id = id; }
    public string Setting {
      get {
        string s = Stub.SettingOf(Id);
        if (s == "throw") throw new Exception("setting unavailable");
        return s;
      }
    }
    public void Show(ToastNotification t) {
      if (Stub.Listed("STUB_SHOW_THROWS", Id)) throw new Exception("show refused for " + Id);
      if (t == null || t.Xml == null) throw new Exception("no payload");
      Console.Out.WriteLine("SHOWN " + Id);
    }
  }
  public static class ToastNotificationManager {
    public static ToastNotifier CreateToastNotifier(string id) {
      if (Stub.Listed("STUB_NO_NOTIFIER", id)) throw new Exception("element not found for " + id);
      return new ToastNotifier(id);
    }
  }
}
"@
'@

# Drop only the assembly-qualified WinRT type-load lines (they cannot resolve off
# Windows); the stubs above provide the same type names to everything that follows.
$body = ($src -split "`n" | Where-Object { $_ -notmatch "ContentType=WindowsRuntime" }) -join "`n"

$appId = "PermissionBrick.TheConstruct"
$fallbackId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("toasttest-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    $runner = Join-Path $tmp "run.ps1"
    Set-Content -LiteralPath $runner -Value ($stubs + "`n" + $body) -Encoding UTF8

    # Run one scenario: returns the exit code plus stdout/stderr of the real script.
    function Invoke-Toast([hashtable]$env0, [string]$file) {
        $out = Join-Path $tmp "out.txt"; $err = Join-Path $tmp "err.txt"
        foreach ($k in @("STUB_SETTINGS", "STUB_SHOW_THROWS", "STUB_NO_NOTIFIER", "STUB_MUTED")) {
            [Environment]::SetEnvironmentVariable($k, $(if ($env0.ContainsKey($k)) { $env0[$k] } else { "" }))
        }
        $p = Start-Process -FilePath (Get-Process -Id $PID).Path `
            -ArgumentList @("-NoProfile", "-File", $(if ($file) { $file } else { $runner })) `
            -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
        $stderr = (Get-Content -Raw -LiteralPath $err -ErrorAction SilentlyContinue)
        [pscustomobject]@{
            Code = $p.ExitCode
            Out  = (Get-Content -Raw -LiteralPath $out -ErrorAction SilentlyContinue)
            Err  = $stderr
            # There is no HKCU on a non-Windows test host, so the (non-fatal) app id
            # registration always complains here. Drop that one line when asking what
            # the script had to say about the toast ITSELF.
            Notes = (($stderr -split "`n" | Where-Object { $_.Trim() -and $_ -notmatch "app id registration failed" }) -join "`n")
        }
    }

    # 1. The happy path: our own app id is accepted, nothing to report.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=Enabled|$fallbackId=Enabled" }
    ok "an Enabled app id shows the toast under our own identity" `
        ($r.Code -eq 0 -and $r.Out -match [regex]::Escape("SHOWN $appId")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    ok "a clean toast says nothing about the toast on stderr" ([string]::IsNullOrWhiteSpace($r.Notes)) $r.Notes

    # 2. THE FIELD BUG: a freshly HKCU-registered app id reports DisabledForApplication
    #    even though notifications are fine. The old code exited 2 here and downgraded
    #    to a VS Code notification; now the always-registered PowerShell app id carries
    #    the toast, and the host is told why it looks different.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=DisabledForApplication|$fallbackId=Enabled" }
    ok "an unrecognised app id falls back to the registered one instead of giving up" `
        ($r.Code -eq 0 -and $r.Out -match [regex]::Escape("SHOWN $fallbackId")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    ok "the fallback identity is reported" ($r.Err -match "fallback app id") $r.Err

    # 3. Neither id is Enabled: still show it (the setting is advisory, and a toast
    #    that might not appear beats one that certainly will not), and say so.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=DisabledForApplication|$fallbackId=DisabledForApplication" }
    ok "with nothing Enabled the toast is still attempted, preferring our own id" `
        ($r.Code -eq 0 -and $r.Out -match [regex]::Escape("SHOWN $appId")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    ok "the odd notifier setting is reported" ($r.Err -match "notifier setting is DisabledForApplication") $r.Err

    # 4. A Setting that throws is the same "Windows has not seen us yet" case.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=throw|$fallbackId=Enabled" }
    ok "a notifier whose Setting throws does not abort the toast" `
        ($r.Code -eq 0 -and $r.Out -match "SHOWN ") "code=$($r.Code) out=$($r.Out) err=$($r.Err)"

    # 5. A definitive, user/machine-wide disable: fall back to VS Code (exit 2) rather
    #    than fire a toast into a muted system.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=DisabledForUser|$fallbackId=DisabledForUser" }
    ok "notifications off for the user exit 2, showing nothing" `
        ($r.Code -eq 2 -and -not ($r.Out -match "SHOWN")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    ok "the suppression names the reason" ($r.Err -match "DisabledForUser") $r.Err
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=DisabledByGroupPolicy|$fallbackId=DisabledByGroupPolicy" }
    ok "a group policy disable exits 2 as well" ($r.Code -eq 2 -and $r.Err -match "DisabledByGroupPolicy") "code=$($r.Code) err=$($r.Err)"

    # 5a. …and a definitive disable vetoes the whole attempt, not just the identity that
    #     reported it: these settings are user/machine-wide, so slipping the same toast
    #     out under the other identity would be walking around the user's own switch.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=DisabledForUser|$fallbackId=Enabled" }
    ok "a user-wide disable is not routed around via an Enabled fallback identity" `
        ($r.Code -eq 2 -and -not ($r.Out -match "SHOWN")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=DisabledByGroupPolicy|$fallbackId=DisabledForApplication" }
    ok "a policy disable is not routed around via a merely-unrecognised identity" `
        ($r.Code -eq 2 -and -not ($r.Out -match "SHOWN")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=Enabled|$fallbackId=DisabledForUser" }
    ok "a disable reported by the fallback identity vetoes it too" `
        ($r.Code -eq 2 -and -not ($r.Out -match "SHOWN")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"

    # 5b. `DisabledForApplication` is ALSO what Windows reports once the user really has
    #     switched us off in Settings, so the deliberate case is read where it is
    #     unambiguous: the per-app Enabled flag the Settings UI writes. A mute must be
    #     obeyed — including not sneaking the toast out under the fallback identity.
    $r = Invoke-Toast @{ STUB_MUTED = "1"; STUB_SETTINGS = "$appId=Enabled|$fallbackId=Enabled" }
    ok "a deliberate mute in Windows settings is obeyed (exit 2, nothing shown anywhere)" `
        ($r.Code -eq 2 -and -not ($r.Out -match "SHOWN")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"
    ok "the mute is reported as a suppression, not a failure" ($r.Err -match "switched off for $([regex]::Escape($appId))") $r.Err

    # 6. Show() itself failing on the preferred id must not lose the notification.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=Enabled|$fallbackId=Enabled"; STUB_SHOW_THROWS = $appId }
    ok "a Show() that throws retries the next candidate" `
        ($r.Code -eq 0 -and $r.Out -match [regex]::Escape("SHOWN $fallbackId")) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"

    # 7. Nothing works: one exit code, one reason, no silence.
    $r = Invoke-Toast @{ STUB_SETTINGS = "$appId=Enabled|$fallbackId=Enabled"; STUB_SHOW_THROWS = "$appId|$fallbackId" }
    ok "when every candidate refuses to show, exit 1 with the last error" `
        ($r.Code -eq 1 -and $r.Notes -match "toast failed: .*show refused") "code=$($r.Code) err=$($r.Err)"
    $r = Invoke-Toast @{ STUB_NO_NOTIFIER = "$appId|$fallbackId" }
    ok "no notifier at all exits 1 with the reason" `
        ($r.Code -eq 1 -and $r.Err -match "no toast notifier available") "code=$($r.Code) err=$($r.Err)"

    # 8. Constrained language mode cannot construct WinRT objects — the UNSTUBBED
    #    script must say so by exit code alone (a constrained session may not even
    #    call [Console]::Error.WriteLine, which is why that line is best-effort).
    $clm = Join-Path $tmp "clm.ps1"
    Set-Content -LiteralPath $clm -Value ("`$ExecutionContext.SessionState.LanguageMode='ConstrainedLanguage'`n" + $src) -Encoding UTF8
    $r = Invoke-Toast @{} $clm
    ok "a constrained language mode session exits 4 instead of failing obscurely" `
        ($r.Code -eq 4) "code=$($r.Code) out=$($r.Out) err=$($r.Err)"

    # 9. On a host with no WinRT at all (the unstubbed script, off Windows) the reason
    #    reaches the caller instead of a bare non-zero exit.
    $plain = Join-Path $tmp "plain.ps1"
    Set-Content -LiteralPath $plain -Value $src -Encoding UTF8
    $r = Invoke-Toast @{} $plain
    if ($IsWindows) {
        ok "(skipped off-Windows check: this IS Windows)" $true
    } else {
        ok "a host without the WinRT notification types exits 3 with the reason" `
            ($r.Code -eq 3 -and $r.Err -match "WinRT notifications are unavailable") "code=$($r.Code) err=$($r.Err)"
        ok "a registry write that cannot work is survivable, not fatal" `
            ($r.Err -match "app id registration failed") $r.Err
    }
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("  toast script tests — {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
if ($script:fail -gt 0) { exit 1 }
exit 0
