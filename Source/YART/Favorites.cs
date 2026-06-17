using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;

namespace YART
{
    /// <summary>
    /// 연구 탭 즐겨찾기 관련 헬퍼
    /// </summary>
    public static class Favorites
    {
        public static readonly Color OnColor = new Color(1f, 0.84f, 0.22f);
        public static readonly Color OffColor = new Color(0.55f, 0.6f, 0.7f);

        public static string TabKey(ResearchTabDef tab) => "t:" + tab.defName;
        public static string PresetKey(string presetId) => "p:" + presetId;

        public static bool Has(string key) => key != null && YARTMod.Settings.favorites.Contains(key);

        public static void Toggle(string key)
        {
            if (key == null) return;
            var favs = YARTMod.Settings.favorites;
            if (!favs.Remove(key)) favs.Add(key);
            YARTMod.Settings.Write();
        }

        public struct Entry
        {
            public GraphKey Key;
            public string Label;
        }

        public static List<Entry> Resolve()
        {
            var result = new List<Entry>();
            foreach (var raw in YARTMod.Settings.favorites)
            {
                if (raw == null || raw.Length < 3) continue;
                string id = raw.Substring(2);
                if (raw[0] == 't')
                {
                    var tab = DefDatabase<ResearchTabDef>.GetNamedSilentFail(id);
                    if (tab != null)
                        result.Add(new Entry { Key = new GraphKey(ChannelRegistry.Bench, tab), Label = tab.LabelCap });
                }
                else if (raw[0] == 'p')
                {
                    var preset = TabPresetManager.ById(id);
                    if (preset != null && preset.ResolveTabs().Count > 0)
                        result.Add(new Entry { Key = GraphKey.ForPreset(id), Label = preset.Name });
                }
            }
            return result;
        }
    }
}
