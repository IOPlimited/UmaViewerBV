using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class UmaViewerAndroidBuild
{
    public const string SpringPluginPath = "Assets/Plugins/Android/libs/arm64-v8a/libUmaSpringNative.so";

    static void Configure()
    {
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        EditorUserBuildSettings.buildAppBundle = false;
    }

    public static void Run()
    {
        Configure();
        var arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "-androidBuildOutput");
        if (index < 0 || index + 1 >= arguments.Length)
            throw new ArgumentException("Missing -androidBuildOutput");
        string output = Path.GetFullPath(arguments[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        bool previousSigning = PlayerSettings.Android.useCustomKeystore;
        try
        {
            // Local test APKs use the Android debug keystore; CI keeps release signing.
            PlayerSettings.Android.useCustomKeystore = false;
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None
            });
            if (result.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException("Android IL2CPP build failed: " + result.summary.result);
            Debug.Log("[AndroidBuild] Succeeded: " + output);
        }
        finally { PlayerSettings.Android.useCustomKeystore = previousSigning; }
    }
}

public sealed class UmaViewerAndroidSpringBuildCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;
    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.Android) return;
        if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
            throw new BuildFailedException("CySpring currently supports Android ARM64. Select ARM64 only.");
        if (!File.Exists(UmaViewerAndroidBuild.SpringPluginPath))
            throw new BuildFailedException("Build the Android CySpring plugin with Native/UmaSpringNative/build_android.py first.");
        byte[] header = new byte[20];
        using (var file = File.OpenRead(UmaViewerAndroidBuild.SpringPluginPath))
            if (file.Read(header, 0, header.Length) != header.Length)
                throw new BuildFailedException("The Android CySpring plugin is incomplete.");
        if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
            header[4] != 2 || header[5] != 1 || header[18] != 183 || header[19] != 0)
            throw new BuildFailedException("The Android CySpring plugin must be an AArch64 ELF64 library.");
    }
}
