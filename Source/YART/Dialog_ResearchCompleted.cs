using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class Dialog_ResearchCompleted : Window
    {
        private readonly ResearchCompletedLetter letter;
        private readonly ResearchProjectDef project;
        private readonly ResearchProjectDef nextProject;
        private readonly List<UnlockedDefsUtility.UnlockedGroup> unlockGroups;
        private readonly int unlockCount;

        private Vector2 scroll;
        private float contentHeight = 99999f;

        public override Vector2 InitialSize => new Vector2(540f, 620f);

        public Dialog_ResearchCompleted(ResearchCompletedLetter letter)
        {
            this.letter = letter;
            project = letter.project;
            nextProject = letter.nextProject;
            unlockGroups = project.GetUnlockedGroups();
            foreach (var g in unlockGroups) unlockCount += g.Defs.Count;

            draggable = true;
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            soundAppear = SoundDefOf.CommsWindow_Open;
            soundClose = SoundDefOf.TabClose;
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (Temporary.Font(GameFont.Medium))
            using (Temporary.Color(Constraints.SectionUnlocks))
            {
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ResearchFinished".Translate(project.LabelCap));
            }
            float top = 40f;
            Widgets.DrawLineHorizontal(0f, top, inRect.width);
            top += 10f;

            // 하단 고정 영역
            const float buttonH = 38f;
            float closeY = inRect.height - buttonH;
            float nextBlockH = 30f + (nextProject == null ? 26f : 36f);
            float nextY = closeY - 12f - nextBlockH;

            // 중앙 스크롤
            Rect midOut = new Rect(0f, top, inRect.width, Mathf.Max(60f, nextY - 10f - top));
            float viewW = midOut.width - 20f;
            Widgets.BeginScrollView(midOut, ref scroll, new Rect(0f, 0f, viewW, contentHeight));
            float cy = 0f;
            cy = DrawDescription(viewW, cy);
            cy = DrawUnlocks(viewW, cy);
            contentHeight = cy;
            Widgets.EndScrollView();

            Widgets.DrawLineHorizontal(0f, nextY - 10f, inRect.width);
            DrawNext(new Rect(0f, nextY, inRect.width, nextBlockH));

            // 닫기
            if (Widgets.ButtonText(new Rect(inRect.width - 140f, closeY, 140f, buttonH - 2f), "Close".Translate()))
            {
                Dismiss();
            }
        }

        private float DrawDescription(float viewW, float cy)
        {
            if (project.description.NullOrEmpty()) return cy;
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(Constraints.BodyText))
            {
                float h = Text.CalcHeight(project.description, viewW);
                Widgets.Label(new Rect(0f, cy, viewW, h), project.description);
                return cy + h + 14f;
            }
        }

        private float DrawUnlocks(float viewW, float cy)
        {
            // 헤더 + 뷰 전환 토글
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(Constraints.SectionUnlocks))
            {
                Widgets.Label(new Rect(0f, cy, viewW, 26f), $"{"Unlocks".Translate()} ({unlockCount})");
            }
            if (unlockCount > 0)
            {
                bool expanded = YARTMod.Settings.unlockedContentExpanded;
                Rect toggleRect = new Rect(viewW - 18f, cy + 4f, 18f, 18f);
                if (Mouse.IsOver(toggleRect)) Widgets.DrawHighlight(toggleRect);
                GUIDrawingUtilities.DrawIcon(toggleRect, Assets.IconSwap,
                    expanded ? Constraints.SectionUnlocks : Constraints.ToggleInactive);
                TooltipHandler.TipRegion(toggleRect, "YART_ToggleUnlockedView".Translate());
                if (Widgets.ButtonInvisible(toggleRect))
                {
                    SoundDefOf.Click.PlayOneShotOnCamera();
                    YARTMod.Settings.unlockedContentExpanded = !expanded;
                    YARTMod.Settings.Write();
                }
            }
            cy += 30f;

            if (unlockCount == 0)
            {
                using (Temporary.Color(Constraints.MutedText))
                    Widgets.Label(new Rect(8f, cy, viewW, 24f), "YART_None".Translate());
                return cy + 28f;
            }

            cy = UnlockedDefsUtility.Draw(0f, cy, viewW, unlockGroups,
                YARTMod.Settings.unlockedContentExpanded, 40f, JumpToResearch);
            return cy + 6f;
        }

        private void DrawNext(Rect rect)
        {
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(Constraints.SectionFollowups))
            {
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 26f), "YART_NextResearchHeader".Translate());
            }
            float yy = rect.y + 30f;

            if (nextProject == null)
            {
                using (Temporary.Color(Constraints.MutedText))
                    Widgets.Label(new Rect(rect.x + 8f, yy, rect.width, 24f), "YART_None".Translate());
                return;
            }

            Rect row = new Rect(rect.x, yy, rect.width, 32f);
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
                Widgets.Label(new Rect(row.x + 8f, row.y, row.width - 16f, row.height), nextProject.LabelCap);
            if (Widgets.ButtonInvisible(row))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                JumpToResearch(nextProject);
            }
        }

        private void JumpToResearch(ResearchProjectDef def)
        {
            if (def == null) return;
            if (MainButtonDefOf.Research.TabWindow is MainTabWindow_YART yart)
            {
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Research);
                yart.RequestOpenAt(def);
            }
            else
            {
                Find.WindowStack.Add(new Dialog_InfoCard(def));
            }
            // Dismiss();
        }

        private void Dismiss()
        {
            Find.LetterStack.RemoveLetter(letter);
            Close();
        }
    }
}
