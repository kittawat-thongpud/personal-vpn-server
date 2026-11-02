param([string]$ProfileFolder, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$version = '0.3.0'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $source 'PersonalVPN.exe'))) { throw 'Extract the Windows release ZIP before running install.ps1.' }
$programRoot = Join-Path $env:LOCALAPPDATA 'Programs/PersonalVPN'
$destination = Join-Path $programRoot $version
$appData = Join-Path $env:LOCALAPPDATA 'PersonalVPN'
# Installation is per-user. Keep profile data separate from versioned executables.
New-Item -ItemType Directory -Force -Path $destination,$appData | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [IO.Directory]::GetAccessControl($appData, [Security.AccessControl.AccessControlSections]::Access)
$acl.SetAccessRuleProtection($true, $false)
foreach ($identity in @($sid, (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
[IO.Directory]::SetAccessControl($appData, $acl)
Get-ChildItem -LiteralPath $source | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force }
$executable = Join-Path $destination 'PersonalVPN.exe'
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Personal VPN.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'Personal VPN.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $destination
    $shortcut.Description = 'Personal VPN - per-app proxy preview'
    $shortcut.Save()
}
Write-Output "Installed: $executable"
Write-Output 'Installation makes no network changes. Connect can apply the per-user system proxy and restore it on disconnect.'
if (-not $NoLaunch) {
    if ($ProfileFolder) {
        $resolvedProfile = (Resolve-Path -LiteralPath $ProfileFolder).Path
        if ($resolvedProfile.Contains('"')) { throw 'Invalid profile folder path.' }
        Start-Process -FilePath $executable -ArgumentList @('--import-folder', ('"' + $resolvedProfile + '"'))
    } else { Start-Process -FilePath $executable }
}
