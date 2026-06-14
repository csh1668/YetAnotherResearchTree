using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;

namespace YART.Compat
{
    /// <summary>
    /// Vanilla Gravship Expanded 호환 어댑터
    /// </summary>
    public static class GravshipCompat
    {
        public static readonly Lazy<bool> IsVanillaGravshipExpandedLoaded = new Lazy<bool>(() =>
        {
            return LoadedModManager.RunningModsListForReading.Any(mod => mod.PackageId == "vanillaexpanded.gravship");
        });

        public static readonly MethodInfo IsGravshipResearchMethod = AccessTools.Method("VanillaGravshipExpanded.GravshipResearchUtility:IsGravshipResearch", new Type[] { typeof(ResearchProjectDef) });

        private static readonly Lazy<FieldInfo> CurrentGravtechProjectField = new Lazy<FieldInfo>(() =>
            IsVanillaGravshipExpandedLoaded.Value
                ? AccessTools.Field("VanillaGravshipExpanded.World_ExposeData_Patch:currentGravtechProject")
                : null);

        public static ResearchProjectDef GetCurrentGravtechProject()
        {
            if (!IsVanillaGravshipExpandedLoaded.Value) return null;
            return CurrentGravtechProjectField.Value?.GetValue(null) as ResearchProjectDef;
        }
    }

    /// <summary>VGE Gravship 채널 어댑터</summary>
    public sealed class GravshipChannel : ResearchChannel
    {
        private string cachedLabel;

        public override string Id => "Gravship";
        public override string Label
        {
            get
            {
                if (cachedLabel == null)
                {
                    var tab = DefDatabase<ResearchTabDef>.GetNamedSilentFail("VGE_Gravtech");
                    cachedLabel = tab != null ? (string)tab.LabelCap : "Gravtech";
                }
                return cachedLabel;
            }
        }
        public override Color Color => new Color(0.35f, 0.85f, 0.6f);
        public override bool Matches(ResearchProjectDef def)
            => (bool)GravshipCompat.IsGravshipResearchMethod.Invoke(null, new object[] { def });
        public override ResearchProjectDef CurrentProject => GravshipCompat.GetCurrentGravtechProject();
    }
}
