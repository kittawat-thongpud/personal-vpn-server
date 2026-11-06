param([ValidateSet('x86','x64')][string]$Architecture, [string]$Version)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Upgrade integration tests require an isolated GitHub-hosted runner.' }
$root = Join-Path $env:ProgramFiles 'PersonalVPN'
$oldRoot = Join-Path $(if ($Architecture -eq 'x86') { ${env:ProgramFiles(x86)} } else { $env:ProgramFiles }) ('PersonalVPN/0.7.2-' + $Architecture)
$setup = (Resolve-Path "dist/PersonalVPN-Setup-$Version-$Architecture.exe").Path
$asset = "PersonalVPN-Setup-0.7.2-$Architecture.exe"
gh release download client-v0.7.2 --repo $env:GITHUB_REPOSITORY --pattern $asset --dir $env:RUNNER_TEMP
if ($LASTEXITCODE -ne 0) { throw 'Previous installer download failed' }
$oldSetup = Join-Path $env:RUNNER_TEMP $asset
$expected = if ($Architecture -eq 'x86') { '217c71a27c8ac0796eb63c137d2255f0e753861024cd5f3dc7b568088ac4680c' } else { '96e4a97cdbed9f514f28c4d820f54a50a6818837bf6cb051e2664e2d71023dc4' }
if ((Get-FileHash $oldSetup).Hash -ne $expected) { throw 'Previous installer hash mismatch' }
function Install([string]$Installer, [string]$Name, [string]$Directory='') {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="'+(Join-Path $env:RUNNER_TEMP ($Name+'.log'))+'"'))
    if ($Directory) { $arguments += '/DIR="'+$Directory+'"' }
    $process = Start-Process -FilePath $Installer -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(240000)) { $process.Kill(); throw "$Name installer timed out" }
    if ($process.ExitCode -ne 0) { Get-Content (Join-Path $env:RUNNER_TEMP ($Name+'.log')) -Tail 65; throw "$Name installer exit: $($process.ExitCode)" }
}
function LaunchIdle([string]$Exe) {
    $process = Start-Process -FilePath $Exe -PassThru -WindowStyle Hidden
    $ready = $false
    for ($i=0; $i -lt 150; $i++) {
        if ($process.HasExited) { throw 'Idle GUI exited before upgrade' }
        try { $signal = [Threading.EventWaitHandle]::OpenExisting('Local\PersonalVPN-Show-'+$env:USERNAME); $signal.Dispose(); $ready=$true; break } catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Idle GUI did not initialize its instance signal' }
    return $process
}
function VerifyInstalled {
    $exe = Join-Path $root 'PersonalVPN.exe'
    if ((Get-FileHash $exe).Hash -ne (Get-FileHash dist/windows/PersonalVPN.exe).Hash) { throw 'Installed app differs from qualified package' }
    if ((Get-Content (Join-Path $root 'CLIENT-VERSION.txt') -Raw).Trim() -ne $Version) { throw 'Installed shared version mismatch' }
    if ((Get-Content (Join-Path $root 'driver-install.json') -Raw | ConvertFrom-Json).result -ne 'PASS') { throw 'Installed driver not ready' }
    $large = [IntPtr]::Zero; $small = [IntPtr]::Zero
    try {
        if ([PersonalVpnIconCheck]::ExtractIconEx((Join-Path $root 'Assets/personal-vpn.ico'),0,[ref]$large,[ref]$small,1) -ne 1 -or $large -eq [IntPtr]::Zero -or $small -eq [IntPtr]::Zero) { throw 'Windows shell cannot extract the installed desktop icon' }
    } finally { if ($large -ne [IntPtr]::Zero) { [void][PersonalVpnIconCheck]::DestroyIcon($large) }; if ($small -ne [IntPtr]::Zero) { [void][PersonalVpnIconCheck]::DestroyIcon($small) } }
    if ((Get-FileHash $settings).Hash -ne $settingsHash) { throw 'Upgrade changed encrypted user settings' }
    if (-not (Test-Path -LiteralPath (Join-Path $root "architecture-$Architecture.txt"))) { throw 'Architecture marker missing' }
    $shell = New-Object -ComObject WScript.Shell
    $commonLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Personal VPN.lnk'
    foreach ($path in @($commonLink,$userLink)) {
        $link = $shell.CreateShortcut($path)
        if ($link.TargetPath -ne $exe -or $link.WorkingDirectory -ne $root) { throw 'Shortcut points to an old installation' }
        if ($link.IconLocation -ne ((Join-Path $root 'Assets/personal-vpn.ico')+',0')) { throw 'Shortcut icon is not a stable standalone ICO' }
        if (-not (Test-Path -LiteralPath ($link.IconLocation -replace ',0$',''))) { throw 'Shortcut icon missing' }
    }
    $key = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{5A4D9679-160C-4DAA-A3E8-06409432D512}_is1'
    if ($key.DisplayVersion -ne $Version -or $key.InstallLocation.TrimEnd('\') -ne $root) { throw 'Stable uninstall registration mismatch' }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
        $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
        try { foreach ($arch in @('x86','x64')) { $legacy=$hive.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{5A4D9679-160C-4DAA-A3E8-06409432D512}-'+$arch+'_is1'); if ($legacy) { $legacy.Dispose(); throw 'Legacy uninstall entry remains' } } } finally { $hive.Dispose() }
    }
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PersonalVpnIconCheck {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] public static extern uint ExtractIconEx(string file,int index,out IntPtr large,out IntPtr small,uint count);
 [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}
'@
# A real current-user DPAPI blob is retained byte-for-byte, not merely recreated.
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$settings = Join-Path $env:LOCALAPPDATA 'PersonalVPN/settings.dpapi'
New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
$payload = [Text.Encoding]::UTF8.GetBytes('{"Traffic":"Global","Protocol":"Hysteria 2","CaptureMode":"Proxy","Profiles":[]}')
[IO.File]::WriteAllBytes($settings,[Security.Cryptography.ProtectedData]::Protect($payload,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
Install $oldSetup 'legacy-install' $oldRoot
$note = Join-Path $oldRoot 'keep-user-note.txt'
[IO.File]::WriteAllText($note,'An unowned file must survive retiring the old package.')
$userLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Personal VPN.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($userLink); $link.TargetPath=Join-Path $oldRoot 'PersonalVPN.exe'; $link.IconLocation=$link.TargetPath+',0'; $link.Save()
$oldGui = LaunchIdle (Join-Path $oldRoot 'PersonalVPN.exe')
$settingsHash = (Get-FileHash $settings).Hash
Install $setup 'migrate-versioned-install'
if (-not $oldGui.WaitForExit(15000)) { throw 'Previous GUI is still running after migration' }
VerifyInstalled
if (Test-Path -LiteralPath (Join-Path $oldRoot 'PersonalVPN.exe')) { throw 'Old versioned application was not retired' }
if (-not (Test-Path -LiteralPath $note)) { throw 'Migration removed an unowned legacy file' }
# Same-version reinstall updates the same product/directory while a GUI is open.
$current = LaunchIdle (Join-Path $root 'PersonalVPN.exe')
$settingsHash = (Get-FileHash $settings).Hash
Install $setup 'reinstall-in-place'
if (-not $current.WaitForExit(15000)) { throw 'Previous GUI remained mapped across in-place update' }
VerifyInstalled
$report = [ordered]@{result='PASS';architecture=$Architecture;version=$Version;installed=$root;checks=@('versioned migration and old GUI closure','single stable directory and uninstall identity','same-version reinstall closes idle GUI','encrypted settings preserved','common and shadowing user shortcut repaired','standalone icon exists','owned old files retired; unowned file retained','persistent adapter service ready')}
$report | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $env:RUNNER_TEMP 'upgrade.json')
