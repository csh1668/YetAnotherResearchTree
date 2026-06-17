using System.Collections.Generic;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class YARTModSettings : ModSettings
    {
        // 사용자 정의 탭 그룹(프리셋) — 전역 영속. 드롭다운에 별도 병합 그래프 항목으로 노출.
        public List<ResearchPreset> tabPresets = new List<ResearchPreset>();

        // 즐겨찾기한 드롭다운 항목 키 (탭 = "t:"+defName, 프리셋 = "p:"+id). 전역 영속.
        public List<string> favorites = new List<string>();

        // 바닐라 연구 버튼의 탭 윈도우를 YART로 교체할지 (off = 바닐라 연구창 복원)
        public bool replaceVanillaResearchTab = true;

        // 통합 벤치 뷰 — 켜면 벤치 탭을 단일 통합 그래프로 표시 (연구 창에서 토글)
        public bool unifiedBenchView = true;

        // 선행이 더 높은 시대인 연구의 색을 유효(배치) 시대로 통일할지. off = 본래 시대 색 유지.
        public bool unifyEraColorToEffective = false;

        // 좌측 패널 해금 콘텐츠를 2-column(아이콘+라벨)로 펼칠지. off = 아이콘만 나열. 패널 토글 버튼으로 변경.
        public bool unlockedContentExpanded = false;

        // 호버 시 비관련 노드를 어둡게(디밍)할지. off(기본) = 디밍 없이 관련 경로만 하이라이트.
        public bool focusHighlightDimming = false;

        // 연구 완료 시 바닐라 모달 창 대신 알림 편지(Letter)를 보낼지. on(기본) = 편지 / off = 바닐라 창.
        public bool completionLetterInsteadOfDialog = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref replaceVanillaResearchTab, "replaceVanillaResearchTab", true);
            Scribe_Values.Look(ref unifiedBenchView,          "unifiedBenchView",          true);
            Scribe_Values.Look(ref unifyEraColorToEffective,  "unifyEraColorToEffective",  false);
            Scribe_Values.Look(ref unlockedContentExpanded,   "unlockedContentExpanded",   false);
            Scribe_Values.Look(ref focusHighlightDimming,     "focusHighlightDimming",     false);
            Scribe_Values.Look(ref completionLetterInsteadOfDialog, "completionLetterInsteadOfDialog", true);
            Scribe_Collections.Look(ref tabPresets, "tabPresets", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && tabPresets == null)
                tabPresets = new List<ResearchPreset>();
            Scribe_Collections.Look(ref favorites, "favorites", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && favorites == null)
                favorites = new List<string>();
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

            // ── 호버 디밍 옵션 ────────────────────────────────────────────────────
            listing.CheckboxLabeled("YART_Settings_FocusDimming".Translate(), ref focusHighlightDimming,
                "YART_Settings_FocusDimmingDesc".Translate());

            // ── 연구 완료 편지 옵션 ───────────────────────────────────────────────
            listing.CheckboxLabeled("YART_Settings_CompletionLetter".Translate(), ref completionLetterInsteadOfDialog,
                "YART_Settings_CompletionLetterDesc".Translate());

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
