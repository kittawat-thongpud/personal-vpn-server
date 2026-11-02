param(
    [ValidateSet('45.77.179.227')][string]$Server = '45.77.179.227',
    [Parameter(Mandatory = $true)][ValidatePattern('^SHA256:[A-Za-z0-9+/]{43}$')][string]$ExpectedHostFingerprint,
    [ValidatePattern('^[a-z][a-z0-9_-]{0,31}$')][string]$Device = 'windows',
    [ValidateRange(1,65535)][int]$ProxyPort = 7688,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$privateRoot = Join-Path $projectRoot ('.state\client-test\' + $Server)
$pythonPath = Join-Path $projectRoot '.tools\release-venv\Scripts\python.exe'
$bridgePath = Join-Path $PSScriptRoot 'http_connect.py'
if (-not (Test-Path -LiteralPath $pythonPath)) { throw 'The project Python environment is missing. Ask Codex to prepare the test tools.' }
foreach ($command in @('ssh','scp','ssh-keygen')) { if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "$command was not found. Windows OpenSSH Client is required." } }

# Credentials stay in this ignored directory. Only the current Windows user and
# SYSTEM can access it. No SSH/VPN password is accepted as a script argument.
New-Item -ItemType Directory -Path $privateRoot -Force | Out-Null
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$rights = [System.Security.AccessControl.FileSystemRights]::FullControl
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$propagation = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
foreach ($identity in @([System.Security.Principal.WindowsIdentity]::GetCurrent().User, (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')))) {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity,$rights,$inherit,$propagation,$allow)))
}
Set-Acl -LiteralPath $privateRoot -AclObject $acl
$knownHosts = Join-Path $privateRoot 'known_hosts'
$sshConfig = Join-Path $privateRoot 'empty_ssh_config'
Set-Content -LiteralPath $sshConfig -Value '' -Encoding ascii
$proxyCommand = 'ProxyCommand="' + $pythonPath.Replace('\','/') + '" "' + $bridgePath.Replace('\','/') + '" %h %p --proxy-port ' + $ProxyPort
$common = @('-F',$sshConfig,'-o',$proxyCommand,'-o',('UserKnownHostsFile=' + $knownHosts),'-o','GlobalKnownHostsFile=NUL','-o','HostKeyAlgorithms=ssh-ed25519','-o','ConnectTimeout=10')

# First obtain only the public host key with every credential method disabled.
# No credential is transmitted before comparison with the console fingerprint.
Write-Host 'Checking the VPS public SSH host key through your existing proxy...'
$probePreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $probe = & ssh @common -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o PreferredAuthentications=none -o PubkeyAuthentication=no -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no "root@$Server" true 2>&1
} finally { $ErrorActionPreference = $probePreference }
if (-not (Test-Path -LiteralPath $knownHosts)) { throw 'Could not obtain the public host key. No profile was downloaded.' }
$fingerprints = @(& ssh-keygen -lf $knownHosts -E sha256)
if ($LASTEXITCODE -ne 0 -or -not ($fingerprints | Where-Object { ($_ -split '\s+')[1] -eq $ExpectedHostFingerprint })) {
    throw 'SSH host key does not match the fingerprint from the Vultr console. No credentials were sent.'
}
if ($VerifyOnly) { Write-Host 'Host fingerprint verified against the console. No credential was sent.'; return }
Write-Host 'Host fingerprint verified. Enter the VPS password only at the SSH prompt below.'

# Only exported client files are fetched, never .state/server.json or tls.key.
$remote = 'root@' + $Server + ':/opt/personal-vpn/exports/' + $Device
& scp @common -o StrictHostKeyChecking=yes -o PreferredAuthentications=password,keyboard-interactive -o PubkeyAuthentication=no -r $remote $privateRoot
if ($LASTEXITCODE -ne 0) { throw 'SCP did not finish. Do not paste the password into chat.' }
$destination = Join-Path $privateRoot $Device
foreach ($name in @('xray.json','hysteria.json','links.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $name))) { throw "Missing exported file: $name" }
}
Write-Host ('Client profiles downloaded privately to: ' + $destination)
Write-Host 'Do not paste their contents into chat or commit them to Git.'
