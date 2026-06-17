using System.Collections.Generic;
using Verse;
using YART.Data;

namespace YART
{
    public static class TabPresetManager
    {
        public static List<ResearchPreset> Presets => YARTMod.Settings.tabPresets;

        public static ResearchPreset ById(string id)
        {
            if (id == null) return null;
            return YARTMod.Settings.tabPresets.Find(p => p.Id == id);
        }

        public static ResearchPreset Create(string name, IEnumerable<string> tabDefNames)
        {
            var preset = new ResearchPreset(name, tabDefNames);
            YARTMod.Settings.tabPresets.Add(preset);
            YARTMod.Settings.Write();
            GraphBuildPipeline.RebuildPresetNonBlocking(preset);
            return preset;
        }

        public static void Update(ResearchPreset preset, string name, IEnumerable<string> tabDefNames)
        {
            if (preset == null) return;
            preset.Name = name;
            preset.TabDefNames = new List<string>(tabDefNames);
            YARTMod.Settings.Write();
            GraphBuildPipeline.RebuildPresetNonBlocking(preset);
        }

        public static void Delete(string presetId)
        {
            if (presetId == null) return;
            YARTMod.Settings.tabPresets.RemoveAll(p => p.Id == presetId);
            YARTMod.Settings.Write();
            ResearchGraph.Instance.RemovePresetSubGraph(presetId);
        }
    }
}
