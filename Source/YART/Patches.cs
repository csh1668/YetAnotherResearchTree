using System;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART
{
    /// <summary>
    /// 바닐라 연구창에 YART로 전환하는 아이콘 버튼을 우하단에 덧그린다.
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_Research), nameof(MainTabWindow_Research.DoWindowContents))]
    public static class MainTabWindow_Research_DoWindowContents_Patch
    {
        public static void Postfix(Rect inRect)
        {
            Rect btn = new Rect(inRect.xMax - 34f, inRect.yMax - 34f, 30f, 30f);
            Widgets.DrawBoxSolid(btn, new Color(0f, 0f, 0f, 0.5f));
            GUIDrawingUtilities.DrawBorderLines(btn, new Color(0.55f, 0.6f, 0.4f, 0.9f), 1f);
            if (Mouse.IsOver(btn)) Widgets.DrawHighlight(btn);
            GUIDrawingUtilities.DrawIcon(btn.ContractedBy(5f), Assets.IconSwap, new Color(0.85f, 0.95f, 0.65f));
            TooltipHandler.TipRegion(btn, "YART_SwitchToYART".Translate());
            if (Widgets.ButtonInvisible(btn))
            {
                VanillaTabReplacer.SwitchTo(useYart: true);
            }
        }
    }

    // YART가 연구 탭을 대체할 때, 바닐라가 (MainTabWindow_Research)로 캐스트하는 지점 보호

    [HarmonyPatch(typeof(Alert_NeedResearchProject), "OnClick")]
    public static class Alert_NeedResearchProject_OnClick_Patch
    {
        public static Exception Finalizer(Exception __exception)
            => __exception is InvalidCastException ? null : __exception;
    }

    [HarmonyPatch(typeof(Alert_NeedAnomalyProject), "OnClick")]
    public static class Alert_NeedAnomalyProject_OnClick_Patch
    {
        public static Exception Finalizer(Exception __exception)
            => __exception is InvalidCastException ? null : __exception;
    }

    [HarmonyPatch(typeof(Dialog_EntityCodex), "LeftRect")]
    public static class Dialog_EntityCodex_LeftRect_Patch
    {
        public static Exception Finalizer(Exception __exception)
            => __exception is InvalidCastException ? null : __exception;
    }

    /// <summary>
    /// InfoCard의 연구 하이퍼링크는 해당 연구로 점프까지 시킨다.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_InfoCard.Hyperlink), "ActivateHyperlink")]
    public static class Dialog_InfoCard_Hyperlink_ActivateHyperlink_Patch
    {
        public static bool Prefix(ref Dialog_InfoCard.Hyperlink __instance)
        {
            if (__instance.researchProject != null
                && MainButtonDefOf.Research.TabWindow is MainTabWindow_YART yart)
            {
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Research);
                yart.RequestOpenAt(__instance.researchProject);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayDataLoader), nameof(PlayDataLoader.LoadAllPlayData))]
    public static class PlayDataLoader_LoadAllPlayData_Patch
    {
        private static bool firstSeen;
        public static void Postfix()
        {
            if (!firstSeen) { firstSeen = true; return; }
            GraphBuildPipeline.OnPlayDataReloaded();
        }
    }

    /// <summary>
    /// 연구 큐 자동 진행을 위한 바닐라 ResearchManager 훅.
    /// 설정이 켜져 있으면 완료 시 바닐라 모달 창(Dialog_NodeTree) 대신 전용 알림 편지를 보낸다.
    /// </summary>
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class ResearchManager_FinishProject_Patch
    {
        // 바닐라에서 창을 띄우려는 경우 doCompletionDialog만 가로채서 끄고, 억제 여부를 __state로 Postfix에 전달한다
        public static void Prefix(ResearchProjectDef proj, ref bool doCompletionDialog, out bool __state)
        {
            __state = doCompletionDialog && proj != null && YARTMod.Settings.completionLetterInsteadOfDialog;
            if (__state) doCompletionDialog = false;
        }

        public static void Postfix(ResearchProjectDef proj, bool __state)
        {
            ResearchQueueManager.Instance?.Notify_ProjectFinished(proj);

            if (__state && proj != null)
            {
                SendCompletionLetter(proj);
            }
        }

        private static void SendCompletionLetter(ResearchProjectDef proj)
        {
            var next = ResearchQueueManager.ChannelOf(proj)?.CurrentProject;
            if (next == proj) next = null;

            var body = new StringBuilder();
            body.Append(proj.LabelCap).Append("\n\n").Append(proj.description);

            int unlockCount = proj.GetUnlockedDefs().Count;
            if (unlockCount > 0)
            {
                body.Append("\n\n").Append("Unlocks".Translate()).Append(": ").Append(unlockCount);
            }
            if (next != null)
            {
                body.Append("\n\n").Append("YART_NextInQueue".Translate(next.LabelCap));
            }

            var letterDef = next != null
                ? YARTLetterDefOf.YART_ResearchCompleted          // 다음 연구 있음 → PositiveEvent
                : YARTLetterDefOf.YART_ResearchCompletedNeutral;  // 없음 → NeutralEvent
            var letter = (ResearchCompletedLetter)LetterMaker.MakeLetter(letterDef);
            letter.Label = "ResearchFinished".Translate(proj.LabelCap);
            letter.Text = body.ToString();
            letter.project = proj;
            letter.nextProject = next;
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.StopProject))]
    public static class ResearchManager_StopProject_Patch
    {
        public static void Postfix(ResearchProjectDef proj)
        {
            ResearchQueueManager.Instance?.Notify_ProjectStopped(proj);
        }
    }

    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.SetCurrentProject))]
    public static class ResearchManager_SetCurrentProject_Patch
    {
        public static void Postfix(ResearchProjectDef proj)
        {
            ResearchQueueManager.Instance?.Notify_CurrentProjectChanged(proj);
        }
    }
}
