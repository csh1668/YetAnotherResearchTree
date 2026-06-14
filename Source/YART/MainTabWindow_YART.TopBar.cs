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
    public partial class MainTabWindow_YART
    {
        private static List<ResearchChannel> GetVisibleChannels()
        {
            var result = new List<ResearchChannel> { ChannelRegistry.Bench };
            var graph = ResearchGraph.Instance;
            foreach (var channel in ChannelRegistry.All)
            {
                if (channel.IsBench) continue;
                if (graph.ChannelHasRealNodes(channel)) result.Add(channel);
            }
            return result;
        }

        private static List<ResearchTabDef> GetVisibleStandardTabs()
        {
            var result = new List<ResearchTabDef>();
            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                // 통합 키(IsUnified)는 Tab=null이므로 반드시 먼저 건너뜀
                if (kvp.Key.IsUnified) continue;
                if (!kvp.Key.Channel.IsBench) continue;
                if (!kvp.Value.Nodes.Any(n => !n.IsDummy && !n.IsProxy)) continue;
                if (!Find.ResearchManager.TabInfoVisible(kvp.Key.Tab)) continue;
                result.Add(kvp.Key.Tab);
            }
            result.SortBy(t => (int)t.index); // Def 로드 순서 (Main이 항상 첫 번째)
            return result;
        }

        private Rect ComputeTopLeftControlsRect()
        {
            float row1 = 0f; // All tabs 토글 + 탭 드롭다운 (벤치 + 탭 ≥2)
            if (SelectedChannel.IsBench && GetVisibleStandardTabs().Count >= 2)
            {
                row1 += Constraints.UnifiedBenchToggleWidth + 8f;
                if (!CurrentKey.IsUnified) row1 += Constraints.TabDropdownWidth + 8f;
            }
            float content = Mathf.Max(row1, 220f); // 아래 행 검색창 폭 220
            return new Rect(0f, 0f, 16f + content + 16f, Constraints.QueueBarHeight);
        }

        private void DoTopLeftControls()
        {
            const float x0 = 16f;
            const float y1 = 12f;
            const float searchH = 28f;

            bool hasTabRow = false;

            // 1행: 벤치 채널의 All tabs 토글 + 탭 드롭다운
            if (SelectedChannel.IsBench)
            {
                var visibleTabs = GetVisibleStandardTabs();

                // 통합 뷰가 아닐 때만 selectedTab 유효성 보정 (통합 중엔 매 프레임 키 교체로 뷰가 튕김)
                if (!CurrentKey.IsUnified)
                {
                    var currentSub = ResearchGraph.Instance.GetSubGraph(CurrentKey);
                    bool currentTabValid = currentSub != null && currentSub.Nodes.Any(n => !n.IsDummy && !n.IsProxy);
                    if (!currentTabValid && visibleTabs.Count > 0)
                    {
                        SwitchGraph(new GraphKey(ChannelRegistry.Bench, visibleTabs[0]), playSound: false);
                    }
                }

                if (visibleTabs.Count >= 2)
                {
                    hasTabRow = true;
                    float x = x0;
                    Rect toggleRect = new Rect(x, y1, Constraints.UnifiedBenchToggleWidth, Constraints.TabDropdownHeight);
                    DrawUnifiedBenchToggle(toggleRect);
                    x += Constraints.UnifiedBenchToggleWidth + 8f;

                    if (!CurrentKey.IsUnified)
                    {
                        Rect ddRect = new Rect(x, y1, Constraints.TabDropdownWidth, Constraints.TabDropdownHeight);
                        DrawTabDropdown(ddRect, visibleTabs);
                    }
                }
            }

            // 2행: 검색창 (탭 행이 있으면 그 아래, 없으면 맨 위 자리)
            float searchY = hasTabRow ? y1 + Constraints.TabDropdownHeight + 6f : y1;
            Rect searchRect = new Rect(x0, searchY, 220f, searchH);
            DoSearchBox(searchRect);
        }

        private Rect ComputeTrackChipsRect(Rect inRect)
        {
            var channels = GetVisibleChannels();
            if (channels.Count <= 1 && ResearchQueueManager.Instance == null) return Rect.zero;

            float height = channels.Count * (Constraints.TrackChipHeight + Constraints.TrackChipGap)
                           - Constraints.TrackChipGap;
            return new Rect(inRect.xMax - 16f - Constraints.TrackChipWidth,
                inRect.yMax - 16f - height, Constraints.TrackChipWidth, height);
        }

        private void DoTrackChipsColumn(Rect rect)
        {
            if (rect.height <= 0f) return;

            var channels = GetVisibleChannels();
            float y = rect.y;
            foreach (var channel in channels)
            {
                DrawTrackChip(new Rect(rect.x, y, rect.width, Constraints.TrackChipHeight), channel);
                y += Constraints.TrackChipHeight + Constraints.TrackChipGap;
            }
        }

        private void DoZoomIndicator(Rect zoomRect)
        {
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.75f, 0.82f, 0.92f, 0.95f)))
            {
                Widgets.Label(zoomRect, $"{zoomLevel * 100:F0}%");
            }

            TooltipHandler.TipRegion(zoomRect, "YART_ZoomTooltip".Translate());
            if (Widgets.ButtonInvisible(zoomRect) && zoomLevel != 1f)
            {
                Vector2 screenCenter = new Vector2(canvasRect.width, canvasRect.height) / 2f;
                Vector2 centerWorld = (screenCenter - scrollPosition) / zoomLevel;
                zoomLevel = 1f;
                scrollPosition = screenCenter - centerWorld * zoomLevel;
                scrollAnimating = false;
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
        }

        private void DoVanillaSwitchButton(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.45f));
            GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
            GUIDrawingUtilities.DrawIcon(rect.ContractedBy(4f), Assets.IconSwap, new Color(0.82f, 0.87f, 0.97f));
            TooltipHandler.TipRegion(rect, "YART_SwitchToVanilla".Translate());
            if (Widgets.ButtonInvisible(rect))
            {
                VanillaTabReplacer.SwitchTo(useYart: false);
            }
        }

        private void DrawTrackChip(Rect rect, ResearchChannel channel)
        {
            bool selected = channel == SelectedChannel;
            Color trackColor = channel.Color;
            var queueMgr = ResearchQueueManager.Instance;
            var current = ResearchQueueManager.GetCurrentProject(channel);
            var queue = queueMgr?.GetQueue(channel);
            int queueCount = queue?.Count ?? 0;

            // 배경/테두리
            Widgets.DrawBoxSolid(rect, selected ? GenColor.WithAlpha(trackColor, 0.15f) : new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, selected ? trackColor : Constraints.PanelBorder, selected ? 2f : 1f);
            if (!selected && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            float pad = 6f;

            // 1행: 채널명 (+ 큐 수) — 큐 배지가 있으면 nameRect 오른쪽을 줄여 겹침 방지
            Rect nameRect = new Rect(rect.x + pad, rect.y + pad - 1f, rect.width - pad * 2 - (queueCount > 0 ? 30f : 0f), 18f);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Color(GenColor.WithAlpha(trackColor, 0.9f)))
            {
                Widgets.Label(nameRect, channel.Label);
            }

            if (queueCount > 0)
            {
                using (Temporary.Font(GameFont.Small))
                {
                    string countText = queueCount.ToString();
                    Vector2 textSize = Text.CalcSize(countText);
                    const float pillH = 18f;
                    float pillW = Mathf.Max(pillH, textSize.x + 10f);
                    Rect pillRect = new Rect(rect.xMax - pad - pillW, rect.y + pad - 1f, pillW, pillH);

                    // 둥근 pill 배경 (GUI.DrawTexture의 borderRadius 오버로드 — 게임 동봉 Unity 모듈에서 시그니처 확인됨)
                    GUI.DrawTexture(pillRect, BaseContent.WhiteTex, ScaleMode.StretchToFill, true, 0f,
                        GenColor.WithAlpha(trackColor, 0.30f), 0f, pillH / 2f);

                    using (Temporary.Anchor(TextAnchor.MiddleCenter))
                    using (Temporary.Color(new Color(0.92f, 0.95f, 1f)))
                    {
                        Widgets.Label(pillRect, countText);
                    }
                }
            }

            // 2행: 현재 프로젝트 또는 상태
            Rect statusRect = new Rect(rect.x + pad, rect.y + 22f, rect.width - pad * 2, rect.height - 28f);
            string statusText;
            Color statusColor;
            string tooltip = null;
            bool waiting = false;

            if (current != null)
            {
                statusText = current.LabelCap;
                statusColor = Color.white;
            }
            else if (queueCount > 0)
            {
                // 머리가 막혀 있음 (크로스트랙 선행, 벤치 부재 등)
                waiting = true;
                statusText = queue[0].LabelCap;
                statusColor = new Color(0.95f, 0.8f, 0.35f);
                tooltip = "YART_CannotStartYet".Translate(queue[0].LabelCap);
            }
            else
            {
                statusText = "YART_Idle".Translate();
                statusColor = channel.IsBench
                    ? new Color(0.95f, 0.6f, 0.4f) // 벤치 채널 유휴는 경고 톤
                    : new Color(0.5f, 0.5f, 0.55f);
            }

            if (waiting)
            {
                GUIDrawingUtilities.DrawIcon(new Rect(statusRect.x, statusRect.y + 1f, 12f, 12f), Assets.IconLock, statusColor);
                statusRect.x += 16f;
                statusRect.width -= 16f;
            }

            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Color(statusColor))
            {
                Widgets.Label(statusRect, statusText.Truncate(statusRect.width));
            }

            // 진행바 (하단)
            if (current != null)
            {
                Rect barRect = new Rect(rect.x + 1f, rect.yMax - 4f, (rect.width - 2f) * Mathf.Clamp01(current.ProgressPercent), 3f);
                Widgets.DrawBoxSolid(new Rect(rect.x + 1f, rect.yMax - 4f, rect.width - 2f, 3f), new Color(0f, 0f, 0f, 0.5f));
                Widgets.DrawBoxSolid(barRect, trackColor);
            }

            if (channel.Tooltip != null) tooltip = tooltip == null ? channel.Tooltip : tooltip + "\n\n" + channel.Tooltip;

            if (tooltip != null)
            {
                TooltipHandler.TipRegion(rect, tooltip);
            }

            if (Widgets.ButtonInvisible(rect))
            {
                // 통합 뷰 ON + 벤치 칩 클릭 → 통합 키로 전환 (일반 탭 키로 돌아가지 않게).
                if (YARTMod.Settings.unifiedBenchView && channel.IsBench)
                {
                    GraphBuildPipeline.EnsureBuilt();
                    ResearchGraph.Instance.GetOrBuildUnifiedBench();
                    if (ResearchGraph.Instance.GetSubGraph(GraphKey.UnifiedBench) != null)
                        SwitchGraph(GraphKey.UnifiedBench);
                    else
                        SwitchGraph(new GraphKey(channel, selectedTab));
                }
                else
                {
                    SwitchGraph(new GraphKey(channel, selectedTab));
                }
            }
        }

        private void DrawUnifiedBenchToggle(Rect rect)
        {
            bool isOn = CurrentKey.IsUnified;

            Widgets.DrawBoxSolid(rect, isOn ? GenColor.WithAlpha(SelectedChannel.Color, 0.15f) : new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, isOn ? SelectedChannel.Color : Constraints.PanelBorder, isOn ? 1.5f : 1f);
            if (!isOn && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            // 체크 아이콘 (ON) 또는 빈 박스 아이콘 영역 (OFF)
            float iconSize = 14f;
            Rect iconRect = new Rect(rect.x + 6f, rect.y + (rect.height - iconSize) / 2f, iconSize, iconSize);
            if (isOn)
            {
                GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconCheck, SelectedChannel.Color);
            }
            else
            {
                Widgets.DrawBoxSolid(iconRect, new Color(0.25f, 0.32f, 0.42f));
                GUIDrawingUtilities.DrawBorderLines(iconRect, Constraints.PanelBorder, 1f);
            }

            // 라벨
            Rect labelRect = new Rect(rect.x + iconSize + 10f, rect.y, rect.width - iconSize - 14f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(isOn ? SelectedChannel.Color : new Color(0.75f, 0.82f, 0.92f)))
            {
                Widgets.Label(labelRect, "YART_AllTabs".Translate());
            }

            TooltipHandler.TipRegion(rect, "YART_AllTabsTooltip".Translate());

            if (Widgets.ButtonInvisible(rect))
            {
                if (!isOn)
                {
                    // ON: 통합 그래프 빌드 후 서브그래프가 존재하면 전환
                    GraphBuildPipeline.EnsureBuilt();
                    ResearchGraph.Instance.GetOrBuildUnifiedBench();
                    if (ResearchGraph.Instance.GetSubGraph(GraphKey.UnifiedBench) != null)
                    {
                        YARTMod.Settings.unifiedBenchView = true;
                        YARTMod.Settings.Write();
                        SwitchGraph(GraphKey.UnifiedBench);
                    }
                }
                else
                {
                    // OFF: 마지막으로 보던 탭으로 복귀
                    YARTMod.Settings.unifiedBenchView = false;
                    YARTMod.Settings.Write();
                    SwitchGraph(new GraphKey(ChannelRegistry.Bench, selectedTab));
                }
            }
        }

        private void DrawTabDropdown(Rect rect, List<ResearchTabDef> tabs)
        {
            var currentTab = CurrentKey.Tab; // 통합 뷰가 아닐 때만 호출되므로 Tab != null 보장

            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            Rect labelRect = new Rect(rect.x + 8f, rect.y, rect.width - 28f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            {
                string label = currentTab != null ? (string)currentTab.LabelCap : "—";
                Widgets.Label(labelRect, label.Truncate(labelRect.width));
            }

            Rect arrowRect = new Rect(rect.xMax - 20f, rect.y, 16f, rect.height);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            using (Temporary.Color(new Color(0.7f, 0.75f, 0.85f)))
            {
                Widgets.Label(arrowRect, "▼");
            }

            TooltipHandler.TipRegion(rect, "YART_TabDropdownTooltip".Translate());

            if (Widgets.ButtonInvisible(rect))
            {
                // 검색 가능한 드롭다운 (모드 다수 환경에서 탭이 많아질 수 있음 — 키는 라벨만)
                var options = new List<SearchableFloatMenu.Option>();
                foreach (var tab in tabs)
                {
                    var localTab = tab;
                    options.Add(new SearchableFloatMenu.Option(localTab.LabelCap, () =>
                    {
                        SwitchGraph(new GraphKey(ChannelRegistry.Bench, localTab));
                    }, selected: localTab == currentTab));
                }
                Vector2 anchorScreen = GUIUtility.GUIToScreenPoint(new Vector2(rect.x, rect.yMax + 2f));
                Find.WindowStack.Add(new SearchableFloatMenu(anchorScreen, rect.width + 80f, options));
            }
        }
    }
}
