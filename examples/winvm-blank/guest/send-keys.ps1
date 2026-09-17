# Types text into the FOREGROUND window of the interactive session via SendKeys.
#
# Why: QMP synthetic scancodes (winvm/vm/input.sh type) are mangled by the guest's
# GERMAN keyboard layout, and the guest clipboard does NOT cross into a Remote
# Utilities session. SendKeys works at the character level and is relayed by the RU
# viewer into the remote machine, so it is the only reliable way to fill remote
# dialogs (VPN credentials, Windows lock screen, app fields).
#
# Secrets never travel on a command line: -File points at a temp file whose content
# is typed and which is deleted immediately afterwards (unless -KeepFile).
#
#   powershell -File send-keys.ps1 -File C:\tmp\p.txt -Then '{ENTER}'
#   powershell -File send-keys.ps1 -Text 'ambro-ch' -Then '{TAB}'
#
# Always run through run-interactive.ps1 (needs the interactive desktop) and always
# verify the target field has focus by screenshot BEFORE calling this.
param(
    [string]$File,                 # file whose (trimmed) content is typed - for secrets
    [string]$Text,                 # literal text to type - for non-secrets
    [string]$Then,                 # raw SendKeys tokens appended, e.g. '{TAB}' / '{ENTER}'
    [int]$DelayMs = 25,            # per-character delay; RU relay drops keys when too fast
    [int]$StartDelayMs = 400,      # settle time before the first keystroke
    [string]$FocusWindow,          # window-title substring to bring to the foreground first
    [switch]$KeepFile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

# run-interactive.ps1 starts this script through a scheduled task, and the spawned
# powershell console TAKES THE FOREGROUND - without re-focusing the target window the
# keystrokes would land in that console. Always pass -FocusWindow when typing into a
# remote session (e.g. -FocusWindow 'hp test01').
Add-Type -Namespace Win32 -Name Fg -MemberDefinition @'
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@
if ($FocusWindow) {
    $target = Get-Process |
        Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$FocusWindow*" } |
        Select-Object -First 1
    if (-not $target) { throw "send-keys: no window matching '$FocusWindow'" }
    [Win32.Fg]::ShowWindow($target.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
    [Win32.Fg]::SetForegroundWindow($target.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600
    Write-Host "FOCUSED: $($target.MainWindowTitle)"
}

if ($File) {
    if (-not (Test-Path -LiteralPath $File)) { throw "send-keys: file not found: $File" }
    # -Raw keeps embedded specials intact; trailing CRLF from scp/Set-Content is stripped.
    $Text = (Get-Content -LiteralPath $File -Raw) -replace '\r?\n$', ''
    if (-not $KeepFile) { Remove-Item -LiteralPath $File -Force }
}
if (-not $Text -and -not $Then) { throw 'send-keys: nothing to type (-File/-Text/-Then all empty)' }

Start-Sleep -Milliseconds $StartDelayMs

# SendKeys metacharacters must be wrapped in braces; wrapping is uniform and also
# produces the correct escapes for the braces themselves ('{' -> '{{}', '}' -> '{}}').
$special = '+^%~(){}[]'
foreach ($ch in $Text.ToCharArray()) {
    $token = if ($special.Contains([string]$ch)) { '{' + $ch + '}' } else { [string]$ch }
    [System.Windows.Forms.SendKeys]::SendWait($token)
    Start-Sleep -Milliseconds $DelayMs
}

if ($Then) {
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait($Then)
}

Write-Host ("SENT_KEYS chars={0}{1}" -f $Text.Length, $(if ($Then) { " then=$Then" } else { '' }))
exit 0
