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
