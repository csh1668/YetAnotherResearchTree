using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;

namespace YART
{
    // 캔버스 상호작용 의미론: 노드 클릭(선택/큐/프록시 점프), 호버 포커스 모드
    public partial class MainTabWindow_YART
    {
        private void HandleNodeInteraction(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // HandleInput에서 "드래그 없는 좌클릭"으로 판별된 경우에만 진입 (MouseUp 시점)
            if (!clickPending) return;
            clickPending = false;
            if (IsMouseOverBlockingElement()) return;

            Vector2 mousePos = Event.current.mousePosition;

            // Reverse iteration to handle draw order (though nodes rarely overlap)
            // We only check visible nodes for performance
            foreach (var node in graph.Nodes)
            {
                if (node.IsDummy) continue;
                if (!visibleRect.Overlaps(node.Rect)) continue;

                Rect nodeRectScreen = GetNodeScreenRect(node, offset, zoom);

                if (nodeRectScreen.Contains(mousePos))
                {
                    var target = node;
                    if (node.IsProxy)
                    {
                        target = node.OriginalNode;
                    }

                    // 미발견(IsHidden) 연구는 바닐라처럼 상호작용 불가 (정보 마스킹)
                    if (target.IsHidden)
                    {
                        Event.current.Use();
                        return;
                    }

                    // Alt+클릭 = 큐 맨 앞에 삽입 (진행 중 연구를 밀어내고 먼저 연구).
                    // Available일 때만 큐잉 가능 — 좌측 패널과 동일(Locked는 불가, 거부음).
                    if (Event.current.alt)
                    {
                        var mgr = ResearchQueueManager.Instance;
                        if (mgr != null && target.State == ResearchNodeState.Available)
                            mgr.EnqueueWithChainToFront(target.Def); // 사운드는 EnqueueWithChainToFront 내부(ResearchStart)
                        else
                            SoundDefOf.ClickReject.PlayOneShotOnCamera();
                        Event.current.Use();
                        return;
                    }

                    // Shift+클릭 = 큐 끝에 추가 (미완료 선행 체인 포함). Available일 때만.
                    if (Event.current.shift)
                    {
                        var mgr = ResearchQueueManager.Instance;
                        if (mgr != null && target.State == ResearchNodeState.Available)
                            mgr.EnqueueWithChain(target.Def); // 사운드는 EnqueueWithChain 내부(ResearchStart)
                        else
                            SoundDefOf.ClickReject.PlayOneShotOnCamera();
                        Event.current.Use();
                        return;
                    }

                    if (!history.HasCurrent || history.Current != target)
                    {
                        history.Push(target);
                        leftPanelScrollPosition = Vector2.zero;
                        SoundDefOf.Click.PlayOneShotOnCamera(); // 노드 선택 (바닐라 연구 목록과 동일)
                    }

                    // 프록시 클릭 = 원본 노드가 속한 그래프로 전환 + 포커스
                    if (node.IsProxy)
                    {
                        SwitchGraph(target.Key, playSound: false); // 노드 선택 Click으로 통일
                        FocusOnNode(target);
                    }

                    Event.current.Use();
                    return; // Handled one click
                }
            }

            // 빈 곳 클릭: 검색 드롭다운 → 좌측 패널 순으로 닫기
            if (searchResultsRect.height > 0f)
            {
                searchDropdownClosed = true;
                searchResultsRect = Rect.zero;
                GUI.FocusControl(null); // Help/자동완성 패널은 포커스 조건 — 외부 클릭 시 닫히도록 해제
                Event.current.Use();
            }
            else if (history.HasCurrent)
            {
                history.Clear();
                Event.current.Use();
            }
        }

