using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Gallop
{
    /// <summary>
    /// 从 Resources/Localize/zh-CN-names.json 读角色名 / Mob 名（table 6 / 59）。
    ///
    /// Localize.CN 里手写的常量只覆盖一百多个 ID，其余以前直接回落到数据库原文（日文），
    /// 所以 9007~9052、1146、2001 这些角色一直显示日文原名。这里按表补齐。
    /// </summary>
    public static class LocalizeTables
    {
        private const string ResourcePath = "Localize/zh-CN-names";

        private static Dictionary<int, string> _chara;
        private static Dictionary<int, string> _mob;
        private static bool _loaded;
        private static readonly HashSet<int> _missing = new HashSet<int>();

        public static string GetCharaName(int id)
        {
            Ensure();

            // 只查角色表：跨表兜底会把 mob 的名字当成角色名（9052 就因此显示成「纳杰瓦尔」）
            if (_chara != null && _chara.TryGetValue(id, out string value))
                return value;

            LogMissing(id);
            return null;
        }

        public static string GetMobName(int id)
        {
            Ensure();

            if (_mob != null && _mob.TryGetValue(id, out string value))
                return value;

            LogMissing(id);
            return null;
        }

        /// <summary>表里没有这个名字时记一次，便于后续补表（同一 ID 只记一次）。</summary>
        private static void LogMissing(int id)
        {
            if (_missing.Add(id))
                Debug.Log($"[LocalizeTables] 表里没有 {id}，回落到数据库原文");
        }

        private static void Ensure()
        {
            if (_loaded)
                return;

            _loaded = true;

            try
            {
                TextAsset asset = Resources.Load<TextAsset>(ResourcePath);

                if (asset == null)
                {
                    Debug.LogWarning("[LocalizeTables] 找不到 " + ResourcePath);
                    return;
                }

                var root = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(asset.text);

                if (root == null)
                    return;

                _chara = ToMap(root, "6");
                _mob = ToMap(root, "59");

                Debug.Log($"[LocalizeTables] chara={_chara.Count} mob={_mob.Count}");
            }
            catch (System.Exception error)
            {
                Debug.LogWarning("[LocalizeTables] 解析失败: " + error.Message);
            }
        }

        private static Dictionary<int, string> ToMap(
            Dictionary<string, Dictionary<string, string>> root, string key)
        {
            var map = new Dictionary<int, string>();

            if (!root.TryGetValue(key, out Dictionary<string, string> table) || table == null)
                return map;

            foreach (var pair in table)
            {
                if (int.TryParse(pair.Key, out int id) && !string.IsNullOrEmpty(pair.Value))
                    map[id] = pair.Value;
            }

            return map;
        }
    }
}
