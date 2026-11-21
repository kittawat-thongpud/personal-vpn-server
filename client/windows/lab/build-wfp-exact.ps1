param([Parameter(Mandatory)][string] $Report)

$ErrorActionPreference = 'Stop'
$upstream = '0a0eb97f67d1dbcb3d08bda66d3b24f465d95475'
$kit = '10.0.28000.2526'
$targetPlatform = ("{0}.0" -f $kit.Substring(0, $kit.LastIndexOf('.')))
$source = Join-Path $env:RUNNER_TEMP ('pvn-wfp-exact-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $source | Out-Null
$patch = Join-Path $PSScriptRoot 'wfp-exact-executable.patch'

git -C $source init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize driver source.' }
git -C $source fetch --depth=1 https://github.com/mullvad/win-split-tunnel.git $upstream
if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch the pinned driver source.' }
git -C $source checkout --detach FETCH_HEAD
if ($LASTEXITCODE -ne 0 -or (git -C $source rev-parse HEAD) -ne $upstream) { throw 'Driver source pin verification failed.' }
git -C $source apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'The exact-executable patch no longer matches the pinned source.' }
git -C $source apply $patch
if ($LASTEXITCODE -ne 0) { throw 'Cannot apply the exact-executable patch.' }

$existing = Get-Content -Raw (Join-Path $source 'src/ioctl.cpp')
$arrivals = Get-Content -Raw (Join-Path $source 'src/procmgmt/procmgmt.cpp')
if ($existing -notmatch 'HasEntryExact\(context->RegisteredImage\.Instance, &Entry->ImageName\)' -or
    $arrivals -notmatch 'HasEntryExact\(registeredImage, &RegistryEntry->ImageName\)' -or
    $existing -match 'ON_BY_INHERITANCE|PropagateApplyTargetSettings' -or $arrivals -match 'ON_BY_INHERITANCE|REASON_BY_INHERITANCE') {
    throw 'Executable policy must match both registered and newly arriving processes without ancestry.'
}

$packages = @'
<?xml version="1.0" encoding="utf-8"?>
<packages>
  <package id="Microsoft.Windows.WDK.x64" version="@KIT@" targetFramework="native" />
  <package id="Microsoft.Windows.SDK.CPP.x64" version="@KIT@" targetFramework="native" />
  <package id="Microsoft.Windows.SDK.CPP" version="@KIT@" targetFramework="native" />
</packages>
'@
$props = @'
<Project>
  <Import Project="$(MSBuildThisFileDirectory)packages\Microsoft.Windows.WDK.x64.@KIT@\build\native\Microsoft.Windows.WDK.x64.props" />
  <Import Project="$(MSBuildThisFileDirectory)packages\Microsoft.Windows.SDK.CPP.x64.@KIT@\build\native\Microsoft.Windows.SDK.cpp.x64.props" />
  <Import Project="$(MSBuildThisFileDirectory)packages\Microsoft.Windows.SDK.CPP.@KIT@\build\native\Microsoft.Windows.SDK.cpp.props" />
</Project>
'@
Set-Content -LiteralPath (Join-Path $source 'packages.config') -Value $packages.Replace('@KIT@', $kit)
Set-Content -LiteralPath (Join-Path $source 'Directory.Build.props') -Value $props.Replace('@KIT@', $kit)
nuget.exe restore (Join-Path $source 'packages.config') -Source https://api.nuget.org/v3/index.json -PackagesDirectory (Join-Path $source 'packages') -NonInteractive
if ($LASTEXITCODE -ne 0) { throw 'Pinned Windows SDK / WDK restore failed.' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -latest -version '[18.0,19.0)' -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild for Windows kernel builds is unavailable.' }
$infFile = Join-Path $PSScriptRoot 'personal-vpn-bypass.inf'
$vcxproj = Join-Path $source 'src/mullvad-split-tunnel.vcxproj'
[xml]$projectSettings = Get-Content -LiteralPath $vcxproj -Raw
$ns = [System.Xml.XmlNamespaceManager]::new($projectSettings.NameTable)
$ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
$projectSettings.SelectSingleNode('//m:Inf[@Include]', $ns).SetAttribute('Include', 'personal-vpn-bypass.inf')
foreach ($node in $projectSettings.SelectNodes('//m:PreBuildEvent | //m:PostBuildEvent', $ns)) { [void]$node.ParentNode.RemoveChild($node) }
$projectSettings.Save($vcxproj)
Copy-Item -LiteralPath $infFile -Destination (Join-Path $source 'src/personal-vpn-bypass.inf')
$project = Join-Path $source 'src/mullvad-split-tunnel.sln'
& $msbuild $project /m /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x64 "/p:WindowsTargetPlatformVersion=$targetPlatform" /p:SignMode=Off /p:EnableInf2cat=false /p:TargetName=personal-vpn-bypass
if ($LASTEXITCODE -ne 0) { throw 'Windows exact-executable driver build failed.' }

$driver = Get-Item -LiteralPath (Join-Path $source 'bin/x64-Release/personal-vpn-bypass.sys')
$bytes = [IO.File]::ReadAllBytes($driver.FullName)
$header = [BitConverter]::ToInt32($bytes, 0x3c)
if ([BitConverter]::ToUInt32($bytes, $header) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes, $header + 4) -ne 0x8664) {
    throw 'Unexpected driver binary format or architecture.'
}
$candidate = Join-Path (Split-Path -Parent $Report) 'wfp-x64-unsigned'
$package = Join-Path $candidate 'driver'
New-Item -ItemType Directory -Path $candidate,$package | Out-Null
Copy-Item -LiteralPath $driver.FullName,$infFile -Destination $package
Copy-Item -LiteralPath (Join-Path $source 'LICENSE-MPL.txt'),$patch -Destination $candidate
$tools = Join-Path $source "packages/Microsoft.Windows.WDK.x64.$kit/c/bin/$targetPlatform"
$catalog = Get-ChildItem -LiteralPath $tools -Recurse -Filter Inf2Cat.exe -File | Select-Object -First 1
if (-not $catalog) { throw 'Inf2Cat.exe is unavailable in the pinned WDK.' }
& $catalog.FullName "/driver:$package" /os:10_VB_X64,10_NI_X64,10_GE_X64,Server2025_X64
$catalogFile = Join-Path $package 'personal-vpn-bypass.cat'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $catalogFile)) { throw 'The unsigned driver INF did not qualify for a catalog.' }

[ordered]@{
    result = 'PASS'
    upstream = $upstream
    wdk = $kit
    architecture = 'x64'
    patchSha256 = (Get-FileHash -LiteralPath $patch).Hash
    driverSha256 = (Get-FileHash -LiteralPath $driver.FullName).Hash
    infSha256 = (Get-FileHash -LiteralPath $infFile).Hash
    catalogSha256 = (Get-FileHash -LiteralPath $catalogFile).Hash
    signature = (Get-AuthenticodeSignature -LiteralPath $driver.FullName).Status.ToString()
    kernelLoaded = $false
} | ConvertTo-Json | Set-Content -LiteralPath $Report
