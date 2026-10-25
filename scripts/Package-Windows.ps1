$ErrorActionPreference = 'Stop'
$stage = Join-Path $PSScriptRoot '../package-stage'
New-Item $stage -ItemType Directory -Force | Out-Null
Copy-Item "$PSScriptRoot/../release/win-x64/LanBridge.exe" $stage
foreach ($name in @('README.md', 'WINDOWS-ACCEPTANCE.md', 'LICENSE', 'VERSION.json')) {
    Copy-Item "$PSScriptRoot/../$name" $stage
}
foreach ($name in @('Enable-LAN.cmd', 'Enable-LAN.ps1', 'Test-Drag.cmd')) {
    Copy-Item "$PSScriptRoot/$name" $stage
}
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $runtime = Get-ChildItem (Join-Path $packageRoot $package) -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $runtime) { throw "Missing runtime notices: $package" }
    $notices = Join-Path $stage "notices/$package"
    New-Item $notices -ItemType Directory -Force | Out-Null
    $files = Get-ChildItem $runtime.FullName -File | Where-Object { $_.Name -match 'LICENSE|ThirdPartyNotices' }
    if (-not $files) { throw "Missing license files: $package" }
    $files | Copy-Item -Destination $notices
}
Compress-Archive -Path "$stage/*" -DestinationPath "$PSScriptRoot/../artifacts/LanBridge-Windows-x64.zip" -Force
