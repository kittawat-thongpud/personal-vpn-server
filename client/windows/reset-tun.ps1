param([Parameter(Mandatory=$true)][Guid]$AdapterId)
$ErrorActionPreference='Stop'
$adapter = @(Get-NetAdapter -IncludeHidden | Where-Object { [Guid]$_.InterfaceGuid -eq $AdapterId })
if ($adapter.Count -eq 0) { exit 0 }
if ($adapter.Count -ne 1 -or $adapter[0].Name -ne 'PersonalVPN') { throw 'TUN reset ownership mismatch' }
$index = $adapter[0].ifIndex
# This device belongs exclusively to Personal VPN. Keep the device, clear capture.
Get-NetRoute -InterfaceIndex $index -ErrorAction SilentlyContinue |
    Where-Object { $_.NextHop -in @('172.28.255.2','fd26:706e:1::2') } |
    Remove-NetRoute -Confirm:$false -ErrorAction Stop
Get-NetIPAddress -InterfaceIndex $index -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -in @('172.28.255.1','fd26:706e:1::1') } |
    Remove-NetIPAddress -Confirm:$false -ErrorAction Stop
Set-DnsClientServerAddress -InterfaceIndex $index -ResetServerAddresses -ErrorAction Stop
$remaining = @(Get-NetRoute -InterfaceIndex $index -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -in @('172.28.255.2','fd26:706e:1::2') })
$addresses = @(Get-NetIPAddress -InterfaceIndex $index -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -in @('172.28.255.1','fd26:706e:1::1') })
if ($remaining.Count -gt 0 -or $addresses.Count -gt 0) { throw 'TUN capture reset incomplete' }
