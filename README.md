# WaterWakes

KSP mod: foam trails, bow spray and mist for craft moving through the ocean. Works on the stock ocean and on
Scatterer's (it reads Scatterer's wave height under each part from `PartBuoyancy.waterLevel`).

Each frame every loaded craft's parts are cut at the waterline (collider boxes), the cuts grouped into hulls, and each
hull emits from its wet bow and stern. Strength follows the hull's load on the water (draft against draft at rest), so
the effects peak at the hump and fade as the hull rises onto the step and lifts off.

Settings: toolbar button in flight. Changes apply live; Save writes `GameData/WaterWakes/PluginData/settings.cfg`.
Defaults can also be patched through a `WATERWAKES` config node.

## Build

```
MSBuild Source/WaterWakes.csproj -restore -p:Configuration=Release [-p:KspManaged=<KSP>/KSP_x64_Data/Managed/]
```

Output: `GameData/WaterWakes/Plugins/WaterWakes.dll`.

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
python Unity/fix_bundle.py GameData/WaterWakes/Shaders/waterwakes.shaders   # needs UnityPy
```

## Tests

`KspTests/` holds in-game tests run with [KspTest](https://github.com/chambem42/KspTest) (a submodule:
`git submodule update --init`). `SplashTests` drops bodies onto still water (a cylinder upright and on its side, a ball,
a wedge, the Mk1-3 pod at Apollo's size) and surfaces a submarine, recording FAR's water slam and the splash; the crafts
come from `KspTests/WaterWakes.KspTests.Run/crafts/generator/splash_crafts.py`. FAR's water model makes the slam: until
a FAR release has it, point `FarGameData` (or `FAR_GAMEDATA`) at a FAR build's `GameData/FerramAerospaceResearch`, and
`KspDir` (or `KSPDIR`) at a KSP install, in `KspTests/Directory.Build.props.user`. CustomProceduralParts comes from a
sibling checkout (`CppGameData`).

```
cd KspTests
dotnet build WaterWakes.KspTests.Run
KSPTEST_EXPLICIT=1 KSPTEST_PARAMS="cylinderDrops=8@5" dotnet test WaterWakes.KspTests.Run --no-build --filter "FullyQualifiedName~Splash_CylinderDrops"
```
