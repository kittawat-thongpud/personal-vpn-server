param([string]$Output = (Join-Path $PSScriptRoot '../../dist/windows'))
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($Output)
$cores = Join-Path $packageRoot 'cores'
New-Item -ItemType Directory -Force -Path $cores | Out-Null
$temporary = Join-Path $env:RUNNER_TEMP 'personal-vpn-core-downloads'
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$archive = Join-Path $temporary 'Xray-windows-64.zip'
Invoke-WebRequest 'https://github.com/XTLS/Xray-core/releases/download/v26.10.31/Xray-windows-64.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '2E04A9995A6F2AD1B28C0BB25C708ECB2695E43A1C9C7889C4B6AC2B48CF7A6A') { throw 'Xray archive checksum mismatch' }
$expanded = Join-Path $temporary 'xray'
Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
Copy-Item -LiteralPath (Join-Path $expanded 'xray.exe') -Destination (Join-Path $cores 'xray.exe')
Copy-Item -LiteralPath (Join-Path $expanded 'LICENSE') -Destination (Join-Path $cores 'LICENSE-Xray.txt')
$hy = Join-Path $cores 'hysteria.exe'
Invoke-WebRequest 'https://github.com/HyNetworks/hysteria/releases/download/app/v2.13.0/hysteria-windows-amd64.exe' -OutFile $hy
if ((Get-FileHash -LiteralPath $hy -Algorithm SHA256).Hash -ne '162EF8FE55DC7EC810DDA662908F8E65EC36BD6DA39CB87C3BD66184AB68F067') { throw 'Hysteria checksum mismatch' }
Invoke-WebRequest 'https://raw.githubusercontent.com/HyNetworks/hysteria/app/v2.13.0/LICENSE.md' -OutFile (Join-Path $cores 'LICENSE-Hysteria.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $packageRoot
@'
Native core sources (unmodified binaries; pinned hashes are checked at build and connection time):
Xray-core v26.10.31, Mozilla Public License 2.0
https://github.com/XTLS/Xray-core/tree/v26.10.31
https://github.com/XTLS/Xray-core/releases/tag/v26.10.31
Hysteria app/v2.13.0, MIT License
https://github.com/HyNetworks/hysteria/tree/app/v2.13.0
https://github.com/HyNetworks/hysteria/releases/tag/app/v2.13.0
.NET runtime: https://github.com/dotnet/runtime / https://github.com/dotnet/wpf
App source: https://github.com/kittawat-thongpud/personal-vpn-server/tree/main/client/windows/app
'@ | Set-Content -LiteralPath (Join-Path $packageRoot 'THIRD-PARTY.txt') -Encoding utf8
