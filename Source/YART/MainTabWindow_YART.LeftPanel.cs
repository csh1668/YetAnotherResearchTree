using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;
using YART.Data;
using YART.Utils;

namespace YART
{
    // 좌측 상세 패널: 선택 연구 정보, 잠금 사유, 액션 버튼, 해금/선행/후행 목록
    public partial class MainTabWindow_YART
    {
        private static readonly List<string> lockedReasonsBuffer = new List<string>(8);
        private static readonly List<ResearchNode> emptyNodeList = new List<ResearchNode>();

        // CalculateLeftPanelHeight 캐시 — Text.CalcHeight 다발 호출을 0.5초 주기로 제한
        // (상태 캐시와 동일 주기; 잠금 사유/진행 상태 변화 반영용)
        private ResearchNode panelHeightCacheNode;
        private float panelHeightCacheWidth;
        private float panelHeightCacheValue;
        private float panelHeightCacheTime = float.NegativeInfinity;

        /// <summary>
        /// TechLevel 표시 라벨. 바닐라 ToStringHuman은 Undefined에 대한 번역 키("Undefined")가
        /// Enums.xml에 없어 번역 실패(릴리스: 키 평문 노출, DevMode: 의사번역)한다.
        /// techLevel을 지정하지 않은 연구(=Undefined)만 YART 키로 대체하고, 나머지는 바닐라 라벨 사용.
        /// </summary>
        private static string TechLevelLabel(TechLevel tl)
        {
            return tl == TechLevel.Undefined
                ? (string)"YART_TechLevelUndefined".Translate()
                : tl.ToStringHuman();
        }

        private void DoLeftRect(Rect rect)
        {
            // Glass panel background
            Widgets.DrawBoxSolid(rect, Constraints.PanelBg);

            if (history.HasCurrent)
            {
                var currentNode = history.Current;
                Color eraColor = currentNode.EraAccentColor;

                // Top accent bar (era color)
                GUIDrawingUtilities.DrawTopAccentBar(rect, eraColor, 4f);

                // Panel border
                GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);

                // Navigation Buttons (Top Right)
                float btnSize = 24f;
                float gap = 4f;
                float btnY = rect.y + 10f;
                float btnX = rect.xMax - btnSize - 8f;

                // Close (X)
                Rect closeRect = new Rect(btnX, btnY, btnSize, btnSize);
                if (DrawNavButton(closeRect, "X", true))
                {
                    history.Clear();
                    return;
                }
                btnX -= (btnSize + gap);

                // Forward (>)
                Rect forwardRect = new Rect(btnX, btnY, btnSize, btnSize);
                if (DrawNavButton(forwardRect, ">", history.CanRedo))
                {
                    history.Redo();
                    leftPanelScrollPosition = Vector2.zero;
                    currentNode = history.Current;
                    NavigateToHistoryNode(currentNode);
                }
                btnX -= (btnSize + gap);

                // Back (<)
                Rect backRect = new Rect(btnX, btnY, btnSize, btnSize);
                if (DrawNavButton(backRect, "<", history.CanUndo))
                {
                    history.Undo();
                    leftPanelScrollPosition = Vector2.zero;
                    currentNode = history.Current;
                    NavigateToHistoryNode(currentNode);
                }
                btnX -= (btnSize + gap);

                // Info card (ⓘ) — 바닐라 위젯, 클릭 시 Dialog_InfoCard를 스스로 연다
                Widgets.InfoCardButton(new Rect(btnX, btnY, btnSize, btnSize), currentNode.Def);

                // 스크롤 영역 설정
                float headerHeight = 45f;
                // 실제 콘텐츠 폭 = viewRect(rect.width-20) - 16 — 높이 계산도 같은 폭 사용 (구 고정폭 260 불일치 수정)
                float heightContentWidth = rect.width - 36f;
                if (panelHeightCacheNode != currentNode || panelHeightCacheWidth != heightContentWidth
                    || Time.realtimeSinceStartup - panelHeightCacheTime > 0.5f)
                {
                    panelHeightCacheValue = CalculateLeftPanelHeight(currentNode, heightContentWidth);
                    panelHeightCacheNode = currentNode;
                    panelHeightCacheWidth = heightContentWidth;
                    panelHeightCacheTime = Time.realtimeSinceStartup;
                }
                Rect viewRect = new Rect(0, 0, rect.width - 20, panelHeightCacheValue);
                Rect scrollRect = new Rect(rect.x, rect.y + headerHeight, rect.width, rect.height - headerHeight);

                Widgets.BeginScrollView(scrollRect, ref leftPanelScrollPosition, viewRect);

                float y = 0;
                float contentWidth = viewRect.width - 16;
                float padding = 12f;

                // 연구 제목 (with era color) + 우측 출처 모드/DLC 아이콘
                var titleIcon = currentNode.IsHidden ? null : GetModIcon(currentNode.SourceMod);
                const float titleIconSz = 24f;
                float titleW = contentWidth - (titleIcon != null ? titleIconSz + 6f : 0f);
                using (Temporary.Font(GameFont.Medium))
                using (Temporary.Anchor(TextAnchor.UpperLeft))
                using (Temporary.Color(eraColor))
                {
                    Widgets.Label(new Rect(padding, y, titleW, 30), currentNode.Label);
                }
                if (titleIcon != null)
                {
                    Rect titleIconRect = new Rect(padding + contentWidth - titleIconSz, y + 3f, titleIconSz, titleIconSz);
                    var prevTitleColor = GUI.color;
                    GUI.color = Color.white;
                    GUI.DrawTexture(titleIconRect, titleIcon, ScaleMode.ScaleToFit);
                    GUI.color = prevTitleColor;
                    if (Mouse.IsOver(titleIconRect) && currentNode.SourceMod != null)
                    {
                        TooltipHandler.TipRegion(titleIconRect, currentNode.SourceMod.Name);
                    }
                }
                y += 32;

                // Tech Level Badge
                string techLevelText = TechLevelLabel(currentNode.TechLevel);
                Rect badgeRect = new Rect(padding, y, 80, 20);
                Widgets.DrawBoxSolid(badgeRect, GenColor.WithAlpha(eraColor, 0.3f));
                GUIDrawingUtilities.DrawBorderLines(badgeRect, GenColor.WithAlpha(eraColor, 0.6f), 1f);
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleCenter))
                {
                    Widgets.Label(badgeRect, techLevelText);
                }
                y += 28;

