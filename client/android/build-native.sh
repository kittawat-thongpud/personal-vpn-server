#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
source_dir="${RUNNER_TEMP:?}/personalvpn-android-core"
git clone --filter=blob:none https://github.com/SagerNet/sing-box.git "$source_dir"
cd "$source_dir"
git checkout --detach c005f35300ce76427dc285481ec5695f5b179cd2
test "$(git rev-parse HEAD)" = c005f35300ce76427dc285481ec5695f5b179cd2
mkdir -p "$project/app/libs"
# The same verified upstream Xray version used by the VPS; fixtures contain only
# freshly generated CI credentials. Preserve it for the Android emulator too.
curl --fail --location --retry 3 -o "$RUNNER_TEMP/xray-fixture.zip" https://github.com/XTLS/Xray-core/releases/download/v26.10.31/Xray-linux-64.zip
printf '%s  %s\n' e4bd9119f50ae7c41658b1abd0679ae06f98bd864a2907ff33f2f2cf7081b214 "$RUNNER_TEMP/xray-fixture.zip" | sha256sum -c -
unzip -p "$RUNNER_TEMP/xray-fixture.zip" xray > "$project/app/libs/xray-fixture"
chmod +x "$project/app/libs/xray-fixture"
export PERSONAL_XRAY="$project/app/libs/xray-fixture"
cp "$project/native/personal_instance.go" experimental/libbox/personal_instance.go
cp "$project/native/health_test.go" experimental/libbox/personal_health_test.go
cp "$project/native/reality_test.go" experimental/libbox/personal_reality_test.go
mkdir -p experimental/personalvpn
cp "$project/native/core.go" experimental/personalvpn/core.go
cp "$project/native/core_test.go" experimental/personalvpn/core_test.go
gofmt -w experimental/personalvpn experimental/libbox/personal*.go
# First reproduce the failure with the unmodified upstream handshake.
PERSONAL_EXPECT_REALITY_REJECT=true go test -ldflags='-checklinkname=0' -tags 'with_gvisor,with_quic,with_utls' ./experimental/libbox -run '^TestPersonalRealityXray$' -count=1 -timeout=90s
python3 "$project/native/patch.py"
cp "$project/native/pin_test.go" common/tls/personal_pin_test.go
gofmt -w option/tls.go common/tls/std_client.go common/tls/reality_client.go
# Build the loopback diagnostic before qualification so a failing test can be
# investigated with the exact patched handshake, without a local source build.
CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath -ldflags='-s -w -checklinkname=0' -tags 'with_quic,with_utls' -o "$project/app/libs/reality-probe.exe" ./cmd/sing-box
CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath -ldflags='-s -w' -o "$project/app/libs/health-probe.exe" "$project/native/tools/health-probe/main.go"
go install github.com/sagernet/gomobile/cmd/gomobile github.com/sagernet/gomobile/cmd/gobind
go test -race -ldflags='-checklinkname=0' -tags 'with_gvisor,with_quic,with_utls' ./experimental/personalvpn
go test -tags 'with_gvisor,with_quic,with_utls' ./common/tls
go test -ldflags='-checklinkname=0' -tags 'with_gvisor,with_quic,with_utls' ./experimental/libbox -run '^TestPersonal(HealthHysteriaRepeated|RealityXray)$' -count=1 -timeout=90s
mkdir -p cmd/personal-fixture
cp "$project/test-fixture/main.go" cmd/personal-fixture/main.go
go build -trimpath -tags 'with_gvisor,with_quic,with_utls' -o "$project/app/libs/test-fixture" ./cmd/personal-fixture
"$(go env GOPATH)/bin/gomobile" bind -target=android/arm64,android/amd64 -androidapi=29 -javapkg=dev.personalvpn -libname=pvn -trimpath -ldflags='-s -w -checklinkname=0' -tags=with_gvisor,with_quic,with_utls -o "$project/app/libs/personalvpn.aar" ./experimental/personalvpn
cp LICENSE "$project/app/libs/LICENSE-sing-box.txt"
git rev-parse HEAD > "$project/app/libs/CORE-SOURCE.txt"
sha256sum "$project/app/libs/personalvpn.aar" > "$project/app/libs/CORE-SHA256.txt"
