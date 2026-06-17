using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class SearchableFloatMenu : Window
    {
        public class TrailingButton
        {
            public readonly Texture2D Icon;
            public readonly Action Action;
            public readonly string Tooltip;
            public readonly Color Color;

            public TrailingButton(Texture2D icon, Action action, string tooltip = null, Color? color = null)
            {
                Icon = icon;
                Action = action;
                Tooltip = tooltip;
                Color = color ?? Color.white;
            }
        }

        public class LeadingToggle
        {
            public readonly Func<bool> IsOn;
            public readonly Action OnToggle;
            public readonly Texture2D OnTex, OffTex;
            public readonly Color OnColor, OffColor;

            public LeadingToggle(Func<bool> isOn, Action onToggle, Texture2D onTex, Texture2D offTex, Color onColor, Color offColor)
            {
                IsOn = isOn;
                OnToggle = onToggle;
                OnTex = onTex;
                OffTex = offTex;
                OnColor = onColor;
                OffColor = offColor;
            }
        }

        public class Option
        {
            public readonly string Label;
            public readonly Action Action;
            public readonly bool Selected;
            public readonly List<TrailingButton> Trailing; // 행 우측 액션 아이콘 (편집/삭제 등), null이면 없음
            public readonly LeadingToggle Leading;          // 행 좌측 토글 (즐겨찾기 별), null이면 없음

            public Option(string label, Action action, bool selected = false,
                List<TrailingButton> trailing = null, LeadingToggle leading = null)
            {
                Label = label;
                Action = action;
                Selected = selected;
                Trailing = trailing;
                Leading = leading;
            }
        }

        private const float RowHeight = 28f;
        private const float SearchHeight = 28f;
        private const int MaxVisibleRows = 12;
        private const string SearchControlName = "YARTMenuSearch";

        private readonly List<Option> options;
        private readonly List<Option> filtered = new List<Option>();
        private readonly Vector2 anchor;
        private readonly float menuWidth;
        private string query = "";
        private Vector2 scroll;
        private bool focusRequested;

        protected override float Margin => 4f;

        public SearchableFloatMenu(Vector2 anchorScreen, float width, List<Option> options)
        {
            this.options = options;
            anchor = anchorScreen;
            menuWidth = Mathf.Max(220f, width);

            layer = WindowLayer.Super;
            closeOnClickedOutside = true;
            doWindowBackground = false;
            drawShadow = false;
            preventCameraMotion = false;
            soundAppear = SoundDefOf.FloatMenu_Open;
        }

        protected override void SetInitialSizeAndPosition()
        {
            float listHeight = Mathf.Min(options.Count, MaxVisibleRows) * RowHeight;
            float height = Margin * 2 + SearchHeight + 4f + listHeight + 4f;
            windowRect = new Rect(anchor.x, anchor.y, menuWidth, height);
            if (windowRect.xMax > UI.screenWidth) windowRect.x = UI.screenWidth - windowRect.width;
            if (windowRect.yMax > UI.screenHeight) windowRect.y = UI.screenHeight - windowRect.height;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect bgRect = inRect.ExpandedBy(Margin);
            Widgets.DrawBoxSolid(bgRect, Constraints.PanelBg);
            GUIDrawingUtilities.DrawBorderLines(bgRect, Constraints.PanelBorder, 1f);

            Rect searchRect = new Rect(inRect.x, inRect.y, inRect.width, SearchHeight);
            Widgets.DrawBoxSolid(searchRect, new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(searchRect, Constraints.PanelBorder, 1f);

            // TextField가 키를 먹기 전에 판정
            bool enterPressed = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            GUI.SetNextControlName(SearchControlName);
            query = Widgets.TextField(new Rect(searchRect.x + 6f, searchRect.y + 2f, searchRect.width - 12f, 24f), query);
            if (!focusRequested)
            {
                GUI.FocusControl(SearchControlName);
                focusRequested = true;
            }
            if (query.Length == 0 && GUI.GetNameOfFocusedControl() != SearchControlName)
            {
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                using (Temporary.Color(Color.gray))
                {
                    Widgets.Label(new Rect(searchRect.x + 8f, searchRect.y, searchRect.width - 12f, searchRect.height), "Filter...");
                }
            }

            // 필터
            filtered.Clear();
            string q = query.Trim().ToLowerInvariant();
            for (int i = 0; i < options.Count; i++)
            {
                if (q.Length == 0 || options[i].Label.ToLowerInvariant().Contains(q))
                {
                    filtered.Add(options[i]);
                }
            }

            if (enterPressed && filtered.Count > 0)
            {
                Select(filtered[0]);
                Event.current.Use();
                return;
            }

            // 목록
            Rect outRect = new Rect(inRect.x, searchRect.yMax + 4f, inRect.width, inRect.height - SearchHeight - 8f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - (filtered.Count > MaxVisibleRows ? 16f : 0f), filtered.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            Option clicked = null;
            Action trailingAction = null;
            Action leadingAction = null;
            float rowY = 0f;
            foreach (var option in filtered)
            {
                Rect rowRect = new Rect(0f, rowY, viewRect.width, RowHeight);
                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
                if (option.Selected) Widgets.DrawBoxSolid(rowRect, new Color(1f, 1f, 1f, 0.06f));

                // 좌측 토글
                float leadingW = 0f;
                if (option.Leading != null)
                {
                    float btn = RowHeight - 10f;
                    Rect lRect = new Rect(rowRect.x + 5f, rowRect.y + (RowHeight - btn) / 2f, btn, btn);
                    bool on = option.Leading.IsOn();
                    var prevL = GUI.color;
                    GUI.color = on ? option.Leading.OnColor : option.Leading.OffColor;
                    if (Widgets.ButtonImage(lRect, on ? option.Leading.OnTex : option.Leading.OffTex))
                        leadingAction = option.Leading.OnToggle;
                    GUI.color = prevL;
                    leadingW = btn + 8f;
                }

                // 우측 액션 아이콘
                float trailingW = 0f;
                if (option.Trailing != null && option.Trailing.Count > 0)
                {
                    float tx = rowRect.xMax - 2f;
                    foreach (var tb in option.Trailing)
                    {
                        float btn = RowHeight - 8f;
                        tx -= btn + 4f;
                        Rect tRect = new Rect(tx, rowRect.y + 4f, btn, btn);
                        if (tb.Tooltip != null) TooltipHandler.TipRegion(tRect, tb.Tooltip);
                        var prevC = GUI.color;
                        GUI.color = Mouse.IsOver(tRect) ? tb.Color : GenColor.WithAlpha(tb.Color, 0.75f);
                        if (Widgets.ButtonImage(tRect, tb.Icon)) trailingAction = tb.Action;
                        GUI.color = prevC;
                    }
                    trailingW = (rowRect.xMax - 2f) - tx + 4f;
                }

                Rect selectRect = new Rect(rowRect.x + leadingW, rowRect.y, rowRect.width - leadingW - trailingW, rowRect.height);

                using (Temporary.Font(GameFont.Small))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                using (Temporary.Color(option.Selected ? new Color(0.6f, 0.8f, 1f) : Color.white))
                {
                    Widgets.Label(new Rect(selectRect.x + 8f, selectRect.y, selectRect.width - 12f, selectRect.height),
                        option.Label.Truncate(selectRect.width - 12f));
                }

                if (Widgets.ButtonInvisible(selectRect))
                {
                    clicked = option;
                }
                rowY += RowHeight;
            }

            if (filtered.Count == 0)
            {
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleCenter))
                using (Temporary.Color(Color.gray))
                {
                    Widgets.Label(new Rect(0f, 0f, outRect.width, RowHeight), "(no match)");
                }
            }

            Widgets.EndScrollView();

            if (leadingAction != null)
            {
                leadingAction();
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                Event.current.Use();
                return;
            }

            if (trailingAction != null)
            {
                Close(doCloseSound: false);
                trailingAction();
                return;
            }

            if (clicked != null)
            {
                Select(clicked);
            }
        }

        private void Select(Option option)
        {
            Close(doCloseSound: false);
            SoundDefOf.Click.PlayOneShotOnCamera();
            option.Action?.Invoke();
        }
    }
}
