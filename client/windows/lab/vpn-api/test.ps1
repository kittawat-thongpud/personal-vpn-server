$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'The Windows VPN API lab requires a disposable GitHub-hosted runner.'
}

$lab = Join-Path $env:RUNNER_TEMP 'PersonalVpnApiLab'
$sample = Join-Path $lab 'MicrosoftSample'
$stage = Join-Path $lab 'package'
$evidence = Join-Path $env:GITHUB_WORKSPACE 'vpn-api-evidence'
New-Item -ItemType Directory -Path $lab, $stage, $evidence | Out-Null
$revision = 'd589fe0f57af13e052c44c662ade5fb1da2bcbb0'
git clone --filter=blob:none --no-checkout https://github.com/microsoft/UwpVpnPluginSample.git $sample
if ($LASTEXITCODE -ne 0) { throw 'Microsoft sample clone failed.' }
git -C $sample checkout --detach $revision
if ($LASTEXITCODE -ne 0 -or (git -C $sample rev-parse HEAD) -ne $revision) { throw 'Sample revision mismatch.' }
Copy-Item (Join-Path $sample 'LICENSE') (Join-Path $evidence 'MicrosoftSample-LICENSE.txt')

$app = Join-Path $sample 'CSharp/TestVpnPluginApp'
$background = Join-Path $sample 'CSharp/TestVpnPluginAppBg'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LabApp.cs') -Destination (Join-Path $app 'App.xaml.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LabVpnPlugin.cs') -Destination (Join-Path $background 'VpnPlugin.cs')
$manifestPath = Join-Path $app 'Package.appxmanifest'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifest.Package.Identity.Name = 'PersonalVpn.WindowsVpnApiLab'
$manifest.Package.Identity.Publisher = 'CN=Personal VPN API Lab'
$manifest.Package.Properties.DisplayName = 'Personal VPN API Lab'
$manifest.Package.Properties.PublisherDisplayName = 'Personal VPN API Lab'
$manifest.Package.Dependencies.TargetDeviceFamily.MinVersion = '10.0.22000.0'
$manifest.Package.Dependencies.TargetDeviceFamily.MaxVersionTested = '10.0.26100.0'
$manifest.Save($manifestPath)
$appProject = Join-Path $app 'TestVpnPluginApp.csproj'
[xml]$project = Get-Content -LiteralPath $appProject -Raw
# This property belongs to the app. Passing it globally also tries to pack
# the referenced Windows Runtime component, which has no Pack target.
$packageOnBuild = $project.CreateElement('GenerateAppxPackageOnBuild', $project.DocumentElement.NamespaceURI)
$packageOnBuild.InnerText = 'true'
$project.Project.PropertyGroup[0].AppendChild($packageOnBuild) | Out-Null
$project.Save($appProject)

$sdkRoot = ${env:ProgramFiles(x86)} + '/Windows Kits/10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'bin') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64/signtool.exe') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK and SignTool not found.' }
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) { throw 'Visual Studio MSBuild not found.' }
& $msbuild $appProject /restore /m /v:minimal /p:Configuration=Debug /p:Platform=x64 "/p:TargetPlatformVersion=$($sdk.Name)" /p:TargetPlatformMinVersion=10.0.22000.0 /p:UseDotNetNativeToolchain=false /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never /p:AppxAutoIncrementPackageRevision=false /p:UapAppxPackageBuildMode=SideloadOnly "/p:AppxPackageDir=$stage\"
if ($LASTEXITCODE -ne 0) { throw 'The UWP test package did not build.' }
$packages = @(Get-ChildItem -LiteralPath $stage -Recurse -File |
    Where-Object { $_.Extension -in '.appx', '.msix' -and $_.DirectoryName -notmatch '[\\/]Dependencies[\\/]' })
if ($packages.Count -ne 1) {
    Get-ChildItem -LiteralPath $stage -Recurse -File | Select-Object -ExpandProperty Name
    throw 'Expected one x64 UWP test package.'
}
$package = $packages[0]

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LabActivation {
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ActivationManager {}
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationManager {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string args, int options, out uint pid);
    }
    public static uint Launch(string appId, string args) {
        var manager = (IActivationManager)(object)new ActivationManager();
        uint pid;
        Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, args, 0, out pid));
        return pid;
    }
}
'@

function Read-Log([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path }
}

function Wait-Log([string]$Path, [string]$Expected, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $lines = @(Read-Log $Path)
        if ($lines -match '^error |^channel-error ') { throw ($lines -join [Environment]::NewLine) }
        if ($lines -contains $Expected) { return }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for '$Expected'."
}

function Invoke-Probe([string]$Name) {
    $path = Join-Path $lab "$Name.exe"
    $process = Start-Process -FilePath $path -ArgumentList @(
        '--noproxy', '*', '--connect-timeout', '4', '--max-time', '7',
        '--silent', '--output', 'NUL', 'https://1.1.1.1/cdn-cgi/trace'
    ) -PassThru -Wait -WindowStyle Hidden
    return $process.ExitCode
}

