#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
test "${GITHUB_ACTIONS:-}" = true
export PATH="$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH"
export ANDROID_USER_HOME="$RUNNER_TEMP/p2p-lab-android-user"
export ANDROID_EMULATOR_HOME="$ANDROID_USER_HOME"
export ANDROID_AVD_HOME="$ANDROID_USER_HOME/avd"
adb_binary="$ANDROID_HOME/platform-tools/adb"
adb() { timeout 60 "$adb_binary" "$@"; }
mkdir -p "$ANDROID_AVD_HOME" "$project/android-evidence"
python3 "$project/signal-server/server.py" init --state "$RUNNER_TEMP/p2p-android-controller" --endpoint https://10.0.2.2:19443 >/dev/null
python3 "$project/signal-server/server.py" serve --state "$RUNNER_TEMP/p2p-android-controller" --port 19443 > "$project/android-evidence/controller.log" 2>&1 &
controller_pid=$!
# This job has no native build. The API 34 image requires >7 GiB userdata even
# when a smaller partition is requested. Free only this ephemeral runner's
# unused NDK, rather than repeatedly timing out an emulator that exited on disk.
if test -d "$ANDROID_HOME/ndk"; then
  sdk_root=$(realpath "$ANDROID_HOME")
  ndk_path=$(realpath "$ANDROID_HOME/ndk")
  test "$ndk_path" = "$sdk_root/ndk"
  rm -rf -- "$ndk_path"
fi
df -h "$RUNNER_TEMP"
sdkmanager 'platform-tools' 'emulator' 'system-images;android-34;google_apis;x86_64' >/dev/null
sudo chmod 666 /dev/kvm
trap 'timeout 10 "$adb_binary" logcat -d -t 5000 > "$project/android-evidence/logcat.txt" 2>/dev/null || true; timeout 10 "$adb_binary" emu kill 2>/dev/null || true; kill "$controller_pid" 2>/dev/null || true' EXIT
echo no | avdmanager create avd --force --name p2plab --path "$ANDROID_AVD_HOME/p2plab.avd" --package 'system-images;android-34;google_apis;x86_64' --device pixel_6
python3 - "$ANDROID_AVD_HOME/p2plab.avd/config.ini" <<'LOW_RES'
from pathlib import Path
import sys
p=Path(sys.argv[1]);settings={'hw.lcd.width':'480','hw.lcd.height':'960','hw.lcd.density':'200','hw.cpu.ncore':'2'}
lines=[line for line in p.read_text().splitlines() if line.partition('=')[0].strip() not in settings]
p.write_text('\n'.join(lines+[f'{k}={v}' for k,v in settings.items()])+'\n')
LOW_RES
printf 'avd.ini.encoding=UTF-8\npath=%s\ntarget=android-34\n' "$ANDROID_AVD_HOME/p2plab.avd" > "$ANDROID_AVD_HOME/p2plab.ini"
emulator -avd p2plab -no-window -no-audio -no-boot-anim -no-snapshot -no-metrics -gpu swiftshader_indirect -memory 2048 > "$project/android-evidence/emulator.txt" 2>&1 &
emulator_pid=$!
sleep 2
if ! kill -0 "$emulator_pid" 2>/dev/null || grep -q 'FATAL' "$project/android-evidence/emulator.txt"; then cat "$project/android-evidence/emulator.txt"; exit 1; fi
timeout 180 "$adb_binary" wait-for-device
boot_deadline=$((SECONDS + 180))
while ((SECONDS < boot_deadline)); do
  if ! kill -0 "$emulator_pid" 2>/dev/null || grep -q 'FATAL' "$project/android-evidence/emulator.txt"; then cat "$project/android-evidence/emulator.txt"; exit 1; fi
  test "$(timeout 10 "$adb_binary" shell getprop sys.boot_completed | tr -d '\r')" = 1 && break
  sleep 2
done
test "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
adb install -r "$project/android/app/build/outputs/apk/debug/app-debug.apk"
adb install -r "$project/android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"
controller=$(base64 -w 0 "$RUNNER_TEMP/p2p-android-controller/owner-invitation.txt")
timeout 240 "$adb_binary" shell am instrument -w -r -e controller "$controller" dev.personalvpn.p2plab.test/androidx.test.runner.AndroidJUnitRunner | tee "$project/android-evidence/result.txt"
grep -Eq '^OK \([0-9]+ tests?\)' "$project/android-evidence/result.txt"
adb exec-out run-as dev.personalvpn.p2plab cat files/android-result.json > "$project/android-evidence/verified-transfer.json"
adb shell am start -W -n dev.personalvpn.p2plab/.MainActivity
sleep 2
adb exec-out screencap -p > "$project/android-evidence/home.png"
