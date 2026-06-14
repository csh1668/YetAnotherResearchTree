using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace YART.Compat
{
    /// <summary>
    /// World Tech Level 모드 호환성 어댑터
    ///
    /// 1. 월드 만들 때 월드 테크레벨 설정 가능
    /// 2. 그 월드 테크레벨 이하의 연구만 표시
    /// 3. 가시성 필터는 "거의" 정적임 - 월드 생성, 로드 등 할때 1회 계산
    /// 4. "거의"라고 한 이유는 WTL 내 특정 옵션을 활성화할 경우 월드 테크레벨 이상의 연구를 얻을 수 있음 그런 경우에는 재빌드 필요
    /// </summary>
    public static class WorldTechLevelCompat
    {
        private const int SigInactive = 0;
        private const int SigUnfiltered = 1;        // 게임 밖

        private static bool _probed;
        private static Func<ResearchProjectDef, bool> _shouldShow;

        private static int _builtSignature = SigInactive;

        private static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (ModLister.GetActiveModWithIdentifier("m00nl1ght.WorldTechLevel", ignorePostfix: true) == null)
                    return;

                var utilType = AccessTools.TypeByName("WorldTechLevel.ResearchUtility");
                var visMethod = utilType == null ? null
                    : AccessTools.Method(utilType, "ShouldProjectBeVisible", new[] { typeof(ResearchProjectDef) });

                if (visMethod == null)
                {
                    Log.Warning("[YART] World Tech Level detected, but binding ShouldProjectBeVisible failed (version mismatch?). Research visibility filter disabled.");
                    return;
                }

                _shouldShow = (Func<ResearchProjectDef, bool>)Delegate.CreateDelegate(
                    typeof(Func<ResearchProjectDef, bool>), visMethod);

                if (Prefs.DevMode) Log.Message("[YART] World Tech Level detected — research visibility filter enabled.");
            }
            catch (Exception e)
            {
                _shouldShow = null;
                Log.Warning("[YART] World Tech Level compatibility init failed — visibility filter disabled: " + e);
            }
        }

        /// <summary>
        /// 현재 가시 집합 시그니처 — 빌드/오픈 시점 비교용. 가시 def들의 shortHash를 누적해
        /// 필터 레벨 변경뿐 아니라 Filter_Research 토글까지(가시 집합이 바뀌므로) 반영한다.
        /// </summary>
        private static int CurrentSignature
        {
            get
            {
                EnsureProbed();
                if (_shouldShow == null) return SigInactive;
                if (Current.ProgramState != ProgramState.Playing) return SigUnfiltered;
                try
                {
                    int hash = 17;
                    var defs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                    for (int i = 0; i < defs.Count; i++)
                    {
                        if (_shouldShow(defs[i])) hash = hash * 31 + defs[i].shortHash;
                    }
                    return hash;
                }
                catch { return SigUnfiltered; }
            }
        }

        /// <summary>이 연구를 트리에 표시할지. WTL 미설치/게임 밖/판정 실패 시 항상 true.</summary>
        public static bool ShouldShow(ResearchProjectDef def)
        {
            EnsureProbed();
            if (_shouldShow == null) return true;
            if (Current.ProgramState != ProgramState.Playing) return true;
            try { return _shouldShow(def); }
            catch { return true; }
        }

        /// <summary>그래프 빌드(CreateNodes) 직후 호출 — 빌드에 사용한 가시 집합 시그니처를 기록.</summary>
        public static void MarkBuilt()
        {
            _builtSignature = CurrentSignature;
        }

        /// <summary>현재 가시 집합이 마지막 빌드와 달라 리빌드가 필요한가 (WTL 활성 시에만 true 가능).</summary>
        public static bool NeedsRebuild
        {
            get
            {
                EnsureProbed();
                if (_shouldShow == null) return false;
                return CurrentSignature != _builtSignature;
            }
        }
    }
}
