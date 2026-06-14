using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class YARTModSettings : ModSettings
    {
        // 바닐라 연구 버튼의 탭 윈도우를 YART로 교체할지 (off = 바닐라 연구창 복원)
        public bool replaceVanillaResearchTab = true;

        // 통합 벤치 뷰 — 켜면 벤치 탭을 단일 통합 그래프로 표시 (연구 창에서 토글)
        public bool unifiedBenchView = true;

        // 선행이 더 높은 시대인 연구의 색을 유효(배치) 시대로 통일할지. off = 본래 시대 색 유지.
        public bool unifyEraColorToEffective = false;

        // 좌측 패널 해금 콘텐츠를 2-column(아이콘+라벨)로 펼칠지. off = 아이콘만 나열. 패널 토글 버튼으로 변경.
        public bool unlockedContentExpanded = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref replaceVanillaResearchTab, "replaceVanillaResearchTab", true);
            Scribe_Values.Look(ref unifiedBenchView,          "unifiedBenchView",          true);
            Scribe_Values.Look(ref unifyEraColorToEffective,  "unifyEraColorToEffective",  false);
            Scribe_Values.Look(ref unlockedContentExpanded,   "unlockedContentExpanded",   false);
            // Constraints 반영은 YARTMod ctor(GetSettings 직후) + UI 변경 핸들러에서 한다.
        }

        public void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // ── 탭 교체 옵션 ──────────────────────────────────────────────────────
            bool replace = replaceVanillaResearchTab;
            listing.CheckboxLabeled("YART_Settings_ReplaceTab".Translate(), ref replace,
                "YART_Settings_ReplaceTabDesc".Translate());
            if (replace != replaceVanillaResearchTab)
            {
                replaceVanillaResearchTab = replace;
                VanillaTabReplacer.Apply(replace);
            }

            // ── 시대 색 통일 옵션 ─────────────────────────────────────────────────
            listing.CheckboxLabeled("YART_Settings_EraColor".Translate(), ref unifyEraColorToEffective,
                "YART_Settings_EraColorDesc".Translate());

            // ── Rebuild ──────────────────────────────────────────────────────────
            listing.Gap();
            if (listing.ButtonText("YART_Settings_Rebuild".Translate()))
            {
                GraphBuildPipeline.RebuildNonBlocking(); // 백그라운드 — 메인 스레드 프리즈 없음
            }
            listing.Label("<color=#888888>" + "YART_Settings_RebuildDesc".Translate() + "</color>");

            // ── 개발자: 레이아웃 그래프 export (DevMode 전용) — 오프라인 하니스용 ─────
            if (Prefs.DevMode)
            {
                listing.Gap();
                if (listing.ButtonText("YART_Settings_ExportGraph".Translate()))
                {
                    LayoutExport.WriteToDesktop();
                }
                listing.Label("<color=#888888>" + "YART_Settings_ExportGraphDesc".Translate() + "</color>");
            }

            listing.End();
        }
    }
}
