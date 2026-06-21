using HarmonyLib;
using RimWorld;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace YART
{
    public class YARTMod : Mod
    {
        public static YARTModSettings Settings { get; private set; }

        public YARTMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<YARTModSettings>();
            Settings.ApplyColors();

            var harmony = new Harmony("seohyeon.yart");
            harmony.PatchAll();

            Log.Message("[YART] Patched!");
        }

        public override string SettingsCategory() => "Yet Another Research Tree";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            Settings.DoSettingsWindowContents(inRect);
        }
    }

    /// <summary>
    /// 바닐라 연구 버튼(MainButtonDefOf.Research)의 탭 윈도우를 YART로 교체한다.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class VanillaTabReplacer
    {
        private static readonly FieldInfo TabWindowIntField = AccessTools.Field(typeof(MainButtonDef), "tabWindowInt");

        static VanillaTabReplacer()
        {
            Apply(YARTMod.Settings.replaceVanillaResearchTab);
        }

        public static void Apply(bool replace)
        {
            var researchButton = MainButtonDefOf.Research;
            researchButton.tabWindowClass = replace ? typeof(MainTabWindow_YART) : typeof(MainTabWindow_Research);
            TabWindowIntField.SetValue(researchButton, null);

            if (Current.ProgramState == ProgramState.Playing)
            {
                Find.MainTabsRoot?.EscapeCurrentTab(playSound: false);
            }
        }

        public static void SwitchTo(bool useYart)
        {
            // 설정 영속화 (다음에 연구 버튼을 눌렀을 때도 같은 창이 열리도록)
            if (YARTMod.Settings.replaceVanillaResearchTab != useYart)
            {
                YARTMod.Settings.replaceVanillaResearchTab = useYart;
                YARTMod.Settings.Write();
            }

            var researchButton = MainButtonDefOf.Research;

            // 두 연구창 모두 def==Research라 OpenTab이 동일 → SetCurrentTab/ToggleTab으론 같은 프레임에
            // 재오픈이 안 된다(닫히기만 함). WindowStack을 직접 조작해 즉시 swap한다.
            var open = Find.WindowStack?.WindowOfType<MainTabWindow>();
            bool wasOpen = open != null && open.def == researchButton;
            if (wasOpen) Find.WindowStack.TryRemove(open, doCloseSound: false);

            researchButton.tabWindowClass = useYart ? typeof(MainTabWindow_YART) : typeof(MainTabWindow_Research);
            TabWindowIntField.SetValue(researchButton, null);

            // 직전에 열려 있었으면 새 클래스 창을 즉시 연다
            if (wasOpen)
            {
                Find.WindowStack.Add(researchButton.TabWindow);
                SoundDefOf.TabOpen.PlayOneShotOnCamera();
            }
        }
    }
}
