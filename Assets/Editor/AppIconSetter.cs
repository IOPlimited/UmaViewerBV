using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 把 Assets/AppIcon.png 设为应用图标（Windows Standalone + Android）。
/// 用 batchmode 调用：-executeMethod AppIconSetter.Run
/// </summary>
public static class AppIconSetter
{
    private const string IconPath = "Assets/AppIcon.png";

    public static void Run()
    {
        Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);

        if (icon == null)
        {
            Debug.LogError($"[AppIcon] 找不到 {IconPath}");
            EditorApplication.Exit(1);
            return;
        }

        ApplyImportSettings(IconPath);

        // 重新导入后再取一次，确保拿到未压缩、无 mip 的图
        AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceUpdate);
        icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);

        int standalone = ApplyGroup(BuildTargetGroup.Standalone, icon, "Standalone");

        // Default 段（m_BuildTarget 为空）也要换：没有独立配置的平台会回落到它
        int unknown = ApplyGroup(BuildTargetGroup.Unknown, icon, "Default");

        int android = ApplyAndroid(icon);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[AppIcon] 完成: Standalone={standalone} Default={unknown} Android={android} 个尺寸, 源图 {icon.width}x{icon.height}");
        EditorApplication.Exit(0);
    }

    /// <summary>
    /// Android 图标走 m_BuildTargetPlatformIcons（m_Kind 2 = 自适应图标），
    /// 这套的 kind 在 UnityEditor.Android 模块里，取不到就跳过 —— 不影响 Windows 图标。
    /// </summary>
    private static int ApplyAndroid(Texture2D icon)
    {
        Debug.LogWarning("[AppIcon] Android 自适应图标跳过（需要 UnityEditor.Android 的 PlatformIconKind，本次只设 Windows）");
        return 0;
    }

    private static void ApplyImportSettings(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;

        if (importer == null)
            return;

        importer.textureType = TextureImporterType.Default;
        importer.spriteImportMode = SpriteImportMode.None;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.isReadable = false;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.maxTextureSize = 2048;

        // 图标不能走 DXT/ETC 压缩，边缘会出现色块，alpha 也可能被削
        var platform = importer.GetDefaultPlatformTextureSettings();
        platform.format = TextureImporterFormat.RGBA32;
        platform.textureCompression = TextureImporterCompression.Uncompressed;
        platform.maxTextureSize = 2048;
        platform.overridden = true;
        importer.SetPlatformTextureSettings(platform);

        // Android 必须单独显式设 RGBA32：默认 override 会按平台走 ETC，
        // 透明通道会被压出硬边（这个图标 52% 面积是全透明的）。
        var android = importer.GetPlatformTextureSettings("Android");
        android.overridden = true;
        android.format = TextureImporterFormat.RGBA32;
        android.textureCompression = TextureImporterCompression.Uncompressed;
        android.maxTextureSize = 2048;
        importer.SetPlatformTextureSettings(android);

        importer.SaveAndReimport();
    }

    private static int ApplyGroup(BuildTargetGroup group, Texture2D icon, string label)
    {
        int[] sizes = PlayerSettings.GetIconSizesForTargetGroup(group, IconKind.Application);

        if (sizes == null || sizes.Length == 0)
        {
            Debug.LogWarning($"[AppIcon] {label} 没有可用的 Application 图标槽位");

            // 退回旧 API：设单个默认尺寸
            PlayerSettings.SetIconsForTargetGroup(group, new[] { icon });
            return 1;
        }

        var textures = new Texture2D[sizes.Length];

        for (int i = 0; i < sizes.Length; i++)
            textures[i] = icon;

        PlayerSettings.SetIconsForTargetGroup(group, textures, IconKind.Application);

        Debug.Log($"[AppIcon] {label} 已设置尺寸: {string.Join(",", sizes)}");
        return sizes.Length;
    }
}
