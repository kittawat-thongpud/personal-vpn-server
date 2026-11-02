param([Parameter(Mandatory=$true)][string]$ReportPath,[switch]$Repair)
$ErrorActionPreference = 'Stop'
$report = [ordered]@{ result='FAIL'; elevated=$false; repaired=$false }
try {
    $report.elevated = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $report.elevated) { throw 'Administrator required.' }
    $cli = @('C:\Program Files (x86)\ZeroTier\One\zerotier-cli.bat','C:\Program Files\ZeroTier\One\zerotier-cli.bat') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $cli) { throw 'ZeroTier CLI not found.' }
    $info = (& $cli -j info) | ConvertFrom-Json
    $networks = ((& $cli -j listnetworks) | Out-String | ConvertFrom-Json)
    $report.node = $info | Select-Object address,online,version,tcpFallbackActive
    $report.networks = @($networks | Select-Object id,status,portDeviceName,assignedAddresses)
    $target = $networks | Where-Object { @($_.assignedAddresses) -match '^10\.46\.136\.123/' } | Select-Object -First 1
    $expectedNetwork = 'b103a835d20fb470'
    if ($Repair -and -not ($networks | Where-Object id -eq $expectedNetwork)) {
        & $cli join $expectedNetwork | Out-Null
        $report.repaired=$true; $report.action='Rejoin the previously configured local network'
        for ($i=0;$i -lt 30;$i++) { Start-Sleep -Seconds 1; $networks=((& $cli -j listnetworks) | Out-String | ConvertFrom-Json); if ($networks | Where-Object { $_.id -eq $expectedNetwork -and $_.status -eq 'OK' }) { break } }
        $target=$networks | Where-Object id -eq $expectedNetwork | Select-Object -First 1
    }
    $adapters = @(Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'ZeroTier' })
    if ($Repair -and $target -and $target.status -eq 'OK' -and @($adapters | Where-Object Status -eq 'Up').Count -eq 0) {
        Restart-Service -Name ZeroTierOneService -Force
        $report.repaired=$true
        for ($i=0;$i -lt 25;$i++) {
            Start-Sleep -Seconds 1
            if (@(Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'ZeroTier' -and $_.Status -eq 'Up' }).Count -gt 0) { break }
        }
        $report.nodeAfter = ((& $cli -j info) | ConvertFrom-Json) | Select-Object address,online,version,tcpFallbackActive
        $report.networksAfter = @(((& $cli -j listnetworks) | ConvertFrom-Json) | Select-Object id,status,portDeviceName,assignedAddresses)
    }
    $parsedAfter = ((& $cli -j listnetworks) | Out-String | ConvertFrom-Json)
    $report.networksAfter = @($parsedAfter | Select-Object id,status,portDeviceName,assignedAddresses)
    $report.adapters = @(Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'ZeroTier' } | Select-Object Name,ifIndex,Status)
    $report.addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -like '10.46.*' | Select-Object IPAddress,PrefixLength,InterfaceAlias,AddressState)
    $report.routes = @(Get-NetRoute -AddressFamily IPv4 | Where-Object DestinationPrefix -like '10.46*' | Select-Object DestinationPrefix,NextHop,InterfaceAlias,InterfaceIndex)
    $report.selfPing = Test-Connection -ComputerName 10.46.136.123 -Count 2 -Quiet
    $report.peerPing = Test-Connection -ComputerName 10.46.136.125 -Count 2 -Quiet
    $report.result='PASS'
} catch { $report.error=$_.Exception.GetType().Name }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
