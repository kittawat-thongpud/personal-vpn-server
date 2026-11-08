#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
api="${ANDROID_TEST_API:-34}"
[[ "$api" == 34 || "$api" == 37.0 ]]
export PATH="$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH"
export ANDROID_USER_HOME="$RUNNER_TEMP/pvn-android-user"
export ANDROID_EMULATOR_HOME="$ANDROID_USER_HOME"
export ANDROID_AVD_HOME="$ANDROID_USER_HOME/avd"
adb_binary="$ANDROID_HOME/platform-tools/adb"
# Device operations and crash-log collection must not wait for a dead emulator.
adb() { timeout 90 "$adb_binary" "$@"; }
mkdir -p "$ANDROID_AVD_HOME"
# The emulator needs only its two Linux gateways and debug/test APKs. Free the
# redundant native build inputs/Windows probes and release APK in this job's
# checkout. The signed release is retained in the source-scoped app cache.
rm -f -- "$project/app/libs/personalvpn.aar" "$project/app/libs/reality-probe.exe" "$project/app/libs/health-probe.exe" "$project/app/build/outputs/apk/release/app-release.apk"
df -h "$RUNNER_TEMP"
sdkmanager 'platform-tools' 'emulator' "system-images;android-$api;google_apis;x86_64" >/dev/null
sudo chmod 666 /dev/kvm
sudo ip address add 198.18.0.1/32 dev lo
printf '198.18.0.1 fixture.local\n' | sudo tee -a /etc/hosts >/dev/null
chmod +x "$project/app/libs/test-fixture" "$project/app/libs/xray-fixture"
"$project/app/libs/test-fixture" "$RUNNER_TEMP/fixture.json" "$project/app/libs/xray-fixture" > "$RUNNER_TEMP/fixture.log" 2>&1 &
fixture_pid=$!
trap 'mkdir -p "$project/app/build/emulator"; timeout 10 "$adb_binary" logcat -d -t 2000 > "$project/app/build/emulator/logcat.txt" 2>/dev/null || true; timeout 10 "$adb_binary" shell dumpsys activity exit-info dev.personalvpn.client > "$project/app/build/emulator/exit-info.txt" 2>/dev/null || true; kill "$fixture_pid" 2>/dev/null || true; timeout 10 "$adb_binary" emu kill 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do test -f "$RUNNER_TEMP/fixture.json" && break; sleep 1; done
test -f "$RUNNER_TEMP/fixture.json"
echo no | avdmanager create avd --force --name pvn --path "$ANDROID_AVD_HOME/pvn.avd" --package "system-images;android-$api;google_apis;x86_64" --device pixel_6
test -f "$ANDROID_AVD_HOME/pvn.avd/config.ini"
# Bound userdata to 3 GiB rather than the device template's 7+ GiB default.
python3 - "$ANDROID_AVD_HOME/pvn.avd/config.ini" <<'AVD_SIZE'
from pathlib import Path
import sys
p = Path(sys.argv[1])
lines = [line for line in p.read_text().splitlines() if not line.startswith('disk.dataPartition.size=')]
p.write_text('\n'.join(lines + ['disk.dataPartition.size=3G']) + '\n')
AVD_SIZE
# Keep the manager and the emulator on the same explicit task-local AVD index.
printf 'avd.ini.encoding=UTF-8\npath=%s\ntarget=android-%s\n' "$ANDROID_AVD_HOME/pvn.avd" "$api" > "$ANDROID_AVD_HOME/pvn.ini"
memory=2048
if [ "$api" = 37.0 ]; then memory=3072; fi
emulator -avd pvn -no-window -no-audio -no-boot-anim -no-snapshot -no-metrics -gpu swiftshader -memory "$memory" -partition-size 3072 > "$RUNNER_TEMP/emulator.log" 2>&1 &
emulator_pid=$!
sleep 2
if ! kill -0 "$emulator_pid" 2>/dev/null || grep -q 'FATAL' "$RUNNER_TEMP/emulator.log"; then cat "$RUNNER_TEMP/emulator.log"; exit 1; fi
timeout 180 "$adb_binary" wait-for-device
boot_deadline=$((SECONDS + 180))
while ((SECONDS < boot_deadline)); do
  if ! kill -0 "$emulator_pid" 2>/dev/null || grep -q 'FATAL' "$RUNNER_TEMP/emulator.log"; then cat "$RUNNER_TEMP/emulator.log"; exit 1; fi
  test "$(timeout 10 "$adb_binary" shell getprop sys.boot_completed | tr -d '\r')" = 1 && break
  sleep 2
done
test "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1
test "$(adb shell getprop ro.build.version.sdk | tr -d '\r')" = "${api%%.*}"
printf 'Emulator qualification API: %s\n' "$api"
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
# Network-recovery scenarios outlast the emulator's screen timeout. Keep the
# isolated UI-test device awake; screen-off recovery is separate acceptance.
adb shell svc power stayon true
adb shell input keyevent KEYCODE_WAKEUP
adb shell wm dismiss-keyguard
adb install -r "$project/app/build/outputs/apk/debug/app-debug.apk"
adb install -r "$project/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk"
adb install -r "$project/probe/build/outputs/apk/debug/probe-debug.apk"
adb install -r "$project/probeAlias/build/outputs/apk/debug/probeAlias-debug.apk"
adb shell appops set dev.personalvpn.client ACTIVATE_VPN allow
adb shell pm grant dev.personalvpn.client android.permission.POST_NOTIFICATIONS
fixture=$(base64 -w 0 "$RUNNER_TEMP/fixture.json")
mkdir -p "$project/app/build/emulator"
instrument_status=0
timeout 480 "$adb_binary" shell am instrument -w -r -e fixture "$fixture" dev.personalvpn.client.test/androidx.test.runner.AndroidJUnitRunner | tee "$project/app/build/emulator/result.txt" || instrument_status=$?
# The test-only recovery progress is written to Android's System.out log.
# Preserve it in the job log even when optional report storage is unavailable.
adb logcat -d -s System.out:I AndroidRuntime:E '*:S' | tail -n 120
if ! grep -Eq '^OK \([0-9]+ tests?\)' "$project/app/build/emulator/result.txt"; then instrument_status=1; fi
for name in app-tunnel site-tunnel home-ui home-large-font home-pending protocol-options background-guidance; do
  destination="$project/app/build/emulator/$name.png"
  if ! adb exec-out run-as dev.personalvpn.client cat "files/$name.png" > "$destination"; then
    rm -f "$destination"
  elif ! head -c 8 "$destination" | cmp -s - <(printf '\x89PNG\r\n\x1a\n'); then
    rm -f "$destination"
  fi
done
adb shell am start -W -n dev.personalvpn.client/.MainActivity
sleep 2
adb exec-out screencap -p > "$project/app/build/emulator/home.png"
adb logcat -d -t 300 > "$project/app/build/emulator/logcat.txt"
exit "$instrument_status"
