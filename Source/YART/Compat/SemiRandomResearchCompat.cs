using System;
using System.Linq;
using Verse;

namespace YART.Compat
{
    /// <summary>
    /// Semi Random Research 호환 어댑터
    ///
    /// SRR이 감지되고 + YART 설정의 호환 기능이 켜져 있을 때만 연구 큐 조작을 막는다.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SemiRandomResearchCompat
    {
        private const string PackageIdMarker = "semirandom";

        /// <summary>SRR이 설치·로드되어 있는지</summary>
        public static bool Detected { get; }

        /// <summary>실제로 연구 큐 조작을 차단할지 — SRR 감지 + 설정의 호환 기능 ON일 때만.</summary>
        public static bool Active => Detected && (YARTMod.Settings?.semiRandomCompatEnabled ?? true);

        static SemiRandomResearchCompat()
        {
            Detected = LoadedModManager.RunningModsListForReading.Any(
                mod => mod.PackageId != null
                    && mod.PackageId.IndexOf(PackageIdMarker, StringComparison.OrdinalIgnoreCase) >= 0);

            if (Detected)
            {
                Log.Message("[YART] Semi Random Research detected.");
            }
        }
    }
}
