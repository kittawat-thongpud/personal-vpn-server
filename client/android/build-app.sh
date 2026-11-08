#!/usr/bin/env bash
set -euo pipefail
project=$(cd "$(dirname "$0")" && pwd)
sdkmanager 'platforms;android-35' 'build-tools;35.0.0' >/dev/null
archive="${RUNNER_TEMP:?}/gradle-8.11.1-bin.zip"
curl --fail --location --retry 3 https://services.gradle.org/distributions/gradle-8.11.1-bin.zip -o "$archive"
printf '%s  %s\n' f397b287023acdba1e9f6fc5ea72d22dd63669d59ed4a289a29b1a76eee151c6 "$archive" | sha256sum -c -
unzip -q "$archive" -d "$RUNNER_TEMP"
cd "$project"
mkdir -p app/src/main/assets
cp app/libs/LICENSE-sing-box.txt app/src/main/assets/LICENSE-sing-box.txt
"$RUNNER_TEMP/gradle-8.11.1/bin/gradle" --no-daemon :app:testDebugUnitTest :app:lintDebug :app:assembleDebug :app:assembleDebugAndroidTest :app:assembleRelease :probe:assembleDebug :probeAlias:assembleDebug
