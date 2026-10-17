function ConvertTo-ConstructCurrentSettings {
    param($Settings)
    if ($null -eq $Settings) { return $null }
    $current = [pscustomobject]$Settings
    if (-not $current.PSObject.Properties['t3codePatched'] -and $current.PSObject.Properties['t3codeLimitResume']) {
        $current | Add-Member -NotePropertyName t3codePatched -NotePropertyValue $current.t3codeLimitResume
    }
    $current.PSObject.Properties.Remove('t3codeLimitResume')
    return $current
}
