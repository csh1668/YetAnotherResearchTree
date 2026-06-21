using System.Collections.Generic;
using RimWorld;
using Verse;

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
    }
}
