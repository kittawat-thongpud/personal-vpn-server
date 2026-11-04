#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
sdkmanager 'platforms;android-35' 'build-tools;35.0.0' >/dev/null
archive="${RUNNER_TEMP:?}/gradle-8.11.1-bin.zip"
curl --fail --location --retry 3 https://services.gradle.org/distributions/gradle-8.11.1-bin.zip -o "$archive"
printf '%s  %s\n' f397b287023acdba1e9f6fc5ea72d22dd63669d59ed4a289a29b1a76eee151c6 "$archive" | sha256sum -c -
unzip -q "$archive" -d "$RUNNER_TEMP"
mkdir -p "$project/android/app/src/main/assets"
cp "$project/dist/Personal-VPN-P2P-Lab.html" "$project/android/app/src/main/assets/"
export P2P_LAB_KEYSTORE="$RUNNER_TEMP/p2p-lab.p12"
keytool -genkeypair -noprompt -keystore "$P2P_LAB_KEYSTORE" -storetype PKCS12 -storepass p2plab-test-only -alias p2plab -keypass p2plab-test-only -keyalg RSA -keysize 2048 -validity 30 -dname 'CN=Personal VPN P2P Lab Experiment'
cd "$project/android"
"$RUNNER_TEMP/gradle-8.11.1/bin/gradle" --no-daemon :app:lintDebug :app:assembleDebug :app:assembleDebugAndroidTest :app:assembleRelease
cp app/build/outputs/apk/release/app-release.apk "$project/dist/PersonalVPN-P2P-Lab.apk"
"$ANDROID_HOME/build-tools/35.0.0/apksigner" verify --verbose --print-certs "$project/dist/PersonalVPN-P2P-Lab.apk" > "$project/dist/ANDROID-LAB-SIGNATURE.txt"
