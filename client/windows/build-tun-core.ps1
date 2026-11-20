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
    $networkText = (Add-PathTiming $networkText).Replace($needle, ("`t`t`t" + 'if remoteAddr.IsValid() { defer pvnpath.Start("adapter_route")(); return tun.PersonalVPNBypassInterface(remoteAddr, r.interfaceMonitor.MyInterfaces()) }' + "`n`t`t`t" + 'defer pvnpath.Start("adapter_default")()' + "`n" + $needle))
    [IO.File]::WriteAllText($networkFile,$networkText)
    $routeFile = Join-Path $source 'route/route.go'
    $routeText = Add-PathTiming ([IO.File]::ReadAllText($routeFile).Replace("`r`n","`n"))
    $routeText = Replace-TimingNeedle $routeText 'fatalErr = r.prepareMatchMetadata(ctx, metadata)' ('defer pvnpath.Start("policy_" + metadata.Network)()' + "`n`t" + 'fatalErr = r.prepareMatchMetadata(ctx, metadata)')
    [IO.File]::WriteAllText($routeFile, $routeText)
    $connFile = Join-Path $source 'route/conn.go'
    $connText = Add-PathTiming ([IO.File]::ReadAllText($connFile).Replace("`r`n","`n"))
    $needleTcp = "`tif len(metadata.DestinationAddresses) > 0 || metadata.Destination.IsIP() {"
    $connText = Replace-TimingNeedle $connText $needleTcp ("`t" + 'finishDial := pvnpath.Direct(metadata.RouteOutbound, "socket_dial")' + "`n" + $needleTcp)
    $tcpEnd = $connText.IndexOf("`n`tif err != nil {", $connText.IndexOf($needleTcp))
    if ($tcpEnd -lt 0) { throw 'Pinned TCP dial timing endpoint changed' }
    $connText = $connText.Insert($tcpEnd, "`n`tfinishDial()")
    $udpStart = "`tif metadata.UDPConnect {"
    $connText = Replace-TimingNeedle $connText $udpStart ("`t" + 'finishDial := pvnpath.Direct(metadata.RouteOutbound, "socket_udp")' + "`n" + $udpStart)
    $udpEnd = $connText.IndexOf("`n`terr = N.ReportPacketConnHandshakeSuccess", $connText.IndexOf($udpStart))
    if ($udpEnd -lt 0) { throw 'Pinned UDP setup timing endpoint changed' }
    $connText = $connText.Insert($udpEnd, "`n`tfinishDial()")
    [IO.File]::WriteAllText($connFile, $connText)
    $native = Join-Path $source 'vendor/github.com/sagernet/sing-tun'
    foreach ($name in @('personalvpn_route_windows.go','personalvpn_route_windows_test.go','tun_bind_windows.go')) {
        Copy-Item -LiteralPath (Join-Path $patchRoot $name) -Destination (Join-Path $native $name)
    }
    gofmt -w $file
    if ($LASTEXITCODE -ne 0) { throw 'TUN patch formatting failed' }
    gofmt -w $networkFile $routeFile $connFile (Join-Path $package.FullName 'path_timing.go') $native/personalvpn_route_windows.go $native/personalvpn_route_windows_test.go $native/tun_bind_windows.go
    if ($LASTEXITCODE -ne 0) { throw 'Route patch formatting failed' }
    Copy-Item $file (Join-Path (Split-Path $Destination) 'TUN-SOURCE.txt')
    foreach ($name in @('personalvpn_route_windows.go','tun_bind_windows.go')) { Copy-Item (Join-Path $native $name) (Join-Path (Split-Path $Destination) ($name + '.txt')) }
    $env:GOOS='windows'; $env:GOARCH=if ($Architecture -eq 'x86') { '386' } else { 'amd64' }; $env:CGO_ENABLED='0'
    go test -mod=vendor -tags with_gvisor ./vendor/github.com/sagernet/sing-tun -run TestPersonalVPN -count=1 -v
    if ($LASTEXITCODE -ne 0) { throw 'Windows native egress regression tests failed' }
    go build -mod=vendor -trimpath -tags with_gvisor -ldflags '-s -w -X github.com/sagernet/sing-box/constant.Version=1.14.4-personalvpn.2' -o $Destination ./cmd/sing-box
    if ($LASTEXITCODE -ne 0) { throw 'TUN core build failed' }
} finally { Pop-Location }
