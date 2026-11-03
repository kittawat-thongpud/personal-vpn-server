#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
source_dir="${RUNNER_TEMP:?}/personalvpn-android-core"
git clone --filter=blob:none https://github.com/SagerNet/sing-box.git "$source_dir"
cd "$source_dir"
git checkout --detach c005f35300ce76427dc285481ec5695f5b179cd2
test "$(git rev-parse HEAD)" = c005f35300ce76427dc285481ec5695f5b179cd2
python3 "$project/native/patch.py"
cp "$project/native/personal_instance.go" experimental/libbox/personal_instance.go
cp "$project/native/pin_test.go" common/tls/personal_pin_test.go
mkdir -p experimental/personalvpn
cp "$project/native/core.go" experimental/personalvpn/core.go
gofmt -w experimental/personalvpn experimental/libbox/personal_instance.go option/tls.go common/tls/std_client.go
go install github.com/sagernet/gomobile/cmd/gomobile github.com/sagernet/gomobile/cmd/gobind
go test -tags 'with_gvisor,with_quic,with_utls' ./experimental/personalvpn ./common/tls
mkdir -p "$project/app/libs"
mkdir -p cmd/personal-fixture
cp "$project/test-fixture/main.go" cmd/personal-fixture/main.go
go build -trimpath -tags 'with_gvisor,with_quic,with_utls' -o "$project/app/libs/test-fixture" ./cmd/personal-fixture
"$(go env GOPATH)/bin/gomobile" bind -target=android/arm64,android/amd64 -androidapi=29 -javapkg=dev.personalvpn -libname=pvn -trimpath -ldflags='-s -w -checklinkname=0' -tags=with_gvisor,with_quic,with_utls -o "$project/app/libs/personalvpn.aar" ./experimental/personalvpn
cp LICENSE "$project/app/libs/LICENSE-sing-box.txt"
git rev-parse HEAD > "$project/app/libs/CORE-SOURCE.txt"
sha256sum "$project/app/libs/personalvpn.aar" > "$project/app/libs/CORE-SHA256.txt"
