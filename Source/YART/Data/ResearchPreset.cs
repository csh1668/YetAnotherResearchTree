using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace YART.Data
{
    /// <summary>
    /// 사용자 정의 탭 그룹(프리셋).
    /// 여러 바닐라 연구 탭(ResearchTabDef)을 하나의 병합 그래프로 묶어 드롭다운에 별도 항목으로 노출한다.
    /// </summary>
    public class ResearchPreset : IExposable
    {
        public string Id;
        public string Name;
        public List<string> TabDefNames = new List<string>();

        public bool IncludeAllBenchTabs;

        public ResearchPreset() { }

        public static ResearchPreset AllTabs => new ResearchPreset
        {
            Id = GraphKey.AllTabsId,
            Name = "YART_AllTabs".Translate(),
            TabDefNames = new List<string>(),
            IncludeAllBenchTabs = true,
        };

        public ResearchPreset(string name, IEnumerable<string> tabDefNames)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name;
            TabDefNames = new List<string>(tabDefNames);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id");
            Scribe_Values.Look(ref Name, "name");
            Scribe_Collections.Look(ref TabDefNames, "tabDefNames", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && TabDefNames == null)
                TabDefNames = new List<string>();
        }

        /// <summary>이 프리셋에 포함된, 현재 로드된 ResearchTabDef 목록 (소실 탭은 제외).</summary>
        public List<ResearchTabDef> ResolveTabs()
        {
            var result = new List<ResearchTabDef>();
            if (TabDefNames == null) return result;
            foreach (var defName in TabDefNames)
            {
                var tab = DefDatabase<ResearchTabDef>.GetNamedSilentFail(defName);
                if (tab != null) result.Add(tab);
            }
            return result;
        }
    }
}
