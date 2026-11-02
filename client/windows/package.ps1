param([ValidateSet('x64','x86')][string]$Architecture = 'x64', [string]$Output = (Join-Path $PSScriptRoot '../../dist/windows'))
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($Output)
$cores = Join-Path $packageRoot 'cores'
$temporary = Join-Path $env:RUNNER_TEMP ('personal-vpn-core-downloads-' + $Architecture)
New-Item -ItemType Directory -Force -Path $cores,$temporary | Out-Null
function Expand-Verified([string]$Url,[string]$Hash,[string]$Name) {
    $archive = Join-Path $temporary ($Name + '.zip')
    Invoke-WebRequest $Url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) { throw "$Name archive checksum mismatch" }
    $folder = Join-Path $temporary $Name
    Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
    return $folder
}
$is32 = $Architecture -eq 'x86'
Set-Content -LiteralPath (Join-Path $packageRoot ('architecture-' + $Architecture + '.txt')) -Value $Architecture -Encoding ascii
$xrayName = if ($is32) { '32' } else { '64' }
$xrayHash = if ($is32) { '9EA3A5849463F155B2484F8F32A9E8FD2A5628886571579D0C75D7FD0D888881' } else { '2E04A9995A6F2AD1B28C0BB25C708ECB2695E43A1C9C7889C4B6AC2B48CF7A6A' }
$xray = Expand-Verified "https://github.com/XTLS/Xray-core/releases/download/v26.10.31/Xray-windows-$xrayName.zip" $xrayHash 'xray'
Copy-Item -LiteralPath (Join-Path $xray 'xray.exe') -Destination $cores
Copy-Item -LiteralPath (Join-Path $xray 'LICENSE') -Destination (Join-Path $cores 'LICENSE-Xray.txt')
$goArch = if ($is32) { '386' } else { 'amd64' }
$hyHash = if ($is32) { '92A47F51B71234DAAA6E7DF139AD4FA35160338F47AB7A2EB99C930D54BD1819' } else { '162EF8FE55DC7EC810DDA662908F8E65EC36BD6DA39CB87C3BD66184AB68F067' }
$hy = Join-Path $cores 'hysteria.exe'
Invoke-WebRequest "https://github.com/HyNetworks/hysteria/releases/download/app/v2.13.0/hysteria-windows-$goArch.exe" -OutFile $hy
if ((Get-FileHash -LiteralPath $hy).Hash -ne $hyHash) { throw 'Hysteria checksum mismatch' }
Invoke-WebRequest 'https://raw.githubusercontent.com/HyNetworks/hysteria/app/v2.13.0/LICENSE.md' -OutFile (Join-Path $cores 'LICENSE-Hysteria.txt')
& (Join-Path $PSScriptRoot 'build-tun-core.ps1') -Architecture $Architecture -Destination (Join-Path $cores 'sing-box.exe')
Invoke-WebRequest 'https://raw.githubusercontent.com/SagerNet/sing-box/c005f35300ce76427dc285481ec5695f5b179cd2/LICENSE' -OutFile (Join-Path $cores 'LICENSE-sing-box.txt')
$wintun = Expand-Verified 'https://www.wintun.net/builds/wintun-0.14.1.zip' '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51' 'wintun'
$winArch = if ($is32) { 'x86' } else { 'amd64' }
$driver = Join-Path $wintun "wintun/bin/$winArch/wintun.dll"
if ((Get-AuthenticodeSignature -LiteralPath $driver).Status -ne 'Valid') { throw 'Wintun signature verification failed' }
Copy-Item -LiteralPath $driver -Destination $cores
Copy-Item -LiteralPath (Join-Path $wintun 'wintun/LICENSE.txt') -Destination (Join-Path $cores 'LICENSE-Wintun.txt')
foreach ($file in @('install.ps1','README.md','reset-tun.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packageRoot }
@'
Native components: verified downloads and a pinned source build with embedded runtime hash:
Xray-core v26.10.31, MPL-2.0: https://github.com/XTLS/Xray-core/tree/v26.10.31
Hysteria app/v2.13.0, MIT: https://github.com/HyNetworks/hysteria/tree/app/v2.13.0
sing-box v1.14.4 + Personal VPN OpenAdapter-only patch (build-tun-core.ps1), GPL-3.0: https://github.com/SagerNet/sing-box/tree/v1.14.4
Wintun 0.14.1, signed redistributable license: https://www.wintun.net/
.NET runtime / WPF: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
Application source: https://github.com/kittawat-thongpud/personal-vpn-server/tree/main/client/windows/app
'@ | Set-Content -LiteralPath (Join-Path $packageRoot 'THIRD-PARTY.txt') -Encoding utf8