                // 설명
                using (Temporary.Font(GameFont.Small))
                using (Temporary.Color(new Color(0.8f, 0.8f, 0.85f)))
                {
                    float descHeight = Text.CalcHeight(currentNode.Def.description, contentWidth);
                    Widgets.Label(new Rect(padding, y, contentWidth, descHeight), currentNode.Def.description);
                    y += descHeight + 12;
                }

                // 비용 및 진행률 — 바닐라 동일하게 CostApparent(테크레벨 차이 계수 반영) 표시.
                // 조금이라도 진행됐으면 "123 / 700" 형식 (바닐라 연구 목록과 동일 표기)
                Rect costRect = new Rect(padding, y, contentWidth, 22);
                using (Temporary.Font(GameFont.Small))
                {
                    string costText = !currentNode.Def.IsFinished && currentNode.Progress > 0f
                        ? "YART_CostProgress".Translate(currentNode.Def.ProgressApparentString, currentNode.Def.CostApparent.ToString("F0"))
                        : "YART_Cost".Translate(currentNode.Def.CostApparent.ToString("N0"));
                    Widgets.Label(costRect, costText);
                }
                y += 28;

                // 테크레벨 차이로 비용이 부풀려진 경우 경고 (바닐라 번역 키 재사용)
                float costFactor = currentNode.Def.CostFactor(Faction.OfPlayer.def.techLevel);
                if (costFactor != 1f && !currentNode.Def.IsFinished)
                {
                    string costWarn = "TechLevelTooLow".Translate(Faction.OfPlayer.def.techLevel.ToStringHuman(),
                        currentNode.Def.techLevel.ToStringHuman(), (1f / costFactor).ToStringPercent())
                        + " " + "ResearchCostComparison".Translate(
                            currentNode.Def.Cost.ToString("F0"), currentNode.Def.CostApparent.ToString("F0"));
                    using (Temporary.Font(GameFont.Tiny))
                    using (Temporary.Color(new Color(0.95f, 0.8f, 0.35f)))
                    {
                        float warnHeight = Text.CalcHeight(costWarn, contentWidth);
                        Widgets.Label(new Rect(padding, y, contentWidth, warnHeight), costWarn);
                        y += warnHeight + 6;
                    }
                }

                // 요구 작업대/시설 (완료 전 상시 표시 — 어떤 건물이 필요한지 계획에 필요)
                y += DrawRequirementsSection(currentNode, padding, y, contentWidth);

                // 잠금 사유 (Locked일 때만; 선행 미완료는 큐로 해결되므로 사유에 포함되지 않음)
                if (currentNode.State == ResearchNodeState.Locked)
                {
                    currentNode.GetLockedReasons(lockedReasonsBuffer);
                    if (lockedReasonsBuffer.Count > 0)
                    {
                        using (Temporary.Font(GameFont.Small))
                        using (Temporary.Color(new Color(0.95f, 0.5f, 0.45f)))
                        {
                            Widgets.Label(new Rect(padding, y, contentWidth, 22), "YART_LockedReasons".Translate());
                            y += 22;
                            foreach (var reason in lockedReasonsBuffer)
                            {
                                string line = "  • " + reason;
                                float lineHeight = Text.CalcHeight(line, contentWidth);
                                Widgets.Label(new Rect(padding, y, contentWidth, lineHeight), line);
                                y += lineHeight + 2;
                            }
                        }
                        y += 8;
                    }
                }

