param(
    [long]$RunId = 0,
    [switch]$Copy
)
$ErrorActionPreference = 'Stop'
$taskRepo = 'kittawat-thongpud/personal-vpn-server'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskGh = Join-Path $taskRoot '.tools/gh/bin/gh.exe'
if (-not (Test-Path -LiteralPath $taskGh)) { $taskGh = (Get-Command gh -ErrorAction Stop).Source }
$taskOldProxy = $env:HTTPS_PROXY
try {
    # Reuse the current proxy in this process only. No Windows settings change.
    if (-not $env:HTTPS_PROXY) {
        $taskApi = [Uri]'https://api.github.com/'
        $taskProxy = [Net.WebRequest]::GetSystemWebProxy().GetProxy($taskApi)
        if ($taskProxy -and $taskProxy -ne $taskApi) { $env:HTTPS_PROXY = $taskProxy.AbsoluteUri }
    }
    if (-not $RunId) {
        $taskRuns = & $taskGh api "repos/$taskRepo/actions/workflows/p2p-lab.yml/runs?branch=codex%2Fp2p-lab&status=success&per_page=1" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $taskRuns.workflow_runs) { throw 'No successful P2P workflow is available.' }
        $RunId = $taskRuns.workflow_runs[0].id
    }
    $taskRun = & $taskGh api "repos/$taskRepo/actions/runs/$RunId" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $taskRun.conclusion -ne 'success' -or $taskRun.path -ne '.github/workflows/p2p-lab.yml' -or $taskRun.head_branch -ne 'codex/p2p-lab') { throw 'The selected workflow is not a qualified P2P lab run.' }
    $taskDownload = Join-Path $taskRoot ('.cache/p2p-ghcr/' + $RunId)
    if (Test-Path -LiteralPath $taskDownload) { $taskDownload += '-' + [Guid]::NewGuid().ToString('N').Substring(0,8) }
    [IO.Directory]::CreateDirectory($taskDownload) | Out-Null
    & $taskGh run download $RunId --repo $taskRepo --name p2p-lab-ghcr --dir $taskDownload
    if ($LASTEXITCODE -ne 0) { throw 'The qualified GHCR installer is unavailable. Use a run that published p2p-lab-ghcr.' }
    $taskRequired = @('P2P-VPS-INSTALL.sh','IMAGE-REF.txt','GHCR.json','INSTALL-RESULT.json')
    $taskVerified = @()
    foreach ($taskLine in [IO.File]::ReadAllLines((Join-Path $taskDownload 'SHA256SUMS.txt'))) {
        if ($taskLine -notmatch '^([a-f0-9]{64})  \./([A-Za-z0-9._-]+)$') { throw 'Unexpected installer checksum entry.' }
        $taskExpected, $taskName = $Matches[1], $Matches[2]
        $taskActual = (Get-FileHash -LiteralPath (Join-Path $taskDownload $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($taskActual -ne $taskExpected) { throw "Checksum failed: $taskName" }
        $taskVerified += $taskName
    }
    foreach ($taskName in $taskRequired) { if ($taskName -notin $taskVerified) { throw "Missing verified installer file: $taskName" } }
    $taskMetadata = Get-Content -Raw -LiteralPath (Join-Path $taskDownload 'GHCR.json') | ConvertFrom-Json
    $taskRef = ([IO.File]::ReadAllText((Join-Path $taskDownload 'IMAGE-REF.txt'))).Trim()
    if ($taskRef -notmatch '^ghcr\.io/kittawat-thongpud/personal-vpn-server/p2p-signal@sha256:[a-f0-9]{64}$' -or $taskMetadata.image -ne $taskRef -or $taskMetadata.source -ne $taskRun.head_sha) { throw 'The published image does not match the qualified source.' }
    $taskInstaller = [IO.File]::ReadAllText((Join-Path $taskDownload 'P2P-VPS-INSTALL.sh')).Replace("`r`n", "`n")
    $taskScript = "bash <<'P2P_LAB_SETUP'`n" + $taskInstaller.TrimEnd() + "`nP2P_LAB_SETUP`n"
    $taskOut = Join-Path $taskRoot '.cache/p2p-lab2-vps-install.txt'
    [IO.File]::WriteAllText($taskOut, $taskScript, (New-Object Text.UTF8Encoding($false)))
    if ($Copy) { Set-Clipboard -Value $taskScript }
    Write-Output 'Prepared the permanent GHCR installer. No expiring link or token is included.'
    Write-Output 'On the VPS, log in once: docker login ghcr.io -u kittawat-thongpud'
    Write-Output 'Enter a GitHub classic token with read:packages at the Password prompt, then paste the installer.'
    Write-Output "Image: $taskRef"
    Write-Output "Command file: $taskOut"
    if ($Copy) { Write-Output 'The installer is on the clipboard.' }
} finally {
    $env:HTTPS_PROXY = $taskOldProxy
}
