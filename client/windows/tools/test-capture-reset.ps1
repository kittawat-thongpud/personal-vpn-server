param([string]$ResetScript = (Join-Path $PSScriptRoot '../reset-tun.ps1'))
$ErrorActionPreference = 'Stop'
$id = [Guid]::NewGuid()
# All network commands below are fake functions in this test process. The
# synthetic adapter never exists on the host; no route/DNS mutation is performed.
function global:Get-NetAdapter { [CmdletBinding()]param([switch]$IncludeHidden)
    [pscustomobject]@{ InterfaceGuid=$global:VpnResetFixture.Id; Name=$global:VpnResetFixture.Name; ifIndex=2147483000 }
}
function global:Get-NetRoute { [CmdletBinding()]param([int]$InterfaceIndex)
    if ($global:VpnResetFixture.Denied) { Write-Error -Message 'Access denied fixture' -ErrorId 'AccessDenied' -ErrorAction Stop }
    @($global:VpnResetFixture.Routes)
}
function global:Get-NetIPAddress { [CmdletBinding()]param([int]$InterfaceIndex) @($global:VpnResetFixture.Addresses) }
function global:Get-DnsClientServerAddress { [CmdletBinding()]param([int]$InterfaceIndex)
    if ($global:VpnResetFixture.MissingDns) { Write-Error -Message 'No DNS row' -ErrorId 'CmdletizationQuery_NotFound_InterfaceIndex' -ErrorAction Stop }
    [pscustomobject]@{ ServerAddresses=@($global:VpnResetFixture.Dns) }
}
function global:Remove-NetRoute { [CmdletBinding(SupportsShouldProcess=$true)]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { $global:VpnResetFixture.Writes++; $global:VpnResetFixture.Routes=@($global:VpnResetFixture.Routes | Where-Object { $_.NextHop -ne $InputObject.NextHop }) }
}
function global:Remove-NetIPAddress { [CmdletBinding(SupportsShouldProcess=$true)]param([Parameter(ValueFromPipeline=$true)]$InputObject)
    process { $global:VpnResetFixture.Writes++; $global:VpnResetFixture.Addresses=@($global:VpnResetFixture.Addresses | Where-Object { $_.IPAddress -ne $InputObject.IPAddress }) }
}
function global:Set-DnsClientServerAddress { [CmdletBinding()]param([int]$InterfaceIndex,[switch]$ResetServerAddresses)
    $global:VpnResetFixture.Writes++; $global:VpnResetFixture.Dns=@()
}
foreach ($case in @('idle-missing-DNS','orphan-route-no-address','address-only','DNS-only','read-access-denied','wrong-owner')) {
    $global:VpnResetFixture = @{ Id=$id; Name='PersonalVPN'; Routes=@(); Addresses=@(); Dns=@(); MissingDns=$false; Denied=$false; Writes=0 }
    $expectedWrites=0; $expectedError=$false
    switch ($case) {
        'idle-missing-DNS' { $global:VpnResetFixture.MissingDns=$true }
        'orphan-route-no-address' { $global:VpnResetFixture.Routes=@([pscustomobject]@{NextHop='172.28.255.2'},[pscustomobject]@{NextHop='192.0.2.1'}); $expectedWrites=1 }
        'address-only' { $global:VpnResetFixture.Addresses=@([pscustomobject]@{IPAddress='172.28.255.1'}); $expectedWrites=1 }
        'DNS-only' { $global:VpnResetFixture.Dns=@('172.28.255.2'); $expectedWrites=1 }
        'read-access-denied' { $global:VpnResetFixture.Denied=$true; $expectedError=$true }
        'wrong-owner' { $global:VpnResetFixture.Name='Other VPN'; $expectedError=$true }
    }
    $failed=$false
    try { & $ResetScript -AdapterId $id } catch { $failed=$true }
    if ($failed -ne $expectedError -or $global:VpnResetFixture.Writes -ne $expectedWrites) { throw "Capture reset fixture failed: $case" }
    if ($case -eq 'orphan-route-no-address' -and $global:VpnResetFixture.Routes[0].NextHop -ne '192.0.2.1') { throw 'Reset removed an unrelated route' }
    Write-Output "PASS $case"
}
Remove-Variable -Name VpnResetFixture -Scope Global
