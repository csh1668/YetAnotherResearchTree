using RimWorld;
using UnityEngine;
using Verse;

namespace YART.Data
{
    public sealed class ColorPalette
    {
        public const int EraCount = 7;

        public Color[] era = new Color[EraCount];
        public Color prereqMet;
        public Color prereqUnmet;

        public ColorPalette Clone()
        {
            var c = new ColorPalette
            {
                era = (Color[])era.Clone(),
                prereqMet = prereqMet,
                prereqUnmet = prereqUnmet,
            };
            return c;
        }
    }

    public static class ColorPalettes
    {
        public const string DefaultId = "default";
        public const string HighContrastId = "highContrast";
        public const string LumOrderedId = "lumOrdered";
        public const string CustomId = "custom";

        // UI 프리셋 버튼 순서
        public static readonly string[] SelectableIds = { DefaultId, HighContrastId, LumOrderedId };

        private static Color Hex(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        private static readonly ColorPalette Default = new ColorPalette
        {
            era = new[]
            {
                new Color(0.85f, 0.36f, 0.30f),
                new Color(0.85f, 0.36f, 0.30f),
                new Color(0.784f, 0.608f, 0.314f),
                new Color(0.3f, 0.7f, 0.4f),
                new Color(0.0f, 0.898f, 0.898f),
                new Color(0.70f, 0.44f, 1.0f),
                new Color(1.0f, 0.843f, 0.0f),
            },
            prereqMet = Color.green,
            prereqUnmet = ColorLibrary.RedReadable,
        };

        // 고대비 (Okabe-Ito)
        private static readonly ColorPalette HighContrast = new ColorPalette
        {
            era = new[]
            {
                Hex(0xD5, 0x5E, 0x00),
                Hex(0xD5, 0x5E, 0x00),
                Hex(0xE6, 0x9F, 0x00),
                Hex(0x00, 0x9E, 0x73),
                Hex(0x56, 0xB4, 0xE9),
                Hex(0xCC, 0x79, 0xA7),
                Hex(0xF0, 0xE4, 0x42),
            },
            prereqMet = Hex(0x56, 0xB4, 0xE9),
            prereqUnmet = Hex(0xD5, 0x5E, 0x00),
        };

        // 휘도 정렬
        private static readonly ColorPalette LumOrdered = new ColorPalette
        {
            era = new[]
            {
                Hex(0x00, 0x6C, 0xE0),
                Hex(0x00, 0x6C, 0xE0),
                Hex(0x71, 0x8F, 0xD8),
                Hex(0xA3, 0xA9, 0xBC),
                Hex(0xC9, 0xC1, 0x9D),
                Hex(0xE6, 0xD6, 0x72),
                Hex(0xFF, 0xE9, 0x38),
            },
            prereqMet = Hex(0xFF, 0xE9, 0x38),
            prereqUnmet = Hex(0x00, 0x77, 0xF7),
        };

        public static ColorPalette Get(string id)
        {
            switch (id)
            {
                case HighContrastId: return HighContrast.Clone();
                case LumOrderedId: return LumOrdered.Clone();
                default: return Default.Clone();
            }
        }
    }
}
