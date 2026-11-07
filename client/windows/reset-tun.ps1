param([Parameter(Mandatory=$true)][Guid]$AdapterId)
$ErrorActionPreference='Stop'
$adapter = @(Get-NetAdapter -IncludeHidden | Where-Object { [Guid]$_.InterfaceGuid -eq $AdapterId })
if ($adapter.Count -eq 0) { exit 0 }
if ($adapter.Count -ne 1 -or $adapter[0].Name -ne 'PersonalVPN') { throw 'TUN reset ownership mismatch' }
$index = $adapter[0].ifIndex
function Read-CaptureRows([string]$Command) {
    try { @(& $Command -InterfaceIndex $index -ErrorAction Stop) }
    catch { if ($_.FullyQualifiedErrorId -notlike '*CmdletizationQuery_NotFound*') { throw } }
}
# This device belongs exclusively to Personal VPN. Keep the device, clear capture.
$captureRoutes = @(Read-CaptureRows 'Get-NetRoute' | Where-Object { $_.NextHop -in @('172.28.255.2','fd26:706e:1::2') })
$captureAddresses = @(Read-CaptureRows 'Get-NetIPAddress' | Where-Object { $_.IPAddress -in @('172.28.255.1','fd26:706e:1::1') })
$captureDns = @(Read-CaptureRows 'Get-DnsClientServerAddress' | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ -in @('172.28.255.2','fd26:706e:1::2') })
# A fresh / already reset Wintun device may have no IP-interface/DNS CIM row.
# Inspect all three resources, but never manufacture an idle-device DNS write.
if ($captureRoutes.Count -eq 0 -and $captureAddresses.Count -eq 0 -and $captureDns.Count -eq 0) { exit 0 }
$captureRoutes |
    Remove-NetRoute -Confirm:$false -ErrorAction Stop
$captureAddresses |
    Remove-NetIPAddress -Confirm:$false -ErrorAction Stop
if ($captureDns.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $index -ResetServerAddresses -ErrorAction Stop }
$remaining = @(Read-CaptureRows 'Get-NetRoute' | Where-Object { $_.NextHop -in @('172.28.255.2','fd26:706e:1::2') })
$addresses = @(Read-CaptureRows 'Get-NetIPAddress' | Where-Object { $_.IPAddress -in @('172.28.255.1','fd26:706e:1::1') })
if ($remaining.Count -gt 0 -or $addresses.Count -gt 0) { throw 'TUN capture reset incomplete' }
$dns = @(Read-CaptureRows 'Get-DnsClientServerAddress' | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ -in @('172.28.255.2','fd26:706e:1::2') })
if ($dns.Count -gt 0) { throw 'TUN DNS reset incomplete' }
