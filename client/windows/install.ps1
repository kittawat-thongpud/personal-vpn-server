param([string]$ProfileFolder, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $source 'PersonalVPN.exe'))) { throw 'Extract the Windows release ZIP first.' }
$version = [IO.File]::ReadAllText((Join-Path $source 'CLIENT-VERSION.txt')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid client release version.' }
$isAdministrator = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    $argsList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'))
    if ($NoLaunch) { $argsList += '-NoLaunch' }
    if ($ProfileFolder) { $argsList += @('-ProfileFolder',('"' + $ProfileFolder + '"')) }
    $installProcess = Start-Process powershell.exe -Verb RunAs -ArgumentList $argsList -PassThru -Wait -WindowStyle Hidden
    if ($installProcess.ExitCode -ne 0) { throw 'Administrator installation did not finish.' }
    exit 0
}
$architecture = if (Test-Path -LiteralPath (Join-Path $source 'architecture-x86.txt')) { 'x86' } else { 'x64' }
$programRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$destination = [IO.Path]::GetFullPath((Join-Path $programRoot 'PersonalVPN'))
$installationMutex = [Threading.Mutex]::new($false, 'Global\PersonalVPN-Setup')
$locked = $false
try {
try { $locked = $installationMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
if (-not $locked) { throw 'Another Personal VPN Setup is running.' }
$prepare = Start-Process -FilePath (Join-Path $source 'PersonalVPN.exe') -ArgumentList @('--prepare-upgrade') -WindowStyle Hidden -PassThru -Wait
if ($prepare.ExitCode -ne 0) { throw 'Disconnect and exit the older Personal VPN from its tray menu, then retry installation.' }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
if ($source -ne $destination) { Get-ChildItem -LiteralPath $source | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force } }
$staleMarker = Join-Path $destination ('architecture-' + $(if ($architecture -eq 'x86') { 'x64' } else { 'x86' }) + '.txt')
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($staleMarker)) -ne $destination) { throw 'Invalid architecture marker path.' }
if (Test-Path -LiteralPath $staleMarker) { Remove-Item -LiteralPath $staleMarker -Force }
$executable = Join-Path $destination 'PersonalVPN.exe'
$report = Join-Path $destination 'driver-install.json'
$driverInstall = Start-Process -FilePath $executable -ArgumentList @('--install-adapter','--report',('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait
if ($driverInstall.ExitCode -ne 0) { throw 'Signed Wintun driver preparation failed.' }
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Personal VPN.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'Personal VPN.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = (Join-Path $destination 'Assets/personal-vpn.ico') + ',0'
    $shortcut.Description = 'Personal VPN - Administrator'
    $shortcut.Save()
    $legacyPath = Join-Path (Split-Path $shortcutPath) 'PersonalVPN.lnk'
    if (Test-Path -LiteralPath $legacyPath) {
        $legacy = $shell.CreateShortcut($legacyPath)
        $targets = @($legacy.TargetPath)
        $program32 = ${env:ProgramFiles(x86)}
        if ($program32 -and $legacy.TargetPath.StartsWith($program32+'\',[StringComparison]::OrdinalIgnoreCase)) { $targets += $programRoot + $legacy.TargetPath.Substring($program32.Length) }
        foreach ($target in $targets) {
            if ([IO.Path]::GetFileName($target) -eq 'PersonalVPN.exe' -and (Test-Path -LiteralPath (Join-Path (Split-Path $target) 'CLIENT-VERSION.txt'))) { Remove-Item -LiteralPath $legacyPath -Force; break }
        }
    }
}
$refresh = Start-Process -FilePath $executable -ArgumentList @('--refresh-shortcuts') -WindowStyle Hidden -PassThru -Wait
if ($refresh.ExitCode -ne 0) { throw 'Shortcut cache refresh failed.' }
} finally { if ($locked) { $installationMutex.ReleaseMutex() }; $installationMutex.Dispose() }
Write-Output "Installed: $executable"
Write-Output 'The app requires Administrator. TUN routing starts only after confirmed mode selection and Connect.'
if (-not $NoLaunch) {
    if ($ProfileFolder) { Start-Process -FilePath $executable -ArgumentList @('--import-folder',('"' + (Resolve-Path -LiteralPath $ProfileFolder).Path + '"')) }
    else { Start-Process -FilePath $executable }
}
