#!/usr/bin/env bash
# The mod's build (GameData/WaterWakes/Plugins, which the test project deploys) against the game the CI set up: KSP's
# Managed assemblies and the Harmony CKAN installed into it (the csproj finds GameData/000_Harmony beside Managed).
# Then the tests to run (KSPTEST_EXPLICIT: they are all Explicit) and their parameters (KSPTEST_PARAMS): a run by hand
# (workflow_dispatch) its explicit and params inputs, read from the event rather than pasted into a command.
set -euo pipefail
m="$KSPTEST_GAME/KSP_x64_Data/Managed"
[ -d "$m" ] || m="$KSPTEST_GAME/KSP_Data/Managed"
dotnet build Source/WaterWakes.csproj -c Release "-p:KspManaged=$m/"

[ -n "${GITHUB_ENV:-}" ] || exit 0
# Otherwise (a push, a pull request, a run by hand that names none): each drop body's 4 t drop from 20 m, whose particles
# SplashTests.CheckCounts checks against their baseline.
explicit="Splash_CylinderDrops;Splash_SideCylinderDrops;Splash_SphereDrops;Splash_WedgeDrops;Splash_ApolloPodDrops"
params="drops=4@20"
if [ "${GITHUB_EVENT_NAME:-}" = workflow_dispatch ]; then
  v=$(jq -r '.inputs.explicit // ""' "$GITHUB_EVENT_PATH"); [ -z "$v" ] || explicit=$v
  v=$(jq -r '.inputs.params // ""' "$GITHUB_EVENT_PATH"); [ -z "$v" ] || params=$v
fi
for pair in "KSPTEST_EXPLICIT=$explicit" "KSPTEST_PARAMS=$params"; do
  d="EOF_$RANDOM$RANDOM"
  printf '%s<<%s\n%s\n%s\n' "${pair%%=*}" "$d" "${pair#*=}" "$d" >> "$GITHUB_ENV"
  echo "$pair"
done
