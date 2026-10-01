#!/usr/bin/env bash
# Build the PhoneFarm stack on this linux-arm64 DGX and deploy the agent to all attached phones.
# The Android workload ships x86_64 build tools; the wrappers under ~/tools (aapt2qemu, llc,
# ld.lld) plus the arm64 shims installed under ~/.dotnet/shared make the build run natively.
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROOT="$HOME/.dotnet"
DOTNET="$HOME/.dotnet/dotnet"
export PATH="$HOME/.dotnet:$PATH"
ADB="${ADB:-/usr/bin/adb}"
export JAVA_HOME="$HOME/jdk17"
export ANDROID_HOME="$HOME/android-sdk"
export AAPT2_TOOL_PATH="$HOME/tools/aapt2qemu"
BUILD_ARGS=(-c Debug -nodeReuse:false -p:RunAOTCompilation=false)

APK="src/Agent/bin/Debug/net10.0-android/pl.mz1.phonefarm.agent-Signed.apk"

if [ ! -f bin/Dispatcher/Dispatcher ]; then
  echo "== build dispatcher =="
  $DOTNET publish src/Dispatcher/Dispatcher.csproj -c Release -o bin/Dispatcher
fi

echo "== build worker =="
$DOTNET publish src/Worker.MonteCarlo/Worker.MonteCarlo.csproj -c Release -o bin/worker-montecarlo

if [ "${SKIP_AGENT:-0}" != 1 ]; then
  echo "== build agent =="
  $DOTNET build src/Agent/Agent.csproj "${BUILD_ARGS[@]}"
fi

echo "== deploy agent to devices =="
for serial in $($ADB devices | awk 'NR>1 && $2=="device" {print $1}'); do
  echo "-- $serial"
  $ADB -s "$serial" install -r --no-streaming "$APK"
  $ADB -s "$serial" shell am force-stop pl.mz1.phonefarm.agent || true
  $ADB -s "$serial" shell am start-foreground-service \
    -n pl.mz1.phonefarm.agent/pl.mz1.phonefarm.agent.AgentService --es enable 1
done

echo
echo "start dispatcher:  nohup env PHONEFARM_URL=http://0.0.0.0:8080 DOTNET_ROOT=$DOTNET_ROOT $PWD/bin/Dispatcher/Dispatcher >/tmp/dispatcher.log 2>&1 &"
echo "submit job:        ./submit.sh 24 20000000"
