using RimWorld;
using UnityEngine;

namespace YART.Data
{
    /// <summary>
    /// 현재 적용되는 색상
    /// </summary>
    public static class ActiveColors
    {
        // 시대 색 인덱스 순서 — eraColors/프리셋 era 배열, EraIndex와 동일 순서.
        public static readonly TechLevel[] EraOrder =
        {
            TechLevel.Animal, TechLevel.Neolithic, TechLevel.Medieval, TechLevel.Industrial,
            TechLevel.Spacer, TechLevel.Ultra, TechLevel.Archotech,
        };

        private static readonly Color[] era = new Color[ColorPalette.EraCount];

        public static Color PrereqMet;
        public static Color PrereqUnmet;

        static ActiveColors()
        {
            Apply(ColorPalettes.Get(ColorPalettes.DefaultId));
        }

        public static void Apply(ColorPalette p)
        {
            int n = Mathf.Min(ColorPalette.EraCount, p.era.Length);
            for (int i = 0; i < n; i++) era[i] = p.era[i];
            PrereqMet = p.prereqMet;
            PrereqUnmet = p.prereqUnmet;
        }

        public static int EraIndex(TechLevel tl)
        {
            switch (tl)
            {
                case TechLevel.Animal: return 0;
                case TechLevel.Neolithic: return 1;
                case TechLevel.Medieval: return 2;
                case TechLevel.Industrial: return 3;
                case TechLevel.Spacer: return 4;
                case TechLevel.Ultra: return 5;
                case TechLevel.Archotech: return 6;
                case TechLevel.Undefined:
                default: return -1;
            }
        }

        public static Color Era(TechLevel tl)
        {
            int i = EraIndex(tl);
            return i < 0 ? Color.gray : era[i];
        }
    }
}