        /// <summary>
        /// 노드 우클릭 컨텍스트 메뉴: Start now / Stop / Queue / Unqueue / 원본 그래프로 / 인포 카드.
        /// 좌측 패널 버튼과 동일한 의미론 (시작·중단은 바닐라 API 경유).
        /// </summary>
        private void HandleNodeContextMenu(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            if (!rightClickPending) return;
            rightClickPending = false;
            if (IsMouseOverBlockingElement()) return;

            Vector2 mousePos = Event.current.mousePosition;
            foreach (var node in graph.Nodes)
            {
                if (node.IsDummy) continue;
                if (!visibleRect.Overlaps(node.Rect)) continue;
                if (!GetNodeScreenRect(node, offset, zoom).Contains(mousePos)) continue;

                var target = node.IsProxy ? node.OriginalNode : node;
                if (target.IsHidden) return; // 미발견 연구는 메뉴 없음 (정보 마스킹)

                // 해금 아이콘 우클릭 = 해당 def InfoCard (대표 아이콘 또는 리치 스트립/"+n").
                // 노드 컨텍스트 메뉴보다 우선 — 아이콘 위가 아니면 아래로 흘러 일반 메뉴.
                if (zoom >= Constraints.NodeTextMinZoom && TryHandleUnlockIconRightClick(target, GetNodeScreenRect(node, offset, zoom), zoom, mousePos))
                {
                    Event.current.Use();
                    return;
                }

                var def = target.Def;
                var queueMgr = ResearchQueueManager.Instance;
                var options = new List<FloatMenuOption>();

                if (target.State == ResearchNodeState.Available && def.CanStartNow)
                {
                    options.Add(new FloatMenuOption("YART_StartNow".Translate(), () => StartResearch(def)));
                }

                int queuePos = queueMgr?.GetQueuePosition(def) ?? -1;
                if (target.State == ResearchNodeState.InProgress)
                {
                    options.Add(new FloatMenuOption("YART_Stop".Translate(), () =>
                    {
                        if (queueMgr != null) queueMgr.Remove(def);
                        else Find.ResearchManager.StopProject(def);
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                    }));
                }
                else if (queuePos > 0)
                {
                    options.Add(new FloatMenuOption("YART_Unqueue".Translate(queuePos), () =>
                    {
                        queueMgr.Remove(def);
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                    }));
                }
                else if (queueMgr != null && target.State == ResearchNodeState.Available)
                {
                    // Available일 때만 큐잉 (Locked는 불가 — 좌측 패널·Shift+클릭과 동일)
                    int chainCount = ResearchQueueManager.CollectMissingChain(def).Count;
                    options.Add(new FloatMenuOption(
                        chainCount > 1 ? "YART_QueueChain".Translate(chainCount) : "YART_Queue".Translate(),
                        () => queueMgr.EnqueueWithChain(def))); // 사운드는 EnqueueWithChain 내부
                }

                if (node.IsProxy)
                {
                    options.Add(new FloatMenuOption("YART_GoToOriginal".Translate(), () => JumpToNode(target)));
                }
                options.Add(new FloatMenuOption("YART_ShowInfoCard".Translate(), () => Find.WindowStack.Add(new Dialog_InfoCard(def))));
                // 외부 후행 점프는 배지 호버 팝업으로 이동 (DrawExternalDependentsPopup)

                Find.WindowStack.Add(new FloatMenu(options));
                Event.current.Use();
                return;
            }
        }

