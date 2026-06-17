using System;
using System.Linq;
using Verse;

namespace YART.Compat
{
    /// <summary>
    /// Semi Random Research 호환 어댑터
    ///
    /// SRR 활성화 시, 연구 큐 조작을 못하도록 막는다
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SemiRandomResearchCompat
    {
        private const string PackageIdMarker = "semirandom";

        public static bool Active { get; }

        static SemiRandomResearchCompat()
        {
            Active = LoadedModManager.RunningModsListForReading.Any(
                mod => mod.PackageId != null
                    && mod.PackageId.IndexOf(PackageIdMarker, StringComparison.OrdinalIgnoreCase) >= 0);

            if (Active)
            {
                Log.Message("[YART] Semi Random Research detected — manual research manipulation disabled.");
            }
        }
    }
}