                // 청사진 구입처 보충
                if (currentNode.Def.TechprintCount > 0 && !currentNode.Def.TechprintRequirementMet)
                {
                    y += DrawTechprintSourcesSection(currentNode.Def, padding, y, contentWidth);

                    if (Prefs.DevMode)
                    {
                        if (DrawPanelButton(new Rect(padding, y, contentWidth, 26), "Dev: Apply techprint",
                                new Color(0.85f, 0.5f, 0.3f), enabled: true))
                        {
                            Find.ResearchManager.ApplyTechprint(currentNode.Def, null);
                            SoundDefOf.TechprintApplied.PlayOneShotOnCamera();
                            ResearchNode.InvalidateAllStates();
                        }
                        y += 30;
                    }
                    y += 4;
                }

                // 연구/큐 버튼 (상태에 따라 1~2개)
                y += DrawResearchActionButtons(currentNode, padding, y, contentWidth);

                // Divider
                DrawSectionDivider(new Rect(padding, y, contentWidth, 1));
                y += 12;

                // Unlocked Content (New Section)
                var unlockedDefs = currentNode.UnlockedDefs;
                Rect unlockedHeaderRect = new Rect(padding, y, contentWidth, 25);
                // 연구 보상
                y += DrawSectionHeader(unlockedHeaderRect, "Unlocks".Translate(), unlockedDefs.Count, Constraints.SectionUnlocks);

                // 2-column(아이콘+라벨) 토글 버튼 — 카운트 배지(우측 24px) 왼쪽에. 항목이 있을 때만.
                if (unlockedDefs.Count > 0)
                {
                    bool expanded = YARTMod.Settings.unlockedContentExpanded;
                    Rect toggleRect = new Rect(unlockedHeaderRect.xMax - 24f - 4f - 18f, unlockedHeaderRect.y + 2f, 18f, 18f);
                    if (Mouse.IsOver(toggleRect)) Widgets.DrawHighlight(toggleRect);
                    GUIDrawingUtilities.DrawIcon(toggleRect, Assets.IconSwap,
                        expanded ? Constraints.SectionUnlocks : Constraints.ToggleInactive);
                    TooltipHandler.TipRegion(toggleRect, "YART_ToggleUnlockedView".Translate());
                    if (Widgets.ButtonInvisible(toggleRect))
                    {
                        SoundDefOf.Click.PlayOneShotOnCamera();
                        YARTMod.Settings.unlockedContentExpanded = !expanded;
                        YARTMod.Settings.Write();
                    }
                }

                if (unlockedDefs.Count == 0)
                {
                    using (Temporary.Color(Constraints.MutedText))
                    using (Temporary.Font(GameFont.Small))
                    {
                        Widgets.Label(new Rect(padding + 8, y, contentWidth, 22), "YART_None".Translate());
                    }
                    y += 26;
                }
                else if (YARTMod.Settings.unlockedContentExpanded)
                {
                    // 2-column: 아이콘 + 라벨
                    const float rowH = 30f, rowGap = 2f, colGap = 8f, cellIcon = 24f;
                    float colW = (contentWidth - colGap) / 2f;

                    for (int i = 0; i < unlockedDefs.Count; i++)
                    {
                        var def = unlockedDefs[i];
                        int col = i % 2;
                        float cellX = padding + col * (colW + colGap);
                        Rect cellRect = new Rect(cellX, y, colW, rowH);

                        Rect iconRect = new Rect(cellX, y + (rowH - cellIcon) / 2f, cellIcon, cellIcon);
                        Widgets.DefIcon(iconRect, def, null, 1f, null, drawPlaceholder: true);

                        Rect labelRect = new Rect(iconRect.xMax + 6f, y, colW - cellIcon - 6f, rowH);
                        using (Temporary.Font(GameFont.Tiny))
                        using (Temporary.Anchor(TextAnchor.MiddleLeft))
                        {
                            Widgets.Label(labelRect, ((string)def.LabelCap).Truncate(labelRect.width));
                        }

                        if (Mouse.IsOver(cellRect))
                        {
                            Widgets.DrawHighlight(cellRect);
                            TooltipHandler.TipRegion(cellRect, def.LabelCap);
                        }
                        if (Widgets.ButtonInvisible(cellRect))
                        {
                            SoundDefOf.Click.PlayOneShotOnCamera();
                            Find.WindowStack.Add(new Dialog_InfoCard(def));
                        }

                        if (col == 1) y += rowH + rowGap; // 행 완료 시 전진
                    }
                    if (unlockedDefs.Count % 2 == 1) y += rowH + rowGap; // 홀수 마지막 행 정산
                }
                else
                {
                    float iconSize = 32f;
                    float iconGap = 6f;
                    float startX = padding;
                    float curX = startX;

                    foreach (var def in unlockedDefs)
                    {
                        if (curX + iconSize > contentWidth)
                        {
                            curX = startX;
                            y += iconSize + iconGap;
                        }

                        Rect iconRect = new Rect(curX, y, iconSize, iconSize);

                        // Draw Icon (아이콘 없으면 placeholder)
                        Widgets.DefIcon(iconRect, def, null, 1f, null, drawPlaceholder: true);

                        // Highlight on hover
                        if (Mouse.IsOver(iconRect))
                        {
                            Widgets.DrawHighlight(iconRect);
                            TooltipHandler.TipRegion(iconRect, def.LabelCap);
                        }

                        // Click to open Info Card
                        if (Widgets.ButtonInvisible(iconRect))
                        {
                            SoundDefOf.Click.PlayOneShotOnCamera();
                            Find.WindowStack.Add(new Dialog_InfoCard(def));
                        }

                        curX += iconSize + iconGap;
                    }
                    y += iconSize + iconGap; // Final row height
                }
                y += 8;

