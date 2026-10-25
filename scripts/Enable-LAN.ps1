$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    exit
}
try {
    $appPath = Join-Path $PSScriptRoot 'LanBridge.exe'
    if (-not (Test-Path -LiteralPath $appPath)) { throw 'Place this script beside LanBridge.exe.' }
    $appPath = (Resolve-Path -LiteralPath $appPath).Path
    foreach ($protocol in @('TCP', 'UDP')) {
        $ruleName = 'LanBridge-LocalSubnet-' + $protocol
        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -Name $ruleName -DisplayName $ruleName -Direction Inbound -Program $appPath -Protocol $protocol -Action Allow -Profile Private -RemoteAddress LocalSubnet | Out-Null
    }
    Write-Host 'Done. Private-network / local-subnet rules added for this executable only.'
    Write-Host 'Restart LanBridge normally, NOT as administrator.'
} catch {
    Write-Error $_
} finally {
    Read-Host 'Press Enter to close'
}
