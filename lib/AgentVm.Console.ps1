# Local VMConnect credentials stay in the desktop user's DPAPI store. No host password is used.
function Get-ConstructConsoleAccountName {
    param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$')][string]$InstanceName)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { 'cvl' + [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($InstanceName.ToLowerInvariant()))).Replace('-','').ToLowerInvariant().Substring(0,17) }
    finally { $sha.Dispose() }
}
function New-ConstructConsolePassword {
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $bytes = New-Object byte[] 27; $rng.GetBytes($bytes); 'Aa1!' + [Convert]::ToBase64String($bytes) }
    finally { $rng.Dispose() }
}
function Get-ConstructVmConnectFingerprint {
    param([Guid]$VmId, [string]$HostName = '127.0.0.1', [int]$Port = 2179)
    $tcp = New-Object Net.Sockets.TcpClient
    try {
        if (-not $tcp.ConnectAsync($HostName,$Port).Wait(5000)) { throw 'VMConnect unavailable' }
        $stream = $tcp.GetStream(); $stream.ReadTimeout = 5000; $stream.WriteTimeout = 5000
        $blob = [Text.Encoding]::Unicode.GetBytes(([string]$VmId) + [char]0)
        $packet = New-Object IO.MemoryStream
        $binary = New-Object IO.BinaryWriter($packet)
        $binary.Write([uint32](18 + $blob.Length)); $binary.Write([uint32]0)
        $binary.Write([uint32]2); $binary.Write([uint32]0)
        $binary.Write([uint16](([string]$VmId).Length + 1)); $binary.Write($blob)
        $bytes = $packet.ToArray(); $stream.Write($bytes,0,$bytes.Length)
        $binary.Dispose(); $packet.Dispose()
        $ssl = New-Object Net.Security.SslStream($stream,$false,({$true} -as [Net.Security.RemoteCertificateValidationCallback]))
        try {
            $ssl.ReadTimeout = 5000; $ssl.WriteTimeout = 5000
            $ssl.AuthenticateAsClient('localhost')
            $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
            $sha = [Security.Cryptography.SHA256]::Create()
            try { 'sha256:' + [BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-',':').ToLowerInvariant() }
            finally { $sha.Dispose(); $cert.Dispose() }
        } finally { $ssl.Dispose() }
    } finally { $tcp.Dispose() }
}
function Get-ConstructVmSwitchHostAddress {
    param([string]$VmName)
    $adapter = Get-VMNetworkAdapter -VMName $VmName | Select-Object -First 1
    $ip = Get-NetIPAddress -InterfaceAlias "vEthernet ($($adapter.SwitchName))" -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ip) { [string]$ip.IPAddress } else { '' }
}
function Set-ConstructConsoleAccess {
    param([string]$InstanceName, [string]$VmName, [switch]$Reset)
    $name = Get-ConstructConsoleAccountName $InstanceName
    $vm = Get-VM -Name $VmName -ErrorAction Stop
    # LocalUser descriptions are limited to 48 characters. The hashed account name identifies the instance.
    $description = "ConstructL:$($vm.Id)"
    $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if ($user -and $user.Description -notmatch '^ConstructL:[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$') { throw 'Console account name collision' }
    $password = Get-ConstructRemoteToken -Slug "console-$InstanceName"
    if (-not $user -or -not $password -or $Reset) {
        $password = New-ConstructConsolePassword
        $secure = ConvertTo-SecureString $password -AsPlainText -Force
        if ($user) { Set-LocalUser -Name $name -Password $secure -UserMayChangePassword $true -Description $description }
        else { $user = New-LocalUser -Name $name -Password $secure -PasswordNeverExpires -AccountNeverExpires -Description $description }
        Save-ConstructRemoteToken -Slug "console-$InstanceName" -Token $password | Out-Null
    }
    # A reinstall changes the VM GUID. Remove the obsolete grant before recording the new one.
    if ($user.Description -ne $description) {
        Revoke-VMConnectAccess -VMId ([Guid]$user.Description.Split(':')[1]) -UserName "$env:COMPUTERNAME\$name" -ErrorAction SilentlyContinue
        Set-LocalUser -Name $name -Description $description
    }
    if (-not @(Get-VMConnectAccess -VMId $vm.Id -UserName "$env:COMPUTERNAME\$name").Count) {
        Grant-VMConnectAccess -VMId $vm.Id -UserName "$env:COMPUTERNAME\$name" | Out-Null
    }
    $adapter = Get-VMNetworkAdapter -VMName $VmName | Select-Object -First 1
    if (-not $adapter.SwitchName) { throw 'The VM has no connected Hyper-V switch' }
    # Own a scoped rule; enabling a stock rule could expose 2179 on unrelated interfaces.
    $rule = "Construct-Console-$name"
    Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $rule -DisplayName "Construct VMConnect console ($InstanceName)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 2179 -InterfaceAlias "vEthernet ($($adapter.SwitchName))" | Out-Null
}
function Remove-ConstructConsoleAccount {
    param([string]$InstanceName)
    Remove-ConstructRemoteToken -Slug "console-$InstanceName" | Out-Null
    $name = Get-ConstructConsoleAccountName $InstanceName
    $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if ($user -and $user.Description -match '^ConstructL:[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$') {
        Revoke-VMConnectAccess -VMId ([Guid]$user.Description.Split(':')[1]) -UserName "$env:COMPUTERNAME\$name" -ErrorAction SilentlyContinue
        Remove-LocalUser -Name $name
    }
    Get-NetFirewallRule -Name "Construct-Console-$name" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
function Invoke-ConstructConsoleHandoff {
    param([string]$InstanceName, [string]$VmName,
        [scriptblock]$VmLookup = { param($n) Get-VM -Name $n -ErrorAction Stop },
        [scriptblock]$GrantLookup = { param($id,$user) Get-VMConnectAccess -VMId $id -UserName $user -ErrorAction Stop },
        [scriptblock]$ChangePassword = { param($n,$old,$new) ([ADSI]"WinNT://./$n,user").ChangePassword($old,$new) },
        [scriptblock]$ReadToken = { param($slug) Get-ConstructRemoteToken -Slug $slug },
        [scriptblock]$Store = { param($slug,$pw) Save-ConstructRemoteToken -Slug $slug -Token $pw | Out-Null },
        [scriptblock]$Fingerprint = { param($id) Get-ConstructVmConnectFingerprint -VmId $id },
        [scriptblock]$HostAddress = { param($n) Get-ConstructVmSwitchHostAddress -VmName $n })
    $name = Get-ConstructConsoleAccountName $InstanceName
    # Extension and Companion share this store. Serialize read/change/write across processes.
    $mutex = New-Object Threading.Mutex($false, "Local\ConstructConsole-$name")
    $held = $false
    try {
        try { $held = $mutex.WaitOne(20000) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'Console credential is busy; try again' }
        $vm = & $VmLookup $VmName
        if ([string]$vm.State -ne 'Running') { return (@{ error='vm-not-running' } | ConvertTo-Json -Compress) }
        $old = & $ReadToken "console-$InstanceName"
        if (-not $old) { return (@{ setupRequired=$true; reason='no-credential' } | ConvertTo-Json -Compress) }
        $grants = @(& $GrantLookup $vm.Id "$env:COMPUTERNAME\$name")
        if ($grants.Count -eq 0) { return (@{ setupRequired=$true; reason='grant-missing' } | ConvertTo-Json -Compress) }
        $password = New-ConstructConsolePassword; $rotated = $true
        try { & $ChangePassword $name $old $password | Out-Null }
        catch {
            # ERROR_PASSWORD_RESTRICTION is the only fallback. Bad credentials require repair.
            $code = $_.Exception.GetBaseException().HResult -band 0xffff
            if ($code -eq 1325 -or $code -eq 2245) { $password = $old; $rotated = $false }
            else { return (@{ setupRequired=$true; reason='credential-out-of-sync' } | ConvertTo-Json -Compress) }
        }
        if ($rotated) { & $Store "console-$InstanceName" $password | Out-Null }
        try { $fp = & $Fingerprint $vm.Id } catch { return (@{ error='vmconnect-unreachable' } | ConvertTo-Json -Compress) }
        $address = & $HostAddress $VmName
        @{ vmId=[string]$vm.Id; username=$name; domain=$env:COMPUTERNAME; password=$password; certificateFingerprint=$fp; hostAddress=[string]$address; rotated=$rotated } | ConvertTo-Json -Compress
    } finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}