        /// <summary>
        /// 리치카드 해금 아이콘 영역 우클릭 처리. 아이콘 위면 그 def의 InfoCard를,
        /// "+n" 오버플로 위면 전체 해금 목록 FloatMenu를 연다. 처리하면 true.
        /// 좌표 기하는 DrawRichCardContent와 공유(GetRichCardIconMetrics).
        /// </summary>
        private bool TryHandleUnlockIconRightClick(ResearchNode realNode, Rect nodeRectScreen, float zoom, Vector2 mousePos)
        {
            var unlocked = realNode.UnlockedDefs;
            if (unlocked.Count == 0) return false;

            // 대표 아이콘 (좌측, NodeTextMinZoom 이상에서 표시 — 렌더와 동일 좌표)
            if (RepIconRect(nodeRectScreen, zoom).Contains(mousePos))
            {
                Find.WindowStack.Add(new Dialog_InfoCard(unlocked[0]));
                return true;
            }

            // 리치 스트립 (리치 카드에서만; 대표 제외 index 1.. 최대 8개)
            if (GetRichCardLerp(zoom) > 0f && unlocked.Count > 1)
            {
                GetRichCardIconMetrics(nodeRectScreen, zoom, hasRep: true,
                    out float iconSize, out float iconGap, out float iconX, out float iconY);
                int afterRep = unlocked.Count - 1;
                int shown = Mathf.Min(RichCardStripMax, afterRep);

                for (int k = 0; k < shown; k++)
                {
                    Rect iconRect = new Rect(iconX + k * (iconSize + iconGap), iconY, iconSize, iconSize);
                    if (iconRect.Contains(mousePos))
                    {
                        Find.WindowStack.Add(new Dialog_InfoCard(unlocked[1 + k]));
                        return true;
                    }
                }

                if (afterRep > shown)
                {
                    Rect moreRect = new Rect(iconX + shown * (iconSize + iconGap), iconY, 30f * zoom, iconSize);
                    if (moreRect.Contains(mousePos))
                    {
                        var opts = new List<FloatMenuOption>();
                        for (int j = 1 + shown; j < unlocked.Count; j++)
                        {
                            var local = unlocked[j];
                            opts.Add(new FloatMenuOption(local.LabelCap, () => Find.WindowStack.Add(new Dialog_InfoCard(local))));
                        }
                        Find.WindowStack.Add(new FloatMenu(opts));
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Updates focus mode state based on which node is being hovered.
        /// When a node is hovered, only connected nodes and edges are highlighted.
        /// </summary>
        private void UpdateFocusMode(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // Disable focus mode at low zoom for performance
            if (zoom < 0.4f)
            {
                hoveredNode = null;
                focusedNodes.Clear();
                return;
            }

            // Find hovered node
            Vector2 mousePos = Event.current.mousePosition;

            // 큐 바 카드 호버 시 해당 노드를 포커스 대상으로 사용
            ResearchNode newHoveredNode = queueBarHoveredDef != null ? GetNodeOnCurrentGraph(queueBarHoveredDef) : null;

            // Don't activate focus mode when mouse is over panels
            if (newHoveredNode == null && !IsMouseOverBlockingElement())
            {
                foreach (var node in graph.Nodes)
                {
                    if (node.IsDummy) continue;

                    // Optimization: Check culling first
                    if (!visibleRect.Overlaps(node.Rect)) continue;

                    if (GetNodeScreenRect(node, offset, zoom).Contains(mousePos))
                    {
                        newHoveredNode = node;
                        break;
                    }
                }
            }

            // 호버가 없으면 선택된 노드(좌측 패널)가 포커스 앵커 — 하이라이트가 계속 지속된다.
            // 우선순위: 실제 호버 > 선택 노드 > 검색 매칭(디밍)
            if (newHoveredNode == null && history.HasCurrent)
            {
                newHoveredNode = GetNodeOnCurrentGraph(history.Current.Def);
            }

            // Only rebuild focused sets if hovered node changed
            if (newHoveredNode != hoveredNode)
            {
                hoveredNode = newHoveredNode;
                focusedNodes.Clear();

                if (hoveredNode != null)
                {
                    // Add the hovered node itself
                    focusedNodes.Add(hoveredNode);

                    // Collect all connected nodes and edges (including through dummy nodes)
                    // Upstream: 선행 체인 전부 (완료 여부 무관 — 전체 의존 경로를 보여준다)
                    ResearchNode.CollectConnectedNodes(hoveredNode, focusedNodes, true, filter: null);

                    // Downstream: Direct descendants only (traverse dummies, stop at first real node)
                    // Filter: null (collect everything encountered)
                    // Recurse: x.IsDummy (only recurse if it's a dummy node)
                    ResearchNode.CollectConnectedNodes(hoveredNode, focusedNodes, false, filter: null, recurseCondition: x => x.IsDummy);
                }
            }
        }
    }
}