                // Divider
                DrawSectionDivider(new Rect(padding, y, contentWidth, 1));
                y += 12;

                // 선행 연구 (Prerequisites)
                var prereqs = currentNode.PanelPrerequisites ?? emptyNodeList;
                y += DrawSectionHeader(new Rect(padding, y, contentWidth, 25), "YART_Prerequisites".Translate(), prereqs.Count, Constraints.SectionPrerequisites);

                if (prereqs.Count == 0)
                {
                    using (Temporary.Color(Constraints.MutedText))
                    using (Temporary.Font(GameFont.Small))
                    {
                        Widgets.Label(new Rect(padding + 8, y, contentWidth, 22), "YART_None".Translate());
                    }
                    y += 26;
                }
                else
                {
                    foreach (var prereq in prereqs)
                    {
                        y += DrawResearchCard(new Rect(padding, y, contentWidth, 36), prereq);
                    }
                }
                y += 8;

                // Divider
                DrawSectionDivider(new Rect(padding, y, contentWidth, 1));
                y += 12;

                // 후행 연구 (Unlocks)
                var children = currentNode.PanelChildren ?? emptyNodeList;
                y += DrawSectionHeader(new Rect(padding, y, contentWidth, 25), "YART_Unlocks".Translate(), children.Count, Constraints.SectionFollowups);

                if (children.Count == 0)
                {
                    using (Temporary.Color(Constraints.MutedText))
                    using (Temporary.Font(GameFont.Small))
                    {
                        Widgets.Label(new Rect(padding + 8, y, contentWidth, 22), "YART_None".Translate());
                    }
                    y += 26;
                }
                else
                {
                    foreach (var child in children)
                    {
                        y += DrawResearchCard(new Rect(padding, y, contentWidth, 36), child);
                    }
                }

