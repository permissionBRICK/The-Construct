#Requires -Version 5.1
# The host-side extract of a VM config backup must not fail on the archive's
# symlinks. Windows tar.exe cannot create links with absolute Linux targets; the
# tar.exe double below fails an extract the same way whenever it produced a link.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'Provision-AgentVM.ps1'), [ref]$null, [ref]$null)
$fn = $ast.Find({ param($n)
    $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Expand-ConfigBackup'
}, $true)
if (-not $fn) { throw 'Missing Expand-ConfigBackup.' }
. ([scriptblock]::Create($fn.Extent.Text))

$tarImpl = if (Get-Command bsdtar -ErrorAction SilentlyContinue) { 'bsdtar' } else { 'tar' }
function tar.exe {
    & $tarImpl @args
    $code = $LASTEXITCODE
    if ($code -eq 0 -and $args[0] -eq '-xzf') {
        $dest = $args[[array]::IndexOf($args, '-C') + 1]
        $made = @(& find $dest -type l)
        if ($made.Count) { Write-Host "tar.exe double: Can't create '$($made -join "', '")'"; $code = 1 }
    }
    $global:LASTEXITCODE = $code
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('construct-backup-extract-test-' + [guid]::NewGuid().ToString('N'))
try {
    $stage = Join-Path $fixture 'stage'
    $null = New-Item -ItemType Directory -Path "$stage/home/.claude/skills/local", "$stage/home/.ssh", "$stage/home/.secrets", "$stage/projects"
    Set-Content -LiteralPath "$stage/backup-info.json" -Value '{}'
    Set-Content -LiteralPath "$stage/projects/demo.json" -Value '{"name":"demo"}'
    Set-Content -LiteralPath "$stage/home/.claude/skills/local/SKILL.md" -Value 'local skill'
    Set-Content -LiteralPath "$stage/home/.secrets/key" -Value 'secret'
    & ln -s /root/repos/tools/skills/demo "$stage/home/.claude/skills/demo"
    & ln -s /root/.secrets/key "$stage/home/.ssh/id_key"
    & ln -s /root/elsewhere "$stage/home/.ssh/odd[1]*name"
    $tgz = Join-Path $fixture 'backup.tar.gz'
    & tar -czf $tgz -C $stage .
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the fixture archive.' }

    $dest = Join-Path $fixture 'extracted'
    $null = New-Item -ItemType Directory -Path $dest
    $plain = Join-Path $fixture 'plain'
    $null = New-Item -ItemType Directory -Path $plain
    tar.exe -xzf $tgz -C $plain
    if ($LASTEXITCODE -eq 0) { throw 'The tar.exe double accepted an archive with links.' }
    Write-Host 'PASS: a plain extract fails on the links'

    Expand-ConfigBackup -Archive $tgz -Destination $dest
    foreach ($f in 'backup-info.json', 'projects/demo.json', 'home/.claude/skills/local/SKILL.md', 'home/.secrets/key') {
        if (-not (Test-Path -LiteralPath (Join-Path $dest $f) -PathType Leaf)) { throw "Regular file missing after extract: $f" }
    }
    Write-Host 'PASS: every regular file is extracted'
    if (@(& find $dest -type l).Count) { throw 'A symlink was extracted.' }
    Write-Host 'PASS: symlinks, including names with glob characters, are skipped'
    if (@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'construct-backup-links-*').Count) {
        throw 'The exclusion list was left behind.'
    }
    Write-Host 'PASS: the exclusion list is removed'

    $bad = Join-Path $fixture 'broken.tar.gz'
    Set-Content -LiteralPath $bad -Value 'not an archive'
    $failed = $false
    try { Expand-ConfigBackup -Archive $bad -Destination $dest } catch { $failed = $_.Exception.Message -like '*backup*' }
    if (-not $failed) { throw 'A corrupt archive did not fail the extract.' }
    Write-Host 'PASS: a corrupt archive fails the extract'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
