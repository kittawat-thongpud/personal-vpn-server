param([Parameter(Mandatory)][ValidateSet('Start','Probe','Stop')][string] $Action)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or -not [Environment]::Is64BitProcess) {
    throw 'The WFP lab runs only in a disposable, 64-bit GitHub-hosted Windows runner.'
}
$name = 'PvnWfpReferenceLab'
$assets = Join-Path $env:RUNNER_TEMP 'pvn-wfp-reference'
$evidence = Join-Path $env:RUNNER_TEMP 'pvn-wfp-evidence'
$ownership = Join-Path $assets 'owned-service'
$deviceProbe = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class WfpLoadCheck {
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle Open(string file, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint ioctl, IntPtr input, uint inputLength, [Out] byte[] output, uint outputLength, out uint written, IntPtr overlapped);
    public static ulong State() {
        using (var device = Open(@"\\.\MULLVADSPLITTUNNEL", 0xc0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var output = new byte[8]; uint written;
            if (!DeviceIoControl(device, 0x80000024, IntPtr.Zero, 0, output, 8, out written, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (written != 8) throw new InvalidOperationException("Unexpected reference driver ABI.");
            return BitConverter.ToUInt64(output, 0);
        }
    }
}
'@

if ($Action -eq 'Start') {
    if ((Get-Service -Name $name,'mullvad-split-tunnel' -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $ownership)) { throw 'An unrelated or previous split-tunnel service is present.' }
    New-Item -ItemType Directory -Path $assets,$evidence -Force | Out-Null
    $pin = 'https://raw.githubusercontent.com/mullvad/mullvadvpn-app-binaries/80c2e94d5ee8087ea80059ab24f1a81b632714f7/x86_64-pc-windows-msvc/split-tunnel'
    $hashes = @{
        sys = '10CF25BBCFE51FD663A1FEC88A98E9B858F3A579589BB2EC496B66E4FDD1B201'
        cat = 'C599926A0327D7AE06B534F4CD039DB30392E1897BB9D03E4FEC3631744A4E6D'
        inf = '3DD5905E5FB98D61A942A33E8C9A5BA07C3A2DE1E4F319E1FEC3E54DF6591608'
    }
    foreach ($suffix in @('sys','cat','inf')) {
        $filename = "mullvad-split-tunnel.$suffix"; $path = Join-Path $assets $filename
        Invoke-WebRequest -Uri "$pin/$filename" -OutFile $path -TimeoutSec 45
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$suffix]) { throw "Reference $suffix digest mismatch." }
    }
    $driver = Join-Path $assets 'mullvad-split-tunnel.sys'
    $catalog = Join-Path $assets 'mullvad-split-tunnel.cat'
    $ds = Get-AuthenticodeSignature -LiteralPath $driver
    $cs = Get-AuthenticodeSignature -LiteralPath $catalog
    if ($ds.Status -ne 'Valid' -or $ds.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Mullvad VPN AB(,|$)' -or $cs.Status -ne 'Valid' -or $cs.SignerCertificate.Subject -notmatch '^CN=Microsoft Windows Hardware Compatibility Publisher,') { throw 'Reference driver/catalog signature validation failed.' }
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $signtool = Get-ChildItem -LiteralPath $sdk -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
    if (-not $signtool) { throw 'No installed Windows SDK verifier.' }
    & $signtool verify /kp /c $catalog $driver
    if ($LASTEXITCODE -ne 0) { throw 'Catalog membership or kernel signature verification failed.' }
    $before = @(Get-WindowsDriver -Online -ErrorAction Stop | ForEach-Object Driver)
    & pnputil.exe /add-driver (Join-Path $assets 'mullvad-split-tunnel.inf')
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the validated catalog in the driver store.' }
    $published = @(Get-WindowsDriver -Online | Where-Object { $_.Driver -notin $before -and $_.OriginalFileName -like '*mullvad-split-tunnel.inf' } | ForEach-Object Driver)
    $published | Set-Content -LiteralPath (Join-Path $assets 'catalog-ownership')
    & sc.exe create $name 'type=' 'kernel' 'start=' 'demand' 'binPath=' $driver
    if ($LASTEXITCODE -ne 0) { throw 'Could not register the lab driver service.' }
    'created' | Set-Content -LiteralPath $ownership
    & sc.exe start $name
    if ($LASTEXITCODE -ne 0) { throw 'Windows did not load the reference driver; no code-integrity policy was changed.' }
    Add-Type -TypeDefinition $deviceProbe
    $state = [WfpLoadCheck]::State()
    if ($state -ne 1) { throw "Unexpected initial reference driver state: $state." }
    [ordered]@{result='PASS'; state=$state; driverHash=$hashes.sys; catalogHash=$hashes.cat; signature='Verified kernel catalog'; routingTest=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'driver-load.json')
} elseif ($Action -eq 'Probe') {
    Add-Type -TypeDefinition $deviceProbe
    if ([WfpLoadCheck]::State() -ne 1) { throw 'Reference driver is not in its unloaded-policy state.' }
} elseif ($Action -eq 'Stop') {
    if (Test-Path -LiteralPath $ownership) {
        $service = Get-Service -Name $name -ErrorAction Stop
        if ($service.Status -ne 'Stopped') {
            Add-Type -TypeDefinition $deviceProbe
            if ([WfpLoadCheck]::State() -ne 1) { throw 'Driver was not reset. Leave the disposable runner to tear down rather than unload an active callout.' }
            & sc.exe stop $name
            if ($LASTEXITCODE -ne 0) { throw 'Reference driver did not stop.' }
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
        }
        & sc.exe delete $name
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove the lab driver service.' }
        Remove-Item -LiteralPath $ownership
    }
    $catalogOwner = Join-Path $assets 'catalog-ownership'
    if (Test-Path -LiteralPath $catalogOwner) {
        foreach ($entry in (Get-Content -LiteralPath $catalogOwner)) {
            if ($entry -notmatch '^oem\d+\.inf$') { throw 'Unexpected catalog name.' }
            & pnputil.exe /delete-driver $entry
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove the lab-owned catalog.' }
        }
        Remove-Item -LiteralPath $catalogOwner
    }
    if (Test-Path -LiteralPath $evidence) {
        [ordered]@{result='PASS'; serviceAbsent=(-not (Get-Service -Name $name -ErrorAction SilentlyContinue))} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'driver-cleanup.json')
    }
}
