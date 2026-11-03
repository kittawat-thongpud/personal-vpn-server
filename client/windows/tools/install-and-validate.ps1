param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$ExpectedInstallerSha256,
    [Parameter(Mandatory=$true)][string]$ReportDirectory,
    [Parameter(Mandatory=$true)][string]$ProfileDirectory,
    [ValidateSet('x86','x64')][string]$Architecture='x64'
)
$ErrorActionPreference='Stop'
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator required.' }
if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $ExpectedInstallerSha256) { throw 'Installer hash mismatch.' }
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$settingsFile=Join-Path $env:LOCALAPPDATA 'PersonalVPN/settings.dpapi'
$before=(Get-FileHash -LiteralPath $settingsFile).Hash
$summary=[ordered]@{ installed=$false; settingsUnchanged=$false; tunActivated=$false }
try {
    $base=if ($Architecture -eq 'x86' -and $env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { $env:ProgramFiles + ' (x86)' } else { $env:ProgramFiles }
    $clientVersion=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../VERSION')).Trim()
    $installed=Join-Path $base ("PersonalVPN/" + $clientVersion + '-' + $Architecture)
    $setupLog=Join-Path $ReportDirectory 'setup.log'
    $setup=Start-Process -FilePath $InstallerPath -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="'+$installed+'"'),('/LOG="'+$setupLog+'"')) -PassThru -Wait -WindowStyle Hidden
    $summary.setupExitCode=$setup.ExitCode
    if ($setup.ExitCode -ne 0) { throw ('Windows Setup failed with exit code '+$setup.ExitCode+'; see setup.log.') }
    $executable=Join-Path $installed 'PersonalVPN.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Installed executable missing.' }
    $driver=Get-Content -LiteralPath (Join-Path $installed 'driver-install.json') -Raw | ConvertFrom-Json
    if ($driver.result -ne 'PASS') { throw 'Driver installation did not pass.' }
    $summary.installed=$true; $summary.executable=$executable; $summary.driver=$driver
    # Migrate only this app's old per-user shortcuts; the installer also creates
    # common shortcuts. Keep the connected old process running.
    $shell=New-Object -ComObject WScript.Shell
    foreach ($link in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Personal VPN.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) 'Personal VPN.lnk'))) {
        $shortcut=$shell.CreateShortcut($link); $shortcut.TargetPath=$executable; $shortcut.WorkingDirectory=$installed; $shortcut.IconLocation=$executable+',0'; $shortcut.Save()
    }
    function Invoke-ClientCheck([string]$Name,[string[]]$Arguments) {
        $report=Join-Path $ReportDirectory ($Name+'.json')
        $argsList=@($Arguments)+@('--report',('"' + $report + '"'))
        $process=Start-Process -FilePath $executable -ArgumentList $argsList -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw ($Name+' failed; see the sanitized report.') }
        return Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    }
    $summary.selfTest=Invoke-ClientCheck 'self-test' @('--self-test')
    $layout=Join-Path $ReportDirectory 'layout'
    $summary.layout=Invoke-ClientCheck 'layout' @('--layout-test','--output',('"' + $layout + '"'))
    $summary.reality=Invoke-ClientCheck 'reality' @('--probe-protocol','--profile-dir',('"' + $ProfileDirectory + '"'),'--protocol','REALITY')
    $summary.hysteria=Invoke-ClientCheck 'hysteria' @('--probe-protocol','--profile-dir',('"' + $ProfileDirectory + '"'),'--protocol','"Hysteria 2"')
    $summary.selfPing=Test-Connection 10.46.136.123 -Count 1 -Quiet
    $summary.peerPing=Test-Connection 10.46.136.125 -Count 1 -Quiet
    $summary.result='PASS'
} catch { $summary.result='FAIL'; $summary.error=$_.Exception.GetType().Name; $summary.reason=$_.Exception.Message }
$summary.settingsUnchanged=$before -eq (Get-FileHash -LiteralPath $settingsFile).Hash
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'installation.json') -Encoding utf8
