param([ValidateSet('x64','x86')][string]$Architecture, [string]$Destination)
$ErrorActionPreference = 'Stop'
$patchRoot = Join-Path $PSScriptRoot 'native'
$commit = 'c005f35300ce76427dc285481ec5695f5b179cd2'
$source = Join-Path $env:RUNNER_TEMP ('personal-vpn-sing-box-' + $Architecture)
git clone --filter=blob:none https://github.com/SagerNet/sing-box.git $source
if ($LASTEXITCODE -ne 0) { throw 'Source clone failed' }
Push-Location $source
try {
    git checkout --detach $commit
    if ($LASTEXITCODE -ne 0 -or (git rev-parse HEAD) -ne $commit) { throw 'Source revision mismatch' }
    go mod vendor
    if ($LASTEXITCODE -ne 0) { throw 'Verified dependency download failed' }
    $file = Join-Path $source 'vendor/github.com/sagernet/sing-tun/tun_windows.go'
    $text = [IO.File]::ReadAllText($file) -replace '\r\n',"$([char]10)"
    $start = $text.IndexOf('adapter, err := wintun.CreateAdapter(options.Name, TunnelType, generateGUIDByDeviceName(options.Name))')
    if ($start -lt 0) { throw 'Upstream TUN source changed; patch requires review' }
    $end = $text.IndexOf('nativeTun := &NativeTun{', $start)
    if ($end -lt 0) { throw 'Upstream TUN source changed; patch requires review' }
    $replacement = @'
// Personal VPN: only the persistent adapter service creates devices.
    adapter, err := wintun.OpenAdapter(options.Name)
    if err != nil {
        return nil, E.Cause(err, "open persistent adapter")
    }
    
'@
    $text = $text.Substring(0,$start) + $replacement + $text.Substring($end)
    [IO.File]::WriteAllText($file, $text)
    # Pinned Windows socket egress: per-destination non-capture route lookup.
    # Strong-host mutations belong exclusively to the durable managed guard.
    $networkFile = Join-Path $source 'route/network.go'
    $networkText = [IO.File]::ReadAllText($networkFile) -replace '\r\n',"$([char]10)"
    $needle = "`t`t`tdefaultInterface := r.interfaceMonitor.DefaultInterface()"
    if (($networkText.Split([string[]]@($needle),[StringSplitOptions]::None)).Count -ne 2) { throw 'Upstream socket routing source changed; patch requires review' }
    function Add-PathTiming([string]$content) {
        if (-not $content.Contains("import (`n")) { throw 'Upstream import block changed' }
        $content.Replace("import (`n", ('import (' + "`n`t" + '"github.com/sagernet/sing-box/common/pvnpath"' + "`n"))
    }
    function Replace-TimingNeedle([string]$content, [string]$search, [string]$replacement) {
        if ($content.Split([string[]]@($search), [StringSplitOptions]::None).Count -ne 2) { throw 'Pinned sing-box path timing hook changed' }
        $content.Replace($search, $replacement)
    }
    $package = New-Item -ItemType Directory -Path (Join-Path $source 'common/pvnpath')
    Copy-Item -LiteralPath (Join-Path $patchRoot 'path_timing.go') -Destination $package.FullName
    $networkText = (Add-PathTiming $networkText).Replace($needle, ("`t`t`t" + 'if remoteAddr.IsValid() { defer pvnpath.Start("adapter_route")(); return tun.PersonalVPNBypassInterface(remoteAddr, r.interfaceMonitor.MyInterfaces()) }' + "`n" + $needle))
    [IO.File]::WriteAllText($networkFile,$networkText)
    $routeFile = Join-Path $source 'route/route.go'
    $routeText = Add-PathTiming ([IO.File]::ReadAllText($routeFile).Replace("`r`n","`n"))
    $routeText = Replace-TimingNeedle $routeText 'fatalErr = r.prepareMatchMetadata(ctx, metadata)' ('defer pvnpath.Start("policy_" + metadata.Network)()' + "`n`t" + 'fatalErr = r.prepareMatchMetadata(ctx, metadata)')
    [IO.File]::WriteAllText($routeFile, $routeText)
    $directFile = Join-Path $source 'protocol/direct/outbound.go'
    $directText = Add-PathTiming ([IO.File]::ReadAllText($directFile).Replace("`r`n","`n"))
    foreach ($hook in @(
        @('return h.dialer.DialContext(ctx, network, destination)', 'socket_dial'),
        @('conn, err := h.dialer.ListenPacket(ctx, destination)', 'socket_udp'),
        @('return dialer.DialParallelNetwork(ctx, h.dialer, network, destination, destinationAddresses, len(destinationAddresses) > 0 && destinationAddresses[0].Is6(), nil,', 'socket_dial_parallel'),
        @('return dialer.DialParallelNetwork(ctx, h.dialer, network, destination, destinationAddresses, len(destinationAddresses) > 0 && destinationAddresses[0].Is6(), networkStrategy,', 'socket_dial_parallel'),
        @('conn, newDestination, err := dialer.ListenSerialNetworkPacket', 'socket_udp_serial')
    )) {
        $directText = Replace-TimingNeedle $directText $hook[0] (('defer pvnpath.Start("' + $hook[1] + '")()') + "`n`t" + $hook[0])
    }
    [IO.File]::WriteAllText($directFile, $directText)
    $native = Join-Path $source 'vendor/github.com/sagernet/sing-tun'
    foreach ($name in @('personalvpn_route_windows.go','personalvpn_route_windows_test.go','tun_bind_windows.go')) {
        Copy-Item -LiteralPath (Join-Path $patchRoot $name) -Destination (Join-Path $native $name)
    }
    gofmt -w $file
    if ($LASTEXITCODE -ne 0) { throw 'TUN patch formatting failed' }
    gofmt -w $networkFile $routeFile $directFile (Join-Path $package.FullName 'path_timing.go') $native/personalvpn_route_windows.go $native/personalvpn_route_windows_test.go $native/tun_bind_windows.go
    if ($LASTEXITCODE -ne 0) { throw 'Route patch formatting failed' }
    Copy-Item $file (Join-Path (Split-Path $Destination) 'TUN-SOURCE.txt')
    foreach ($name in @('personalvpn_route_windows.go','tun_bind_windows.go')) { Copy-Item (Join-Path $native $name) (Join-Path (Split-Path $Destination) ($name + '.txt')) }
    $env:GOOS='windows'; $env:GOARCH=if ($Architecture -eq 'x86') { '386' } else { 'amd64' }; $env:CGO_ENABLED='0'
    go test -mod=vendor -tags with_gvisor ./vendor/github.com/sagernet/sing-tun -run TestPersonalVPN -count=1 -v
    if ($LASTEXITCODE -ne 0) { throw 'Windows native egress regression tests failed' }
    go build -mod=vendor -trimpath -tags with_gvisor -ldflags '-s -w -X github.com/sagernet/sing-box/constant.Version=1.14.4-personalvpn.2' -o $Destination ./cmd/sing-box
    if ($LASTEXITCODE -ne 0) { throw 'TUN core build failed' }
} finally { Pop-Location }
