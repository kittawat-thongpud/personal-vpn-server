$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'windows-dist'
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
foreach ($taskArch in @('x86','x64')) {
    $taskTarget = Join-Path $taskOutput $taskArch
    dotnet build (Join-Path $taskRoot 'windows/PersonalVPNP2PLab.csproj') -c Release "-p:LabPlatform=$taskArch" -o $taskTarget --nologo
    if ($LASTEXITCODE -ne 0) { throw "Windows $taskArch build failed" }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'dist/Personal-VPN-P2P-Lab.html') -Destination $taskTarget
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskTarget
}
$taskState = Join-Path $env:RUNNER_TEMP 'p2p-windows-controller'
python (Join-Path $taskRoot 'signal-server/server.py') init --state $taskState --endpoint https://127.0.0.1:19443 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'TLS fixture init failed' }
$taskPython = (Get-Command python).Source
$taskServeArgs = @((Join-Path $taskRoot 'signal-server/server.py'), 'serve', '--state', $taskState, '--port', '19443')
$taskServer = Start-Process -FilePath $taskPython -ArgumentList ($taskServeArgs | ForEach-Object { '"' + $_ + '"' }) -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 2
    foreach ($taskArch in @('x86','x64')) {
        # Each architecture receives a new, single-use enrollment invitation.
        python (Join-Path $taskRoot 'signal-server/server.py') owner-invite --state $taskState | Set-Content -Encoding ascii (Join-Path $taskState 'probe.txt')
        $taskExe = Join-Path $taskOutput "$taskArch/PersonalVPNP2PLab.exe"
        $taskResult = Join-Path $taskOutput "$taskArch/native-test.json"
        $taskProbe = Start-Process -FilePath $taskExe -ArgumentList @('--probe', ('"'+(Join-Path $taskState 'probe.txt')+'"'), ('"'+$taskResult+'"')) -PassThru -WindowStyle Hidden
        if (-not $taskProbe.WaitForExit(90000)) { $taskProbe.Kill(); throw 'Native test timed out' }
        if ($taskProbe.ExitCode -ne 0) { if (Test-Path -LiteralPath $taskResult) { Get-Content -LiteralPath $taskResult }; throw 'Native HTTPS/pin/QR test failed' }
        $taskProof = Get-Content -Raw -LiteralPath $taskResult | ConvertFrom-Json
        if (-not ($taskProof.httpsPinning -and $taskProof.wrongPinRejected -and $taskProof.qrRoundTrip)) { throw 'Native test proof incomplete' }
        Compress-Archive -Path (Join-Path $taskOutput "$taskArch/*") -DestinationPath (Join-Path $taskOutput "PersonalVPN-P2P-Lab-$taskArch.zip")
    }
} finally {
    if (-not $taskServer.HasExited) { Stop-Process -Id $taskServer.Id -Force }
}
