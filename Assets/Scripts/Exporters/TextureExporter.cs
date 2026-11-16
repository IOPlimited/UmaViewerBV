using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

public static class TextureExporter
{
    public static Texture2D duplicateTexture(Texture2D source) => ReadTexture(source, source.width, source.height);

    internal static Texture2D ReadTexture(Texture source, int width, int height, Material material = null)
    {
        var previous = RenderTexture.active;
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Texture2D result = null;
        bool srgb = GL.sRGBWrite;
        try
        {
            GL.sRGBWrite = false;
            if (material == null) Graphics.Blit(source, target); else Graphics.Blit(source, target, material);
            RenderTexture.active = target;
            result = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = source.name };
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0); result.Apply();
            return result;
        }
        catch { if (result != null) UnityEngine.Object.DestroyImmediate(result); throw; }
        finally { GL.sRGBWrite = srgb; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
    }

    internal static string WritePng(string directory, Texture2D texture, string name = null,
        IDictionary<string, string> writtenNames = null)
    {
        byte[] bytes = texture.EncodeToPNG();
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        string stem = string.IsNullOrEmpty(name) ? texture.name : name;
        foreach (char invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        stem = stem.TrimEnd(' ', '.');
        if (stem.Length == 0) stem = "texture";
        // Retain the game's readable character/resource name. Only different
        // baked images sharing that name need a content suffix.
        string relative = "Texture2D/" + stem + ".png";
        if (writtenNames != null && writtenNames.TryGetValue(relative, out string previous) && previous != hash)
            relative = "Texture2D/" + stem + "_" + hash.Substring(0, 8) + ".png";
        if (writtenNames != null) writtenNames[relative] = hash;
        if (directory != null)
        {
            Directory.CreateDirectory(Path.Combine(directory, "Texture2D"));
            File.WriteAllBytes(Path.Combine(directory, relative), bytes);
        }
        return relative;
    }

    public static string[] ExportAllTexture(string path, GameObject gameobj)
    {
        var paths = new HashSet<string>();
        var seen = new HashSet<Texture2D>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Renderer renderer in PMXGeometryExporter.Collect(gameobj))
            foreach (Material material in renderer.sharedMaterials)
                foreach (string property in material.GetTexturePropertyNames())
                    if (material.GetTexture(property) is Texture2D texture && seen.Add(texture))
                    {
                        var copy = duplicateTexture(texture);
                        try { paths.Add(WritePng(path, copy, texture.name, names)); }
                        finally { UnityEngine.Object.DestroyImmediate(copy); }
                    }
        var result = new string[paths.Count]; paths.CopyTo(result); Array.Sort(result, StringComparer.Ordinal); return result;
    }
}
