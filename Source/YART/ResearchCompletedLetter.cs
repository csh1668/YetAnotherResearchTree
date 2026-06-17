using System.Collections.Generic;
using RimWorld;
using Verse;
using YART.Data;

namespace YART
{
    [DefOf]
    public static class YARTLetterDefOf
    {
        public static LetterDef YART_ResearchCompleted;
        public static LetterDef YART_ResearchCompletedNeutral;

        static YARTLetterDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(YARTLetterDefOf));
    }

    public class ResearchCompletedLetter : ChoiceLetter
    {
        public ResearchProjectDef project;
        public ResearchProjectDef nextProject;

        public override IEnumerable<DiaOption> Choices
        {
            get { yield return Option_Close; }
        }

        public override void OpenLetter()
        {
            if (project == null)
            {
                // 정보 유실
                base.OpenLetter();
                return;
            }
            Find.WindowStack.Add(new Dialog_ResearchCompleted(this));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref project, "project");
            Scribe_Defs.Look(ref nextProject, "nextProject");
        }

        public static List<Def> GetUnlockedDefs(ResearchProjectDef proj)
        {
            var graph = ResearchGraph.Instance;
            if (proj != null && graph != null && graph.Initialized && graph.AllNodes.TryGetValue(proj, out var node))
            {
                return node.UnlockedDefs;
            }
            return new List<Def>();
        }
    }
}
