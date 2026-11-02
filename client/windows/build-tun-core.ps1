param([ValidateSet('x64','x86')][string]$Architecture, [string]$Destination)
$ErrorActionPreference = 'Stop'
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
    gofmt -w $file
    if ($LASTEXITCODE -ne 0) { throw 'TUN patch formatting failed' }
    Copy-Item $file (Join-Path (Split-Path $Destination) 'TUN-SOURCE.txt')
    $env:GOOS='windows'; $env:GOARCH=if ($Architecture -eq 'x86') { '386' } else { 'amd64' }; $env:CGO_ENABLED='0'
    go build -mod=vendor -trimpath -tags with_gvisor -ldflags '-s -w -X github.com/sagernet/sing-box/constant.Version=1.14.4-personalvpn.1' -o $Destination ./cmd/sing-box
    if ($LASTEXITCODE -ne 0) { throw 'TUN core build failed' }
} finally { Pop-Location }
