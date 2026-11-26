# WakeSplashEffects

Foam trails, bow spray, mist and splashes for craft on Kerbal Space Program's oceans: boats, floatplanes, seaplanes
taking off, capsules splashing down, submarines surfacing. Works on the stock ocean and on Scatterer's.

- **Wakes:** a foam trail from each hull's bow and stern, strongest at the hump and fading as the hull gets onto the step
  and lifts off. Bow spray, spray sheets and mist at speed.

https://github.com/user-attachments/assets/c0c56279-30ca-4dae-a5ac-7711df1cf1be

https://github.com/user-attachments/assets/e073cf11-779b-411e-9043-d1134e91adbb

- **Splashes:** anything that hits the water, from a belly flop or a touchdown to a capsule's splashdown: a spray skirt,
  the crown and the jet that follows, rings spreading out, and foam left behind. A breach when something comes up out
  of the water. With sound.
  
https://github.com/user-attachments/assets/bee5bd6f-0cf8-40e7-8bda-537c26994fa2

https://github.com/user-attachments/assets/b5c4a384-6481-477f-9217-e6a14b13769d

https://github.com/user-attachments/assets/72ebeec5-d35b-41fd-907a-4835c6f568dd
  
- **With Scatterer:** the foam is painted onto Scatterer's moving waves instead of floating over them, on both its
  deferred ocean (0.09xx) and its forward ocean (0.0878, CKAN's release). On the deferred ocean the wakes' waves are
  drawn into it too.

## Install

Requires KSP 1.12 and [Harmony 2](https://github.com/KSPModdingLibs/HarmonyKSP) (`000_Harmony`; used for the Scatterer
hooks). Scatterer is optional.

Copy `GameData/WakeSplashEffects` from the release zip into KSP's `GameData`.

Windows and Linux. Not tried on macOS: the shaders are built for Direct3D 11 and OpenGL core.

## Settings

The toolbar button in flight opens the settings window. Changes apply at once; Save keeps them in
`GameData/WakeSplashEffects/PluginData/settings.cfg`. Intensity and brightness scale everything; each effect (foam, spray,
mist, splashes, sounds, the Scatterer painting and waves) has its own section. A modpack can change the defaults with a
`WAKESPLASHEFFECTS` config node (any of the window's settings, by name):

```
WAKESPLASHEFFECTS
{
    intensity = 0.8
    splashSounds = false
}
```

## Known issues

- A wedge-shaped hull going in edge first throws about half as much sheet and spray on a slow machine (under 50
  frames a game second) as on a fast one.
- The jet after a round body's splash comes out about 1.5 times too tall.

## License

MIT (`LICENSE.txt`).

## How it works

Each frame every loaded craft's parts are cut at the waterline (collider boxes), the cuts grouped into hulls, and each
hull emits from its wet bow and stern. Strength follows the hull's load on the water (draft against draft at rest), so
the effects peak at the hump and fade as the hull rises onto the step and lifts off. It reads Scatterer's wave height
under each part from `PartBuoyancy.waterLevel`.

## Build

```
MSBuild Source/WakeSplashEffects.csproj -restore -p:Configuration=Release [-p:KspManaged=<KSP>/KSP_x64_Data/Managed/]
```

Output: `GameData/WakeSplashEffects/Plugins/WakeSplashEffects.dll`.

## Foam on Scatterer's waves

With Scatterer 0.09xx (its deferred ocean), the foam trail is painted into Scatterer's ocean gbuffer instead of drawn as
particles, so Scatterer lights it on the displaced waves. The foam particles are rendered from above into a foam map
around the camera (on a layer the flight cameras skip); a command buffer in Scatterer's ordered `AfterForwardOpaque`
list, after its ocean, rebuilds each ocean pixel's position from `oceanGbufferDepth`, samples the map, and points the
global `oceanGbufferFoam` at a copy holding max(Scatterer's foam, ours). Without it the particles are drawn as before.

The shader is in `Unity/` (Unity 2019.4 project). KSP 1.12 runs 2019.4.18, which can no longer be licensed; build with
2019.4.40 and then rewrite the bundle's D3D11 programs to the older header (otherwise KSP reports them unsupported):

```
Unity.exe -batchmode -quit -nographics -projectPath Unity -executeMethod BuildBundles.Build -logFile build.log
python Unity/fix_bundle.py GameData/WakeSplashEffects/Shaders/wakesplasheffects.shaders   # needs UnityPy
```

## Tests

`KspTests/` holds in-game tests run with [KspTest](https://github.com/chambem42/KspTest) (a submodule:
`git submodule update --init`). `SplashTests` drops bodies onto still water (a cylinder upright and on its side, a ball,
a wedge, the Mk1-3 pod at Apollo's size) and surfaces a submarine, recording FAR's water slam and the splash; the crafts
come from `KspTests/WakeSplashEffects.KspTests.Run/crafts/generator/splash_crafts.py`. FAR's water model makes the slam: until
a stable FAR release has it, the tests pin FAR's testing release 0.17.0.4 from CKAN. To test another FAR build, point
`FarGameData` (or `FAR_GAMEDATA`) at its `GameData/FerramAerospaceResearch`; set `KspDir` (or `KSPDIR`) to a KSP install.
Both go in `KspTests/Directory.Build.props.user`. CustomProceduralParts is built from
its repository at a pinned commit (`SplashTests.Mods`).

```
cd KspTests
dotnet build WakeSplashEffects.KspTests.Run
KSPTEST_EXPLICIT=1 KSPTEST_PARAMS="cylinderDrops=8@5" dotnet test WakeSplashEffects.KspTests.Run --no-build --filter "FullyQualifiedName~Splash_CylinderDrops"
```

CI (`.github/workflows/tests.yml`) runs them through KspTest's reusable workflow on Windows and Linux, building the mod
against the game's assemblies (`.github/ci/build.sh`). A push runs each drop body's 4 t drop from 20 m, which
checks the particles WakeSplashEffects emits, by kind (`WakeStats`), against a baseline (`SplashTests.CheckCounts`); run the
workflow by hand with `explicit` (`1` or test names) and `params` for others.

## Release

Set the version in `Source/WakeSplashEffects.csproj` (`<Version>`) and `GameData/WakeSplashEffects/WakeSplashEffects.version`, then push a
tag of it: `git tag -a v0.1.0 -m "<notes>"; git push origin v0.1.0`. `.github/workflows/release.yml` runs the tests,
builds, packages (`tools/package.ps1`: `dist/WakeSplashEffects-<version>.zip`) and makes the GitHub release, the tag's message
its notes.
