param([string]$ProfileFolder, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$version = '0.6.2'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $source 'PersonalVPN.exe'))) { throw 'Extract the Windows release ZIP first.' }
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
$destination = Join-Path $env:ProgramFiles ("PersonalVPN/$version-$architecture")
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Get-ChildItem -LiteralPath $source | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force }
$executable = Join-Path $destination 'PersonalVPN.exe'
$report = Join-Path $destination 'driver-install.json'
$driverInstall = Start-Process -FilePath $executable -ArgumentList @('--install-adapter','--report',('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait
if ($driverInstall.ExitCode -ne 0) { throw 'Signed Wintun driver preparation failed.' }
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Personal VPN.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'Personal VPN.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = $executable + ',0'
    $shortcut.Description = 'Personal VPN - Administrator'
    $shortcut.Save()
}
Write-Output "Installed: $executable"
Write-Output 'The app requires Administrator. TUN routing starts only after confirmed mode selection and Connect.'
if (-not $NoLaunch) {
    if ($ProfileFolder) { Start-Process -FilePath $executable -ArgumentList @('--import-folder',('"' + (Resolve-Path -LiteralPath $ProfileFolder).Path + '"')) }
    else { Start-Process -FilePath $executable }
}
