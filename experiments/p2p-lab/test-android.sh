#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
export PATH="$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH"
export ANDROID_USER_HOME="$RUNNER_TEMP/p2p-lab-android-user"
export ANDROID_EMULATOR_HOME="$ANDROID_USER_HOME"
export ANDROID_AVD_HOME="$ANDROID_USER_HOME/avd"
adb_binary="$ANDROID_HOME/platform-tools/adb"
adb() { timeout 60 "$adb_binary" "$@"; }
mkdir -p "$ANDROID_AVD_HOME" "$project/android-evidence"
sdkmanager 'platform-tools' 'emulator' 'system-images;android-34;google_apis;x86_64' >/dev/null
sudo chmod 666 /dev/kvm
trap 'timeout 10 "$adb_binary" logcat -d -t 500 > "$project/android-evidence/logcat.txt" 2>/dev/null || true; timeout 10 "$adb_binary" emu kill 2>/dev/null || true' EXIT
echo no | avdmanager create avd --force --name p2plab --path "$ANDROID_AVD_HOME/p2plab.avd" --package 'system-images;android-34;google_apis;x86_64' --device pixel_6
printf 'avd.ini.encoding=UTF-8\npath=%s\ntarget=android-34\n' "$ANDROID_AVD_HOME/p2plab.avd" > "$ANDROID_AVD_HOME/p2plab.ini"
emulator -avd p2plab -no-window -no-audio -no-boot-anim -no-snapshot -no-metrics -gpu swiftshader_indirect -memory 2048 > "$project/android-evidence/emulator.txt" 2>&1 &
emulator_pid=$!
timeout 180 "$adb_binary" wait-for-device
boot_deadline=$((SECONDS + 180))
while ((SECONDS < boot_deadline)); do
  kill -0 "$emulator_pid" || exit 1
  test "$(timeout 10 "$adb_binary" shell getprop sys.boot_completed | tr -d '\r')" = 1 && break
  sleep 2
done
test "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1
adb install -r "$project/android/app/build/outputs/apk/debug/app-debug.apk"
adb install -r "$project/android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"
timeout 180 "$adb_binary" shell am instrument -w -r dev.personalvpn.p2plab.test/androidx.test.runner.AndroidJUnitRunner | tee "$project/android-evidence/result.txt"
grep -Eq '^OK \([0-9]+ tests?\)' "$project/android-evidence/result.txt"
adb exec-out run-as dev.personalvpn.p2plab cat files/android-result.json > "$project/android-evidence/verified-transfer.json"
adb shell am start -W -n dev.personalvpn.p2plab/.MainActivity
sleep 2
adb exec-out screencap -p > "$project/android-evidence/home.png"
