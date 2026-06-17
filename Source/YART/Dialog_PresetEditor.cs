using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;

namespace YART
{
    /// <summary>
    /// 사용자 탭 그룹(프리셋) 생성/편집 창.
    /// </summary>
    public class Dialog_PresetEditor : Window
    {
        private const string NameControl = "YARTPresetName";

        private readonly MainTabWindow_YART owner;
        private readonly ResearchPreset editing; // null = 신규 생성
        private readonly GraphKey restoreKey; // 열 때의 뷰 — 닫을 때 복원

        private string presetName;
        private readonly HashSet<string> selected = new HashSet<string>();
        private Vector2 scroll;
        private bool nameFocusRequested;

        public Dialog_PresetEditor(MainTabWindow_YART owner) : this(owner, null) { }

        public Dialog_PresetEditor(MainTabWindow_YART owner, ResearchPreset editing)
        {
            this.owner = owner;
            this.editing = editing;
            restoreKey = owner.CurrentGraphKey;

            presetName = editing?.Name ?? "";
            if (editing?.TabDefNames != null)
            {
                foreach (var n in editing.TabDefNames) selected.Add(n);
            }

            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            preventCameraMotion = false;
            draggable = true;
            doCloseX = true;
            onlyOneOfTypeAllowed = true;
            layer = WindowLayer.Dialog;
            soundAppear = SoundDefOf.FloatMenu_Open;
            soundClose = SoundDefOf.FloatMenu_Cancel;
        }

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(80f, (UI.screenHeight - 470f) / 2f, 360f, 470f).Rounded();
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = inRect.y;

            using (Temporary.Font(GameFont.Medium))
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 32f),
                    (editing == null ? "YART_Preset_NewTitle" : "YART_Preset_EditTitle").Translate());
            }
            y += 40f;

            // 이름
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            {
                Widgets.Label(new Rect(inRect.x, y, 56f, 28f), "YART_Preset_Name".Translate());
            }
            GUI.SetNextControlName(NameControl);
            presetName = Widgets.TextField(new Rect(inRect.x + 60f, y, inRect.width - 60f, 28f), presetName);
            if (!nameFocusRequested)
            {
                GUI.FocusControl(NameControl);
                nameFocusRequested = true;
            }
            y += 36f;

            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Color(new Color(0.7f, 0.75f, 0.85f)))
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 20f), "YART_Preset_PickTabs".Translate());
            }
            y += 22f;

            // 탭 목록
            float listBottom = inRect.yMax - 42f;
            Rect listOuter = new Rect(inRect.x, y, inRect.width, listBottom - y);
            Widgets.DrawBoxSolid(listOuter, new Color(0f, 0f, 0f, 0.2f));

            var tabs = owner.SelectableTabsForPreset();
            const float rowH = 30f;
            Rect viewRect = new Rect(0f, 0f, listOuter.width - 16f, tabs.Count * rowH);
            Widgets.BeginScrollView(listOuter, ref scroll, viewRect);

            float ry = 0f;
            foreach (var tab in tabs)
            {
                Rect row = new Rect(0f, ry, viewRect.width, rowH);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                bool on = selected.Contains(tab.defName);
                Rect checkRect = new Rect(row.x + 5f, row.y + (rowH - 22f) / 2f, 22f, 22f);
                if (Widgets.ButtonImage(checkRect, on ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex))
                {
                    Toggle(tab.defName, on);
                }

                // 프리뷰(돋보기)
                Rect eyeRect = new Rect(row.xMax - 28f, row.y + (rowH - 22f) / 2f, 22f, 22f);
                TooltipHandler.TipRegion(eyeRect, "YART_Preset_Preview".Translate());
                if (Widgets.ButtonImage(eyeRect, TexButton.Search))
                {
                    owner.PreviewGraphKey(new GraphKey(ChannelRegistry.Bench, tab));
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }

                // 라벨
                Rect labelRect = new Rect(checkRect.xMax + 6f, row.y, eyeRect.x - checkRect.xMax - 10f, rowH);
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                {
                    Widgets.Label(labelRect, ((string)tab.LabelCap).Truncate(labelRect.width));
                }
                if (Widgets.ButtonInvisible(labelRect))
                {
                    Toggle(tab.defName, on);
                }

                ry += rowH;
            }
            Widgets.EndScrollView();

            // 하단 버튼: 완료 / 취소
            float btnY = inRect.yMax - 34f;
            float btnW = (inRect.width - 8f) / 2f;
            bool canDone = !presetName.NullOrEmpty() && selected.Count > 0;

            // 비활성 시 회색으로 — 클릭 자체도 막힘(active:false)
            var prevColor = GUI.color;
            if (!canDone) GUI.color = new Color(1f, 1f, 1f, 0.4f);
            bool donePressed = Widgets.ButtonText(new Rect(inRect.x, btnY, btnW, 32f),
                "YART_Preset_Done".Translate(), active: canDone);
            GUI.color = prevColor;
            if (donePressed)
            {
                Commit();
                Close();
            }
            if (Widgets.ButtonText(new Rect(inRect.x + btnW + 8f, btnY, btnW, 32f), "YART_Preset_Cancel".Translate()))
            {
                Close();
            }
        }

        private void Toggle(string defName, bool currentlyOn)
        {
            if (currentlyOn)
            {
                selected.Remove(defName);
                SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera();
            }
            else
            {
                selected.Add(defName);
                SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera();
            }
        }

        private void Commit()
        {
            string name = presetName.Trim();
            if (name.NullOrEmpty() || selected.Count == 0) return;
            var tabNames = selected.ToList();
            if (editing == null)
            {
                var preset = TabPresetManager.Create(name, tabNames);
                owner.RequestPresetSwitch(preset.Id);
            }
            else
            {
                TabPresetManager.Update(editing, name, tabNames);
                owner.RequestPresetSwitch(editing.Id);
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            owner.PreviewGraphKey(restoreKey);
        }
    }
}
