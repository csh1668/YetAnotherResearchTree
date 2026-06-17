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
        private readonly List<Def> unlocks;

        private Vector2 scroll;
        private float contentHeight = 99999f;

        public override Vector2 InitialSize => new Vector2(540f, 620f);

        public Dialog_ResearchCompleted(ResearchCompletedLetter letter)
        {
            this.letter = letter;
            project = letter.project;
            nextProject = letter.nextProject;
            unlocks = ResearchCompletedLetter.GetUnlockedDefs(project);

            draggable = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
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
                Widgets.Label(new Rect(0f, cy, viewW, 26f), $"{"Unlocks".Translate()} ({unlocks.Count})");
            }
            if (unlocks.Count > 0)
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

            if (unlocks.Count == 0)
            {
                using (Temporary.Color(Constraints.MutedText))
                    Widgets.Label(new Rect(8f, cy, viewW, 24f), "YART_None".Translate());
                return cy + 28f;
            }

            return YARTMod.Settings.unlockedContentExpanded
                ? DrawUnlocksList(viewW, cy)
                : DrawUnlocksIcons(viewW, cy);
        }

        private float DrawUnlocksIcons(float viewW, float cy)
        {
            const float icon = 40f, gap = 6f;
            float x = 0f, rowY = cy;
            foreach (var def in unlocks)
            {
                if (x + icon > viewW) { x = 0f; rowY += icon + gap; }
                DrawUnlockCell(new Rect(x, rowY, icon, icon), def);
                x += icon + gap;
            }
            return rowY + icon + 6f;
        }

        private float DrawUnlocksList(float viewW, float cy)
        {
            const float rowH = 30f, rowGap = 2f, colGap = 8f, cellIcon = 24f;
            float colW = (viewW - colGap) / 2f;
            for (int i = 0; i < unlocks.Count; i++)
            {
                var def = unlocks[i];
                int col = i % 2;
                float cellX = col * (colW + colGap);
                Rect cellRect = new Rect(cellX, cy, colW, rowH);

                Rect iconRect = new Rect(cellX, cy + (rowH - cellIcon) / 2f, cellIcon, cellIcon);
                Widgets.DefIcon(iconRect, def, null, 1f, null, drawPlaceholder: true);

                Rect labelRect = new Rect(iconRect.xMax + 6f, cy, colW - cellIcon - 6f, rowH);
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                    Widgets.Label(labelRect, ((string)def.LabelCap).Truncate(labelRect.width));

                if (Mouse.IsOver(cellRect))
                {
                    Widgets.DrawHighlight(cellRect);
                    TooltipHandler.TipRegion(cellRect, def.LabelCap);
                }
                if (Widgets.ButtonInvisible(cellRect))
                {
                    SoundDefOf.Click.PlayOneShotOnCamera();
                    Find.WindowStack.Add(new Dialog_InfoCard(def));
                }
                if (col == 1) cy += rowH + rowGap;
            }
            if (unlocks.Count % 2 == 1) cy += rowH + rowGap;
            return cy + 6f;
        }

        // 단일 해금 셀
        private static void DrawUnlockCell(Rect r, Def def)
        {
            Widgets.DefIcon(r, def, null, 1f, null, drawPlaceholder: true);
            if (Mouse.IsOver(r))
            {
                Widgets.DrawHighlight(r);
                TooltipHandler.TipRegion(r, def.LabelCap);
            }
            if (Widgets.ButtonInvisible(r))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                Find.WindowStack.Add(new Dialog_InfoCard(def));
            }
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
            TooltipHandler.TipRegion(row, "YART_ShowInfoCard".Translate());
            if (Widgets.ButtonInvisible(row))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                JumpToNext();
            }
        }

        private void JumpToNext()
        {
            if (MainButtonDefOf.Research.TabWindow is MainTabWindow_YART yart)
            {
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Research);
                yart.RequestOpenAt(nextProject);
            }
            else
            {
                Find.WindowStack.Add(new Dialog_InfoCard(nextProject));
            }
            Dismiss();
        }

        private void Dismiss()
        {
            Find.LetterStack.RemoveLetter(letter);
            Close();
        }
    }
}