                Widgets.EndScrollView();
            }
        }

        /// <summary>
        /// 이력 내비게이션(&lt;/&gt;) 공용: 노드가 다른 그래프 소속이면 전환 후 포커스.
        /// JumpToNode와 달리 history는 건드리지 않는다 (이력 이동 중이므로).
        /// </summary>
        private void NavigateToHistoryNode(ResearchNode node)
        {
            if (node == null) return;
            // 히스토리 항목을 현재 뷰 기준으로 해석한다 — 통합/탭 뷰를 전환한 뒤에도 현재 뷰에 머무른다.
            // (통합 뷰에서 쌓인 복사본 항목을 탭 뷰로 되돌린 뒤 내비게이션해도 통합으로 재진입하지 않고,
            //  현재 그래프에 그 def가 없을 때만 원래 그래프로 전환한다.)
            var resolved = GetNodeOnCurrentGraph(node.Def) ?? node;
            if (!resolved.Key.Equals(CurrentKey))
            {
                SwitchGraph(resolved.Key, playSound: false); // 나브 버튼/마우스 버튼이 자체 Click 재생
            }
            FocusOnNode(resolved);
        }

        /// <summary>
        /// 좌측 패널의 연구 액션 버튼 영역. 상태에 따라 [Start now]/[Queue]/[Unqueue]/[Stop] 조합을 그립니다.
        /// 반환값은 사용한 세로 높이 (CalculateLeftPanelHeight의 45와 일치해야 함).
        /// </summary>
        private float DrawResearchActionButtons(ResearchNode node, float padding, float y, float contentWidth)
        {
            const float rowHeight = 45f;
            const float btnHeight = 32f;
            var def = node.Def;
            var queueMgr = ResearchQueueManager.Instance;

            float btnWidth = (contentWidth - 8f) / 2f;
            Rect btnA = new Rect(padding, y, btnWidth, btnHeight);
            Rect btnB = new Rect(padding + btnWidth + 8f, y, btnWidth, btnHeight);
            Rect btnFull = new Rect(padding, y, contentWidth, btnHeight);

            bool isCurrent = Find.ResearchManager.IsCurrentProject(def);
            int queuePos = queueMgr?.GetQueuePosition(def) ?? -1;

            if (node.State == ResearchNodeState.Completed)
            {
                DrawPanelButton(btnFull, "YART_Completed".Translate(), Constraints.ButtonDisabled, enabled: false);
                return rowHeight;
            }

            if (isCurrent || node.State == ResearchNodeState.InProgress)
            {
                if (SemiRandomResearchCompat.Active)
                {
                    DrawPanelButton(btnFull, "YART_InProgress".Translate(), Constraints.ButtonActive, enabled: false);
                    TooltipHandler.TipRegion(btnFull, "YART_SemiRandomActiveDesc".Translate());
                    return rowHeight;
                }

                DrawPanelButton(btnA, "YART_InProgress".Translate(), Constraints.ButtonActive, enabled: false);
                if (DrawPanelButton(btnB, "YART_Stop".Translate(), new Color(0.7f, 0.3f, 0.3f), enabled: true))
                {
                    if (queueMgr != null) queueMgr.Remove(def);
                    else Find.ResearchManager.StopProject(def);
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                }
                return rowHeight;
            }

            if (node.State == ResearchNodeState.Available)
            {
                // Semi Random Research 활성: 수동 시작/큐잉 불가 — 그 모드의 'Next Research' 탭에서 선택.
                if (SemiRandomResearchCompat.Active)
                {
                    DrawPanelButton(btnFull, "YART_SemiRandomActive".Translate(), Constraints.ButtonDisabled, enabled: false);
                    TooltipHandler.TipRegion(btnFull, "YART_SemiRandomActiveDesc".Translate());
                    return rowHeight;
                }

                if (queuePos > 0)
                {
                    if (DrawPanelButton(btnFull, "YART_Unqueue".Translate(queuePos), Constraints.ButtonDisabled, enabled: true))
                    {
                        queueMgr.Remove(def);
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                    }
                    return rowHeight;
                }

                int chainCount = ResearchQueueManager.CollectMissingChain(def).Count;
                string label = chainCount > 1 ? "YART_QueueChain".Translate(chainCount) : "YART_Queue".Translate();
                if (queueMgr != null) TooltipHandler.TipRegion(btnFull, QueueModifierTip());
                if (DrawPanelButton(btnFull, label, Constraints.ButtonActive, enabled: queueMgr != null))
                {
                    EnqueueByModifier(queueMgr, def); // 노드 클릭과 동일: 기본=끝, Alt=앞, Ctrl=비우고 추가
                }
                return rowHeight;
            }

            DrawPanelButton(btnFull, "YART_Locked".Translate(), Constraints.ButtonDisabled, enabled: false);
            return rowHeight;
        }


        private static void EnqueueByModifier(ResearchQueueManager mgr, ResearchProjectDef def)
        {
            if (mgr == null) return;
            var e = Event.current;
            if (e.control) mgr.EnqueueWithChainExclusive(def);
            else if (e.alt) mgr.EnqueueWithChainToFront(def);
            else mgr.EnqueueWithChain(def);
        }

        private static string QueueModifierTip() => string.Concat(
            (string)"YART_TipQueueShift".Translate(), "\n",
            (string)"YART_TipQueueAlt".Translate(), "\n",
            (string)"YART_TipQueueCtrl".Translate());

        private bool DrawPanelButton(Rect rect, string label, Color color, bool enabled)
        {
            Widgets.DrawBoxSolid(rect, GenColor.WithAlpha(color, enabled ? 0.3f : 0.18f));
            GUIDrawingUtilities.DrawBorderLines(rect, GenColor.WithAlpha(color, enabled ? 1f : 0.5f), 1f);

            if (enabled && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            using (Temporary.Alpha(enabled ? 1f : 0.5f))
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(rect, label);
            }

            return enabled && Widgets.ButtonInvisible(rect);
        }

        /// <summary>
        /// 연구를 즉시 시작합니다. 큐 매니저를 통해 큐 머리로 올린 뒤 바닐라 API로 시작.
        /// </summary>
        private void StartResearch(ResearchProjectDef project)
        {
            var queueMgr = ResearchQueueManager.Instance;
            if (queueMgr != null)
            {
                queueMgr.StartNow(project);
                return;
            }

            // 폴백: 바닐라 MainTabWindow_Research.DoBeginResearch와 동일한 흐름
            SoundDefOf.ResearchStart.PlayOneShotOnCamera();
            Find.ResearchManager.SetCurrentProject(project);
            TutorSystem.Notify_Event("StartResearchProject");
            ResearchNode.InvalidateAllStates();
        }

        /// <summary>
        /// Draws a navigation button with glassmorphism style.
        /// </summary>
        private bool DrawNavButton(Rect rect, string label, bool enabled)
        {
            Color bgColor = enabled ? new Color(0.15f, 0.18f, 0.25f, 0.9f) : new Color(0.1f, 0.1f, 0.12f, 0.5f);
            Color borderColor = enabled ? Constraints.PanelBorder : new Color(0.15f, 0.15f, 0.18f);

            Widgets.DrawBoxSolid(rect, bgColor);
            GUIDrawingUtilities.DrawBorderLines(rect, borderColor, 1f);

            if (enabled && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            using (Temporary.Alpha(enabled ? 1f : 0.3f))
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(rect, label);
            }

            bool clicked = enabled && Widgets.ButtonInvisible(rect);
            if (clicked) SoundDefOf.Click.PlayOneShotOnCamera();
            return clicked;
        }

        /// <summary>
        /// Draws a section header with count badge.
        /// </summary>
        private float DrawSectionHeader(Rect rect, string title, int count, Color accentColor)
        {
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(accentColor))
            {
                Widgets.Label(rect, title);
            }

            // Count badge (우측 끝 정렬)
            Rect countRect = new Rect(rect.xMax - 24, rect.y + 2, 24, 18);
            Widgets.DrawBoxSolid(countRect, GenColor.WithAlpha(accentColor, 0.2f));
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(countRect, count.ToString());
            }

            return rect.height + 4;
        }

        /// <summary>
        /// Draws a section divider line.
        /// </summary>
        private void DrawSectionDivider(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.2f, 0.25f, 0.35f, 0.5f));
        }

        /// <summary>
        /// Draws a research item as a card with icon area and era accent.
        /// </summary>
        private float DrawResearchCard(Rect rect, ResearchNode node)
        {
            Color eraColor = node.EraAccentColor;
            Color bgColor;
            Color textColor = Color.white;

            switch (node.State)
            {
                case ResearchNodeState.Completed:
                    bgColor = new Color(0.15f, 0.25f, 0.15f, 0.7f);
                    break;
                case ResearchNodeState.Available:
                    bgColor = new Color(0.2f, 0.2f, 0.1f, 0.7f);
                    break;
                case ResearchNodeState.InProgress:
                    bgColor = new Color(0.1f, 0.2f, 0.25f, 0.7f);
                    break;
                default:
                    bgColor = new Color(0.12f, 0.12f, 0.15f, 0.6f);
                    textColor = new Color(0.6f, 0.6f, 0.65f);
                    break;
            }

            // Card background
            Widgets.DrawBoxSolid(rect, bgColor);

            // Hover effect
            bool isHovered = Mouse.IsOver(rect);
            if (isHovered)
            {
                Widgets.DrawHighlight(rect);
            }

            // Status icon area (left)
            float iconSize = 20f;
            Rect iconRect = new Rect(rect.x + 8, rect.center.y - iconSize / 2, iconSize, iconSize);

            // Draw status indicator
            switch (node.State)
            {
                case ResearchNodeState.Completed:
                    GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconCheck, GenColor.WithAlpha(eraColor, 0.9f));
                    break;
                case ResearchNodeState.Locked:
                    GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconLock, GenColor.WithAlpha(Constraints.ButtonDisabledText, 0.8f));
                    break;
                case ResearchNodeState.InProgress:
                    GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconPlay, eraColor);
                    break;
                case ResearchNodeState.Available:
                default:
                    GUIDrawingUtilities.DrawIconPlaceholder(iconRect, GenColor.WithAlpha(eraColor, 0.6f));
                    break;
            }

            // Research name
            Rect labelRect = new Rect(rect.x + 34, rect.y, rect.width - 40, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(textColor))
            {
                Widgets.Label(labelRect, node.Label);
            }

            // Tooltip (미발견 연구는 설명 마스킹)
            if (isHovered && !node.IsHidden)
            {
                TooltipHandler.TipRegion(rect, node.Def.description);
            }

            // Click event (미발견 연구는 선택/점프 불가)
            if (Widgets.ButtonInvisible(rect) && !node.IsHidden)
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                if (!history.HasCurrent || history.Current != node)
                {
                    history.Push(node);
                    leftPanelScrollPosition = Vector2.zero;
                }

                // 다른 그래프의 연구면 해당 그래프로 전환 후 포커스
                if (!node.Key.Equals(CurrentKey))
                {
                    SwitchGraph(node.Key, playSound: false); // 카드 클릭 Click으로 통일
                }
                FocusOnNode(node);
            }

            return rect.height + 4;
        }

        /// <summary>
        /// 요구 작업대/시설 섹션. 완료 전, 특수 작업대(requiredResearchBuilding)나 시설이 있을 때만 표시.
        /// 충족 여부(PlayerHasAnyAppropriateResearchBench)로 색을 칠하고, 각 항목은 InfoCard로 연결.
        /// 반환값은 사용한 세로 높이 (MeasureRequirementsHeight와 일치해야 함).
        /// </summary>
        private float DrawRequirementsSection(ResearchNode node, float padding, float y, float contentWidth)
        {
            var def = node.Def;
            if (def.IsFinished) return 0f;

            var bench = def.requiredResearchBuilding;
            var facilities = def.requiredResearchFacilities;
            bool hasFacilities = facilities != null && facilities.Count > 0;
            if (bench == null && !hasFacilities) return 0f;

            float startY = y;
            bool met = def.PlayerHasAnyAppropriateResearchBench;
            Color statusColor = met ? new Color(0.6f, 1f, 0.6f) : new Color(0.95f, 0.8f, 0.35f);

            // 헤더 + 상태 아이콘(체크/잠금)
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.7f, 0.78f, 0.9f)))
            {
                Widgets.Label(new Rect(padding, y, contentWidth - 20f, 22), "YART_Requires".Translate());
            }
            GUIDrawingUtilities.DrawIcon(new Rect(padding + contentWidth - 18f, y + 3f, 14f, 14f),
                met ? Assets.IconCheck : Assets.IconLock, statusColor);
            y += 24f;

            if (bench != null)
            {
                y += DrawRequirementRow(new Rect(padding, y, contentWidth, 28f), bench, statusColor);
            }
            if (hasFacilities)
            {
                foreach (var f in facilities)
                {
                    y += DrawRequirementRow(new Rect(padding, y, contentWidth, 28f), f, statusColor);
                }
            }
            y += 6f;
            return y - startY;
        }

        /// <summary>요구 작업대/시설 한 항목 (아이콘 + 라벨 + InfoCard 클릭). 반환값 = 행 높이+간격.</summary>
        private float DrawRequirementRow(Rect rect, ThingDef thing, Color labelColor)
        {
            bool hovered = Mouse.IsOver(rect);
            if (hovered) Widgets.DrawHighlight(rect);

            const float iconSize = 22f;
            Rect iconRect = new Rect(rect.x + 8f, rect.center.y - iconSize / 2f, iconSize, iconSize);
            Widgets.DefIcon(iconRect, thing, null, 1f, null, drawPlaceholder: true);

            Rect labelRect = new Rect(iconRect.xMax + 8f, rect.y, rect.width - iconSize - 24f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(labelColor))
            {
                Widgets.Label(labelRect, thing.LabelCap);
            }

            if (hovered)
            {
                TooltipHandler.TipRegion(rect, thing.description.NullOrEmpty() ? (string)thing.LabelCap : thing.description);
            }
            if (Widgets.ButtonInvisible(rect))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                Find.WindowStack.Add(new Dialog_InfoCard(thing));
            }
            return rect.height + 4f;
        }

        /// <summary>DrawRequirementsSection의 높이 측정 (CalculateLeftPanelHeight 전용).</summary>
        private static float MeasureRequirementsHeight(ResearchNode node)
        {
            var def = node.Def;
            if (def.IsFinished) return 0f;
            var bench = def.requiredResearchBuilding;
            var facilities = def.requiredResearchFacilities;
            int rows = (bench != null ? 1 : 0) + (facilities != null ? facilities.Count : 0);
            if (rows == 0) return 0f;
            return 24f + rows * 32f + 6f; // 헤더 + 행(28+4) + 하단 여백
        }

        private float DrawTechprintSourcesSection(ResearchProjectDef def, float padding, float y, float contentWidth)
        {
            float startY = y;

            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.7f, 0.78f, 0.9f)))
            {
                Widgets.Label(new Rect(padding, y, contentWidth, 22), "ResearchTechprintsFromFactions".Translate());
            }
            y += 24f;

            const float rowH = 26f;
            foreach (var faction in TechprintFactions(def))
            {
                Rect iconRect = new Rect(padding + 6f, y + (rowH - 22f) / 2f, 22f, 22f);
                FactionUIUtility.DrawFactionIconWithTooltip(iconRect, faction);
                using (Temporary.Font(GameFont.Small))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                using (Temporary.Color(new Color(0.8f, 0.82f, 0.88f)))
                {
                    Widgets.Label(new Rect(iconRect.xMax + 6f, y, contentWidth - 34f, rowH), faction.Name);
                }
                y += rowH;
            }
            y += 4f;
            return y - startY;
        }

        /// <summary>DrawTechprintSourcesSection의 높이 측정 (CalculateLeftPanelHeight 전용).</summary>
        private static float MeasureTechprintSourcesHeight(ResearchProjectDef def)
        {
            float h = 24f; // "...FromFactions" 헤더
            foreach (var _ in TechprintFactions(def)) h += 26f; // 세력 행
            return h + 4f;
        }

        /// <summary>테크프린트를 보유/판매하는 세력 (바닐라 MainTabWindow_Research.DrawTechprintInfo와 동일 판정).</summary>
        private static IEnumerable<Faction> TechprintFactions(ResearchProjectDef def)
        {
            if (def.heldByFactionCategoryTags == null) yield break;
            foreach (var tag in def.heldByFactionCategoryTags)
            {
                foreach (var faction in Find.FactionManager.AllFactionsInViewOrder)
                {
                    if (faction.def.categoryTag == tag) yield return faction;
                }
            }
        }

        /// <summary>
        /// 좌측 패널의 전체 높이 계산 (스크롤용)
        /// </summary>
        private float CalculateLeftPanelHeight(ResearchNode node, float contentWidth)
        {
            float height = 0;

            // 제목
            height += 32;

            // Tech Level Badge
            height += 28;

            // 설명
            using (Temporary.Font(GameFont.Small))
            {
                height += Text.CalcHeight(node.Def.description, contentWidth) + 12;
            }

            // 비용
            height += 28;

            // 비용 계수 경고 (DoLeftRect와 동일 조건/폭/폰트)
            float costFactor = node.Def.CostFactor(Faction.OfPlayer.def.techLevel);
            if (costFactor != 1f && !node.Def.IsFinished)
            {
                string costWarn = "TechLevelTooLow".Translate(Faction.OfPlayer.def.techLevel.ToStringHuman(),
                    node.Def.techLevel.ToStringHuman(), (1f / costFactor).ToStringPercent())
                    + " " + "ResearchCostComparison".Translate(
                        node.Def.Cost.ToString("F0"), node.Def.CostApparent.ToString("F0"));
                using (Temporary.Font(GameFont.Tiny))
                {
                    height += Text.CalcHeight(costWarn, contentWidth) + 6;
                }
            }

            // 요구 작업대/시설 (DrawRequirementsSection과 동일 위치/높이)
            height += MeasureRequirementsHeight(node);

            // 잠금 사유 (DrawLeftPanel의 렌더링과 동일한 계산이어야 함)
            if (node.State == ResearchNodeState.Locked)
            {
                node.GetLockedReasons(lockedReasonsBuffer);
                if (lockedReasonsBuffer.Count > 0)
                {
                    height += 22; // "Locked:" 헤더
                    using (Temporary.Font(GameFont.Small))
                    {
                        foreach (var reason in lockedReasonsBuffer)
                        {
                            height += Text.CalcHeight("  • " + reason, contentWidth) + 2;
                        }
                    }
                    height += 8;
                }
            }

            // 테크프린트 구입처 보충 (DoLeftRect와 동일 조건/구성)
            if (node.Def.TechprintCount > 0 && !node.Def.TechprintRequirementMet)
            {
                height += MeasureTechprintSourcesHeight(node.Def);
                if (Prefs.DevMode) height += 30;
                height += 4;
            }

            // 연구 버튼
            height += 45;

            // 구분선
            height += 12;

            // Unlocked Content
            height += 29; // Header
            var unlocked = node.UnlockedDefs;
            if (unlocked.Count == 0)
            {
                height += 26;
            }
            else if (YARTMod.Settings.unlockedContentExpanded)
            {
                // 2-column(아이콘+라벨) — 그리기와 동일한 행 높이
                const float rowH = 30f, rowGap = 2f;
                int rows = Mathf.CeilToInt(unlocked.Count / 2f);
                height += rows * (rowH + rowGap);
            }
            else
            {
                float iconSize = 32f;
                float gap = 6f;
                int iconsPerRow = Mathf.FloorToInt((contentWidth + gap) / (iconSize + gap));
                if (iconsPerRow < 1) iconsPerRow = 1;

                int rows = Mathf.CeilToInt((float)unlocked.Count / iconsPerRow);
                height += rows * (iconSize + gap);
            }
            height += 8;

            // 구분선
            height += 12;

            // 선행 연구 헤더
            height += 29;

            var prereqs = node.PanelPrerequisites ?? emptyNodeList;
            height += prereqs.Count > 0 ? prereqs.Count * 40 : 26;  // Card height is 36 + 4 spacing
            height += 8;

            // 구분선
            height += 12;

            // 후행 연구 헤더
            height += 29;

            var children = node.PanelChildren ?? emptyNodeList;
            height += children.Count > 0 ? children.Count * 40 : 26;

            // 여유 공간
            height += 60;

            return height;
        }
    }
}
