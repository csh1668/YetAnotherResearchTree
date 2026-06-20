using UnityEngine;
using Verse;

namespace YART
{
    /// <summary>
    /// tools/generate_assets.py로부터 생성됨
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Assets
    {
        public static readonly Texture2D GlowRadial = ContentFinder<Texture2D>.Get("YART/GlowRadial");

        public static readonly Texture2D NodePanel = ContentFinder<Texture2D>.Get("YART/NodePanel");

        public static readonly Texture2D NodePanelBorder = ContentFinder<Texture2D>.Get("YART/NodePanelBorder");

        public static readonly Texture2D NoiseTile = ContentFinder<Texture2D>.Get("YART/NoiseTile");

        public static readonly Texture2D Vignette = ContentFinder<Texture2D>.Get("YART/Vignette");

        public static readonly Texture2D EdgeLine = ContentFinder<Texture2D>.Get("YART/EdgeLine");

        public static readonly Texture2D IconLock = ContentFinder<Texture2D>.Get("YART/IconLock");
        public static readonly Texture2D IconCheck = ContentFinder<Texture2D>.Get("YART/IconCheck");
        public static readonly Texture2D IconPlay = ContentFinder<Texture2D>.Get("YART/IconPlay");
        public static readonly Texture2D IconQueue = ContentFinder<Texture2D>.Get("YART/IconQueue");
        public static readonly Texture2D IconSwap = ContentFinder<Texture2D>.Get("YART/IconSwap");
        public static readonly Texture2D IconSettings = ContentFinder<Texture2D>.Get("YART/IconSettings");
        public static readonly Texture2D IconStar = ContentFinder<Texture2D>.Get("YART/IconStar");
        public static readonly Texture2D IconStarHollow = ContentFinder<Texture2D>.Get("YART/IconStarHollow");

        public static readonly Texture2D IconTechprint = ContentFinder<Texture2D>.Get("UI/Icons/Research/Techprint");

        public const float PanelCornerUV = 12f / 64f;

        static Assets()
        {
            if (NoiseTile != null) NoiseTile.wrapMode = TextureWrapMode.Repeat;
        }
    }
}
