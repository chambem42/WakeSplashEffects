using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Batch mode: Unity.exe -batchmode -quit -projectPath Unity -executeMethod BuildBundles.Build -logFile build.log
// Writes ../GameData/WaterWakes/Shaders/waterwakes.shaders (Windows: D3D11 + OpenGL core).
public static class BuildBundles
{
    public static void Build()
    {
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameData", "WaterWakes", "Shaders"));
        Directory.CreateDirectory(outDir);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        var build = new AssetBundleBuild
        {
            assetBundleName = "waterwakes.shaders",
            assetNames = new[] { "Assets/Shaders/OceanFoam.shader", "Assets/Shaders/Test.shader" },
        };
        AssetBundleManifest m = BuildPipeline.BuildAssetBundles(outDir, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression,
                                                                BuildTarget.StandaloneWindows64);
        if (m == null)
        {
            Debug.LogError("[BuildBundles] failed");
            EditorApplication.Exit(1);
        }
        // only the bundle is wanted in GameData
        foreach (string f in new[] { "Shaders", "Shaders.manifest", "waterwakes.shaders.manifest" })
            File.Delete(Path.Combine(outDir, f));
        Debug.Log("[BuildBundles] built " + Path.Combine(outDir, "waterwakes.shaders"));
    }
}