$rows = [System.Collections.Generic.List[object]]::new()
$packageInfo = $null
$identity = $null
$installCertificate = $null
$localLog = $null
$channelLog = $null
$result = 'FAIL'
try {
    $signer = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Personal VPN API Lab' -CertStoreLocation 'Cert:\CurrentUser\My'
    $labCert = Join-Path $lab 'VpnApiLab.cer'
    Export-Certificate -Cert $signer -FilePath $labCert | Out-Null
    $installCertificate = Import-Certificate -FilePath $labCert -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
    & (Join-Path $sdk.FullName 'x64/signtool.exe') sign /fd SHA256 /sha1 $signer.Thumbprint $package.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Temporary lab app package signing failed.' }
    $dependencies = @(Get-ChildItem -LiteralPath (Join-Path $package.DirectoryName 'Dependencies/x64') -File -ErrorAction SilentlyContinue).FullName
    if ($dependencies.Count) { Add-AppxPackage -Path $package.FullName -DependencyPath $dependencies }
    else { Add-AppxPackage -Path $package.FullName }
    $packageInfo = Get-AppxPackage -Name 'PersonalVpn.WindowsVpnApiLab'
    if (!$packageInfo) { throw 'The UWP package was not registered.' }
    $identity = $packageInfo.PackageFamilyName + '!App'
    $localState = Join-Path $env:LOCALAPPDATA "Packages/$($packageInfo.PackageFamilyName)/LocalState"
    $localLog = Join-Path $localState 'profile.log'
    $channelLog = Join-Path $localState 'channel.log'
    [LabActivation]::Launch($identity, 'init') | Out-Null
    Wait-Log $localLog 'initialized'
    $probes = @('probe-a', 'probe-b')
    foreach ($probe in $probes) {
        $exe = Join-Path $lab "$probe.exe"
        Copy-Item -LiteralPath (Get-Command curl.exe).Source -Destination $exe
        [IO.File]::WriteAllText((Join-Path $localState "$probe.txt"), $exe)
        $exit = Invoke-Probe $probe
        $rows.Add([ordered]@{ phase = 'baseline'; probe = $probe; exitCode = $exit })
        if ($exit -ne 0) { throw "Direct HTTPS baseline failed ($probe, code $exit)." }
    }
    foreach ($selected in $probes) {
        [LabActivation]::Launch($identity, $selected) | Out-Null
        Wait-Log $localLog "profile-added $selected" 40
        Wait-Log $localLog "connected $selected" 60
        Wait-Log $channelLog "channel-started $selected"
        $capturePattern = "^captured $selected 172\.31\.254\.2 tcp/443$"
        $exit = Invoke-Probe $selected
        $captures = @(Read-Log $channelLog | Where-Object { $_ -match $capturePattern }).Count
        $rows.Add([ordered]@{ phase = $selected; probe = $selected; exitCode = $exit; captured = $captures })
        if ($exit -eq 0 -or $captures -eq 0) { throw "The selected process did not stay in the test tunnel ($selected)." }
        # Drain selected SYN retransmissions before inspecting the other app.
        Start-Sleep -Seconds 3
        $before = @(Read-Log $channelLog | Where-Object { $_ -match $capturePattern }).Count
        $excluded = $probes | Where-Object { $_ -ne $selected }
        $exit = Invoke-Probe $excluded
        $after = @(Read-Log $channelLog | Where-Object { $_ -match $capturePattern }).Count
        $rows.Add([ordered]@{ phase = $selected; probe = $excluded; exitCode = $exit; captured = $after - $before })
        if ($exit -ne 0 -or $after -ne $before) { throw "The nonselected process did not retain its direct path ($excluded)." }
    }
    $result = 'PASS'
}
finally {
    if ($identity -and $localLog) {
        try {
            [LabActivation]::Launch($identity, 'cleanup') | Out-Null
            Wait-Log $localLog 'cleaned'
            foreach ($probe in @('probe-a', 'probe-b')) {
                $exit = Invoke-Probe $probe
                $rows.Add([ordered]@{ phase = 'cleanup'; probe = $probe; exitCode = $exit })
                if ($exit -ne 0) { throw "Direct path was not restored ($probe)." }
            }
        }
        catch { $result = 'FAIL'; Write-Warning "Lab profile cleanup check failed: $_" }
    }
    foreach ($log in @($localLog, $channelLog)) {
        if ($log -and (Test-Path -LiteralPath $log)) { Copy-Item -LiteralPath $log -Destination $evidence }
    }
    $report = [ordered]@{ result = $result; os = (Get-CimInstance Win32_OperatingSystem).Caption; rows = $rows.ToArray() }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'route-test.json')
    Get-ChildItem -LiteralPath $evidence -Filter '*.log' | ForEach-Object { Get-Content -LiteralPath $_.FullName }
    $report | ConvertTo-Json -Depth 5
    if ($packageInfo) { Remove-AppxPackage -Package $packageInfo.PackageFullName -ErrorAction Continue }
    if ($installCertificate) { Remove-Item -LiteralPath ('Cert:\LocalMachine\TrustedPeople\' + $installCertificate.Thumbprint) -ErrorAction Continue }
    if ($signer) { Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $signer.Thumbprint) -ErrorAction Continue }
}
if ($result -ne 'PASS') { throw 'The Windows VPN API route test did not pass.' }
