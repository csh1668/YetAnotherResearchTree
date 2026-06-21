using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;

namespace YART.Utils
{
    public static class UnlockedDefsUtility
    {
        public class UnlockedGroup
        {
            public List<ResearchProjectDef> AlsoNeeds;
            public List<Def> Defs;
        }

        public static List<Def> GetUnlockedDefs(this ResearchProjectDef proj)
        {
            var node = proj.HomeNode();
            return node != null ? node.UnlockedDefs : new List<Def>();
        }

        public static List<UnlockedGroup> GetUnlockedGroups(this ResearchProjectDef proj)
        {
            var node = proj.HomeNode();
            return node != null ? node.UnlockedDefsGrouped : new List<UnlockedGroup>();
        }

        public static ResearchNode HomeNode(this ResearchProjectDef proj)
        {
            var graph = ResearchGraph.Instance;
            if (proj != null && graph != null && graph.Initialized && graph.AllNodes.TryGetValue(proj, out var node))
                return node;
            return null;
        }

        public static List<UnlockedGroup> GroupUnlockedDefs(IEnumerable<Def> unlockedDefs, ResearchProjectDef self)
        {
            var groups = new List<UnlockedGroup>();
            if (unlockedDefs == null) return groups;

            foreach (var def in unlockedDefs)
            {
                var also = ResearchPrereqsOf(def)
                    .Where(r => r != null && r != self)
                    .Distinct()
                    .OrderBy(r => r.defName)
                    .ToList();

                var g = groups.FirstOrDefault(x => SameResearchSet(x.AlsoNeeds, also));
                if (g == null)
                {
                    g = new UnlockedGroup { AlsoNeeds = also, Defs = new List<Def>() };
                    groups.Add(g);
                }
                g.Defs.Add(def);
            }
            return groups.OrderBy(g => g.AlsoNeeds.Count).ToList();
        }

        public static IEnumerable<ResearchProjectDef> ResearchPrereqsOf(Def def)
        {
            switch (def)
            {
                case ThingDef t:
                    if (t.researchPrerequisites != null)
                        foreach (var r in t.researchPrerequisites) yield return r;
                    if (t.plant?.sowResearchPrerequisites != null)
                        foreach (var r in t.plant.sowResearchPrerequisites) yield return r;
                    break;
                case TerrainDef te:
                    if (te.researchPrerequisites != null)
                        foreach (var r in te.researchPrerequisites) yield return r;
                    break;
                case RecipeDef rc:
                    if (rc.researchPrerequisite != null) yield return rc.researchPrerequisite;
                    if (rc.researchPrerequisites != null)
                        foreach (var r in rc.researchPrerequisites) yield return r;
                    break;
            }
        }

        private static bool SameResearchSet(List<ResearchProjectDef> a, List<ResearchProjectDef> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static float Draw(float x, float cy, float width,
            List<UnlockedGroup> groups, bool expanded, float iconSize,
            Action<ResearchProjectDef> onResearchClick = null)
        {
            if (groups == null) return cy;
            foreach (var g in groups)
            {
                if (g.Defs.Count == 0) continue;
                if (g.AlsoNeeds.Count > 0)
                    cy = DrawGroupHeader(x, cy, width, g.AlsoNeeds, onResearchClick);
                cy = expanded
                    ? DrawList(x, cy, width, g.Defs)
                    : DrawIcons(x, cy, width, g.Defs, iconSize);
            }
            return cy;
        }

        private static float DrawGroupHeader(float x, float cy, float width,
            List<ResearchProjectDef> alsoNeeds, Action<ResearchProjectDef> onResearchClick)
        {
            const float lineH = 22f;
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            {
                float startX = x + 4f;
                float maxX = x + width;
                float curX = startX;
                float lineY = cy;

                void DrawText(string s, Color col)
                {
                    float w = Text.CalcSize(s).x;
                    using (Temporary.Color(col))
                        Widgets.Label(new Rect(curX, lineY, w, lineH), s);
                    curX += w;
                }

                DrawText((string)"UnlockedWith".Translate() + " ", Constraints.MutedText);

                for (int i = 0; i < alsoNeeds.Count; i++)
                {
                    var research = alsoNeeds[i];
                    string text = research.LabelCap;
                    float tw = Text.CalcSize(text).x;

                    if (curX + tw > maxX && curX > startX)
                    {
                        curX = startX;
                        lineY += lineH;
                    }

                    Rect tokenRect = new Rect(curX, lineY, tw, lineH);
                    bool over = onResearchClick != null && Mouse.IsOver(tokenRect);
                    if (over) Widgets.DrawHighlight(tokenRect);

                    using (Temporary.Color(research.IsFinished ? ActiveColors.PrereqMet : ActiveColors.PrereqUnmet))
                        Widgets.Label(tokenRect, text);

                    if (onResearchClick != null)
                    {
                        if (Widgets.ButtonInvisible(tokenRect))
                        {
                            SoundDefOf.Click.PlayOneShotOnCamera();
                            onResearchClick(research);
                        }
                    }
                    curX += tw;

                    if (i < alsoNeeds.Count - 1)
                        DrawText(", ", Constraints.MutedText);
                }
                return lineY + lineH;
            }
        }

        private static float DrawList(float x, float cy, float width, List<Def> defs)
        {
            const float rowH = 30f, rowGap = 2f, colGap = 8f, cellIcon = 24f;
            float colW = (width - colGap) / 2f;
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                int col = i % 2;
                float cellX = x + col * (colW + colGap);
                Rect cellRect = new Rect(cellX, cy, colW, rowH);

                Rect iconRect = new Rect(cellX, cy + (rowH - cellIcon) / 2f, cellIcon, cellIcon);
                Widgets.DefIcon(iconRect, def, null, 1f, null, drawPlaceholder: true);

                Rect labelRect = new Rect(iconRect.xMax + 6f, cy, colW - cellIcon - 6f, rowH);
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                    Widgets.Label(labelRect, ((string)def.LabelCap).Truncate(labelRect.width));

                HandleCell(cellRect, def);
                if (col == 1) cy += rowH + rowGap;
            }
            if (defs.Count % 2 == 1) cy += rowH + rowGap;
            return cy;
        }

        private static float DrawIcons(float x, float cy, float width, List<Def> defs, float iconSize)
        {
            const float gap = 6f;
            float curX = x, rowY = cy;
            foreach (var def in defs)
            {
                if (curX + iconSize > x + width) { curX = x; rowY += iconSize + gap; }
                HandleCell(new Rect(curX, rowY, iconSize, iconSize), def, drawIcon: true);
                curX += iconSize + gap;
            }
            return rowY + iconSize + gap;
        }

        private static void HandleCell(Rect r, Def def, bool drawIcon = false)
        {
            if (drawIcon) Widgets.DefIcon(r, def, null, 1f, null, drawPlaceholder: true);
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
    }
}
