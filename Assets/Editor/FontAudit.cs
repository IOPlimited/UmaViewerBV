#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 临时审计脚本：把工程里所有 TMP 字体资产的真实状态打到 log 里。
/// 用法（batchmode）：
///   Unity.exe -batchmode -nographics -quit -projectPath . -executeMethod FontAudit.Run -logFile audit.log
/// </summary>
public static class FontAudit
{
    private const string OutPath = "fontaudit.txt";

    [MenuItem("Tools/Font Audit")]
    public static void Run()
    {
        StringBuilder sb = new StringBuilder();

        try
        {
            Dump(sb);
            File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[FontAudit] 结果写入 " + Path.GetFullPath(OutPath));
        }
        catch (Exception error)
        {
            sb.AppendLine("FAILED: " + error);
            File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
            Debug.LogError("[FontAudit] " + error);
        }
    }

    private static void Dump(StringBuilder sb)
    {
        sb.AppendLine("== TMP Settings ==");
        sb.AppendLine("defaultFontAsset = " + Describe(TMP_Settings.defaultFontAsset));

        try
        {
            // 静态属性，不能走实例
            var fallbacks = TMP_Settings.fallbackFontAssets;
            sb.AppendLine("fallbackFontAssets = " + (fallbacks == null ? 0 : fallbacks.Count));

            if (fallbacks != null)
                foreach (TMP_FontAsset f in fallbacks)
                    sb.AppendLine("    " + Describe(f));
        }
        catch (Exception error)
        {
            sb.AppendLine("读取 fallbackFontAssets 失败: " + error.Message);
        }

        sb.AppendLine();
        sb.AppendLine("== Font Assets ==");

        string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TMP_FontAsset asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);

            if (asset == null)
                continue;

            int atlasCount = asset.atlasTextures == null ? 0 : asset.atlasTextures.Length;
            int alive = asset.atlasTextures == null ? 0 : asset.atlasTextures.Count(t => t != null);

            sb.AppendLine($"{Path.GetFileName(path)}");
            sb.AppendLine($"    glyphs={asset.glyphTable?.Count ?? -1}  chars={asset.characterTable?.Count ?? -1}  "
                + $"atlas={alive}/{atlasCount}  mode={asset.atlasPopulationMode}");
            sb.AppendLine($"    srcFont={(asset.sourceFontFile != null ? asset.sourceFontFile.name : "<null>")}  "
                + $"fallbacks={asset.fallbackFontAssetTable?.Count ?? 0}  "
                + $"atlasW={asset.atlasWidth}  atlasH={asset.atlasHeight}");

            if (asset.atlasTextures != null)
            {
                for (int i = 0; i < asset.atlasTextures.Length; i++)
                {
                    Texture2D t = asset.atlasTextures[i];
                    sb.AppendLine($"      [{i}] " + (t == null
                        ? "<null>"
                        : $"{t.width}x{t.height} fmt={t.format}"));
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("== 场景里的字体引用 ==");

        string[] scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes" });

        foreach (string guid in scenes)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            sb.AppendLine(Path.GetFileName(path) + ": " + CountFontRefs(path));
        }

        sb.AppendLine();
        sb.AppendLine("== CJK 字形覆盖检测 ==");
        DumpCoverage(sb);
    }

    private static string Describe(TMP_FontAsset f)
    {
        if (f == null)
            return "<null>";

        return $"{f.name} (mode={f.atlasPopulationMode}, glyphs={f.glyphTable?.Count ?? -1})";
    }

    private static string CountFontRefs(string scenePath)
    {
        // 直接读 YAML：m_fontAsset 后面跟 guid
        Dictionary<string, int> counts = new Dictionary<string, int>();

        foreach (string line in File.ReadLines(scenePath))
        {
            int at = line.IndexOf("m_fontAsset:", StringComparison.Ordinal);

            if (at < 0)
                continue;

            int gi = line.IndexOf("guid: ", at, StringComparison.Ordinal);

            if (gi < 0)
                continue;

            string guid = line.Substring(gi + 6).Trim().TrimEnd('}', ' ');

            if (guid.Length > 32)
                guid = guid.Substring(0, 32);

            counts.TryGetValue(guid, out int n);
            counts[guid] = n + 1;
        }

        return counts.Count == 0
            ? "无引用"
            : string.Join(", ", counts.Select(kv =>
            {
                string p = AssetDatabase.GUIDToAssetPath(kv.Key);
                string name = string.IsNullOrEmpty(p) ? kv.Key : Path.GetFileNameWithoutExtension(p);
                return name + "=" + kv.Value;
            }));
    }

    /// <summary>检查几个常用汉字在当前默认字体里到底能不能取到。</summary>
    private static void DumpCoverage(StringBuilder sb)
    {
        string probe = "成员数语音选择复制一号位角色随机服装特别周无声铃鹿东海帝王大和赤骥";

        foreach (TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (f == null || f.characterTable == null)
                continue;

            HashSet<uint> have = new HashSet<uint>(f.characterTable.Select(c => c.unicode));

            int miss = probe.Where(ch => !have.Contains(ch)).Count();

            sb.AppendLine($"{f.name}: 探针缺 {miss}/{probe.Length} 字"
                + (miss > 0 ? " 缺: " + string.Concat(probe.Where(ch => !have.Contains(ch)).Distinct()) : ""));
        }
    }
}
#endif
