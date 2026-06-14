using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Compat;

namespace YART.Data
{
    public class ResearchEdge
    {
        /// <summary>
        /// 시작 노드 (선행 연구)
        /// </summary>
        public ResearchNode From { get; }

        /// <summary>
        /// 끝 노드 (후속 연구)
        /// </summary>
        public ResearchNode To { get; }

        public ResearchEdge(ResearchNode from, ResearchNode to)
        {
            From = from;
            To = to;
        }
    }

    /// <summary>
    /// SugiyamaLayout.Calculate 가 수집하는 레이아웃 품질 지표.
    /// </summary>
    public class LayoutMetrics
    // public record LayoutMetrics
    {
        /// <summary>ordering 종료 시점의 인접 레이어 원시 교차 수</summary>
        public int Crossings;

        /// <summary>NormalizeEdges 이전 실제-실제 의존 엣지의 rank span 합</summary>
        public int TotalEdgeSpan;

        /// <summary>더미 체인 Y 프로파일의 방향 전환 총 수</summary>
        public int BendCount;

        /// <summary>더미 체인 전체의 |delta Y| 절대합 (단위: px)</summary>
        public float VerticalVariation;

        /// <summary>ShapeChains 비수렴 체인 수</summary>
        public int NonConvergedChains;

        /// <summary>
        /// 막힘으로 정당화되지 않는 더미 체인 방향 전환 수
        /// </summary>
        public int UnjustifiedBends;

        /// <summary>가장 높은 컬럼의 픽셀 범위 (max − min, 단위: px)</summary>
        public float MaxColumnHeight;

        /// <summary>
        /// 예산 초과 컬럼의 제곱합 비용
        /// </summary>
        public float OverflowCost;

        /// <summary>
        /// 비고립 노드(isolatedNodesSet 제외)를 포함하는 서로 다른 랭크 수.
        /// 레이아웃 가로 폭을 랭크 단위로 나타낸다.
        /// </summary>
        public int UsedRankCount;

        /// <summary>
        /// Phase 4 전역 rank balancing의 목적함수.
        /// RankCost = OverflowCost + RankCostSpanWeight × TotalEdgeSpan + RankCostWidthWeight × UsedRankCount
        /// </summary>
        public float RankCost;
    }

    /// <summary>
    /// 특정 구역(Region)의 연구 서브 그래프를 관리합니다.
    /// </summary>
    public class ResearchSubGraph
    {
        /// <summary>이 서브 그래프의 식별 키 (트랙 + 탭)</summary>
        public GraphKey Key { get; }

        /// <summary>이 서브 그래프가 속한 병렬 연구 채널</summary>
        public ResearchChannel Channel => Key.Channel;

        /// <summary>이 서브 그래프의 바닐라 연구 탭 (벤치 채널 전용, 그 외 null)</summary>
        public ResearchTabDef Tab => Key.Tab;

        /// <summary>
        /// 이 구역에 속한 모든 노드 (프록시 포함)
        /// </summary>
        public List<ResearchNode> Nodes { get; } = new List<ResearchNode>();

        /// <summary>
        /// 이 구역 내의 모든 엣지
        /// </summary>
        public List<ResearchEdge> Edges { get; } = new List<ResearchEdge>();

        /// <summary>
        /// 그래프의 전체 바운딩 박스 (렌더링 영역)
        /// </summary>
        public Rect BoundingBox { get; private set; }

        /// <summary>
        /// 각 TechLevel이 시작되는 X 좌표 (구분선 그리기용)
        /// </summary>
        public Dictionary<TechLevel, float> TechLevelBoundaries { get; set; } = new Dictionary<TechLevel, float>();

        public LayoutMetrics Metrics { get; internal set; }

        public ResearchSubGraph(GraphKey key)
        {
            Key = key;
        }

        public void UpdateBoundingBox()
        {
            if (Nodes.Count == 0)
            {
                BoundingBox = Rect.zero;
                return;
            }

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var node in Nodes)
            {
                var rect = node.Rect;
                minX = Mathf.Min(minX, rect.xMin);
                minY = Mathf.Min(minY, rect.yMin);
                maxX = Mathf.Max(maxX, rect.xMax);
                maxY = Mathf.Max(maxY, rect.yMax);
            }

            BoundingBox = new Rect(minX - Constraints.BoundingBoxPadding.x,
                minY - Constraints.BoundingBoxPadding.y,
                (maxX - minX) + 2 * Constraints.BoundingBoxPadding.x,
                (maxY - minY) + 2 * Constraints.BoundingBoxPadding.y);
        }

        public void AddNode(ResearchNode node)
        {
            if (!Nodes.Contains(node))
            {
                Nodes.Add(node);
            }
        }

        public void AddEdge(ResearchNode from, ResearchNode to)
        {
            var edge = new ResearchEdge(from, to);
            Edges.Add(edge);
        }
    }

    /// <summary>
    /// 전체 연구 그래프를 관리하는 메인 컨테이너입니다.
    /// </summary>
    public class ResearchGraph
    {
        private static ResearchGraph _instance;
        public static ResearchGraph Instance => _instance ?? (_instance = new ResearchGraph());

        public Dictionary<GraphKey, ResearchSubGraph> SubGraphs { get; private set; } = new Dictionary<GraphKey, ResearchSubGraph>();
        public Dictionary<ResearchProjectDef, ResearchNode> AllNodes { get; private set; } = new Dictionary<ResearchProjectDef, ResearchNode>();

        private readonly Dictionary<GraphKey, Dictionary<ResearchProjectDef, ResearchNode>> _proxyCache = new Dictionary<GraphKey, Dictionary<ResearchProjectDef, ResearchNode>>();
        private volatile bool _initialized = false;

        // 통합 벤치 뷰
        private ResearchSubGraph _unifiedSubGraph;
        private Dictionary<ResearchProjectDef, ResearchNode> _unifiedNodeByDef;

        public bool Initialized => _initialized;

        private ResearchSubGraph GetOrAddSubGraph(GraphKey key)
        {
            if (!SubGraphs.TryGetValue(key, out var sub))
            {
                sub = new ResearchSubGraph(key);
                SubGraphs[key] = sub;
                _proxyCache[key] = new Dictionary<ResearchProjectDef, ResearchNode>();
            }
            return sub;
        }

        public void Build()
        {
            try
            {
                if (Prefs.DevMode) Log.Message("[YART] Building Research Graph...");
                Clear();

                // 1. 모든 ResearchProjectDef에 대해 ResearchNode 생성
                CreateNodes();

                // 2. 노드 간 연결 및 서브 그래프 할당
                BuildConnections();

                // 2.5 사이클 제거 (레이아웃 엔진 무한루프/폭주 방지)
                RemoveCycles();

                // 3. 중복된 선행 연구 제거 (Transitive Reduction)
                RemoveRedundantPrerequisites();

                // 4. 좌측 패널용 선행/후행 목록 사전 계산
                BuildPanelLists();

                _initialized = true;
                if (Prefs.DevMode) Log.Message($"[YART] Graph built. Total nodes: {AllNodes.Count}");
            }
            catch (Exception ex)
            {
                Log.Error($"[YART] Failed to build research graph: {ex}");
                _initialized = false;
            }
        }

        private void RemoveCycles()
        {
            foreach (var subGraph in SubGraphs.Values)
            {
                // DFS를 위한 상태 추적
                var visited = new HashSet<ResearchNode>();
                var recursionStack = new HashSet<ResearchNode>();

                // 서브 그래프의 모든 노드에 대해 DFS 수행 (비연결 컴포넌트 고려)
                var nodes = subGraph.Nodes.ToList();
                foreach (var node in nodes)
                {
                    if (!visited.Contains(node))
                    {
                        DetectAndRemoveCycle(node, visited, recursionStack, subGraph);
                    }
                }
            }
        }

        private static void DetectAndRemoveCycle(ResearchNode node, HashSet<ResearchNode> visited, HashSet<ResearchNode> recursionStack, ResearchSubGraph subGraph)
        {
            visited.Add(node);
            recursionStack.Add(node);

            // 자식 노드들을 순회
            var children = node.Children.ToList();
            foreach (var child in children)
            {
                if (!visited.Contains(child))
                {
                    DetectAndRemoveCycle(child, visited, recursionStack, subGraph);
                }
                else if (recursionStack.Contains(child))
                {
                    Log.Warning($"[YART] Cycle detected: {node.Label} -> {child.Label}. Breaking connection.");

                    node.Children.Remove(child);
                    child.Prerequisites.Remove(node);

                    // 엣지 리스트에서도 제거
                    var edge = subGraph.Edges.FirstOrDefault(e => e.From == node && e.To == child);
                    if (edge != null)
                    {
                        subGraph.Edges.Remove(edge);
                    }
                }
            }

            recursionStack.Remove(node);
        }

        private void Clear()
        {
            AllNodes.Clear();
            SubGraphs.Clear();
            _proxyCache.Clear();
            _initialized = false;
            _unifiedSubGraph = null;
            _unifiedNodeByDef = null;
        }

        private void CreateNodes()
        {
            var allDefs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            foreach (var def in allDefs)
            {
                // World Tech Level: 월드 테크레벨 초과 연구를 그래프에서 제외
                // (WTL이 바닐라 연구 목록에서 RemoveAll 하는 것과 동일 — 미설치 시 항상 true)
                if (!WorldTechLevelCompat.ShouldShow(def)) continue;

                var node = ResearchNode.Create(def);
                AllNodes[def] = node;

                GetOrAddSubGraph(node.Key).AddNode(node);
            }
            // 이번 빌드에 사용한 필터 시그니처를 기록 (필터 레벨이 바뀌면 PreOpen에서 리빌드)
            WorldTechLevelCompat.MarkBuilt();
        }

        private void BuildConnections()
        {
            foreach (var node in AllNodes.Values)
            {
                var def = node.Def;

                // 명시적 선행 연구 (prerequisites)만 연결
                // 바닐라와 동일하게 hiddenPrerequisites는 그래프에 연결하지 않음
                if (def.prerequisites != null)
                {
                    foreach (var prereqDef in def.prerequisites)
                    {
                        if (AllNodes.TryGetValue(prereqDef, out var prereqNode))
                        {
                            ConnectNodes(prereqNode, node);
                        }
                    }
                }
            }
        }

        private void ConnectNodes(ResearchNode from, ResearchNode to)
        {
            // 같은 서브그래프(트랙+탭)인 경우
            if (from.Key.Equals(to.Key))
            {
                if (to.Prerequisites.Contains(from)) return;

                from.Children.Add(to);
                to.Prerequisites.Add(from);

                SubGraphs[from.Key].AddEdge(from, to);
            }
            // 다른 서브그래프인 경우 (프록시 생성 필요) — 교차 트랙뿐 아니라 교차 탭도 동일 처리
            else
            {
                var proxy = GetOrCreateProxy(from, to.Key);

                if (to.Prerequisites.Contains(proxy)) return;

                proxy.Children.Add(to);
                to.Prerequisites.Add(proxy);

                SubGraphs[to.Key].AddEdge(proxy, to);
            }
        }

        /// <summary>
        /// A -> B, B -> C, A -> C 인 경우 A -> C 연결을 제거
        /// </summary>
        private void RemoveRedundantPrerequisites()
        {
            foreach (var subGraph in SubGraphs.Values)
            {
                RemoveRedundantPrerequisitesForSubGraph(subGraph);
            }
        }

        private void BuildPanelLists()
        {
            foreach (var node in AllNodes.Values)
            {
                node.PanelPrerequisites = new List<ResearchNode>();
                node.PanelChildren = new List<ResearchNode>();
            }
            foreach (var node in AllNodes.Values)
            {
                AddPanelPrereqs(node, node.Def.prerequisites);
                AddPanelPrereqs(node, node.Def.hiddenPrerequisites);
            }
            foreach (var node in AllNodes.Values)
            {
                node.PanelPrerequisites = node.PanelPrerequisites.OrderBy(n => n.TechLevel).ThenBy(n => n.Label).ToList();
                node.PanelChildren = node.PanelChildren.OrderBy(n => n.TechLevel).ThenBy(n => n.Label).ToList();

                node.ExternalChildren = node.PanelChildren
                    .Where(c => !c.Key.Equals(node.Key) && HasSurvivingExternalEdge(node, c))
                    .ToList();
            }
        }

        /// <summary>
        /// node → child(다른 그래프) 의존이 배지 목록에 표시되어야 하는지.
        /// </summary>
        private static bool HasSurvivingExternalEdge(ResearchNode node, ResearchNode child)
        {
            for (int i = 0; i < child.Prerequisites.Count; i++)
            {
                var p = child.Prerequisites[i];
                if (p.IsProxy && p.OriginalNode == node) return true;
            }
            return child.Def.prerequisites == null || !child.Def.prerequisites.Contains(node.Def);
        }

        private void AddPanelPrereqs(ResearchNode node, List<ResearchProjectDef> prereqDefs)
        {
            if (prereqDefs == null) return;
            foreach (var prereqDef in prereqDefs)
            {
                if (!AllNodes.TryGetValue(prereqDef, out var prereqNode)) continue;
                if (!node.PanelPrerequisites.Contains(prereqNode)) node.PanelPrerequisites.Add(prereqNode);
                if (!prereqNode.PanelChildren.Contains(node)) prereqNode.PanelChildren.Add(node);
            }
        }

        private HashSet<ResearchNode> GetAllAncestors(ResearchNode node, HashSet<ResearchNode> visited = null)
        {
            if (visited == null) visited = new HashSet<ResearchNode>();

            foreach (var parent in node.Prerequisites)
            {
                if (visited.Add(parent))
                {
                    GetAllAncestors(parent, visited);
                }
            }
            return visited;
        }

        private ResearchNode GetOrCreateProxy(ResearchNode original, GraphKey targetKey)
        {
            // 방어적으로 보장
            GetOrAddSubGraph(targetKey);

            var cache = _proxyCache[targetKey];
            if (cache.TryGetValue(original.Def, out var existingProxy))
            {
                return existingProxy;
            }

            var proxy = ResearchNode.CreateProxy(original, targetKey);
            cache[original.Def] = proxy;
            SubGraphs[targetKey].AddNode(proxy);
            return proxy;
        }

        public ResearchNode GetNodeForGraph(ResearchProjectDef def, GraphKey key)
        {
            // 통합 벤치 뷰: 통합 복사본 → 통합 프록시 캐시 순으로 조회
            if (key.IsUnified)
            {
                if (_unifiedNodeByDef != null && _unifiedNodeByDef.TryGetValue(def, out var unifiedCopy))
                    return unifiedCopy;
                if (_proxyCache.TryGetValue(key, out var unifiedCache) && unifiedCache.TryGetValue(def, out var unifiedProxy))
                    return unifiedProxy;
                return null;
            }

            if (AllNodes.TryGetValue(def, out var realNode))
            {
                if (realNode.Key.Equals(key)) return realNode;

                if (_proxyCache.TryGetValue(key, out var cache) && cache.TryGetValue(def, out var proxy))
                {
                    return proxy;
                }
            }
            return null;
        }

        public ResearchSubGraph GetOrBuildUnifiedBench()
        {
            if (!_initialized) return null;
            if (_unifiedSubGraph != null) return _unifiedSubGraph;

            var unifiedKey = GraphKey.UnifiedBench;

            try
            {
                var subGraph = GetOrAddSubGraph(unifiedKey);
                var unifiedNodeByDef = new Dictionary<ResearchProjectDef, ResearchNode>();

                foreach (var kvp in SubGraphs)
                {
                    if (kvp.Key.IsUnified) continue;
                    if (kvp.Key.Channel == null || !kvp.Key.Channel.IsBench) continue;

                    foreach (var node in kvp.Value.Nodes)
                    {
                        if (node.IsDummy || node.IsProxy) continue;

                        var copy = ResearchNode.CreateUnifiedCopy(node);
                        subGraph.AddNode(copy);
                        unifiedNodeByDef[node.Def] = copy;
                    }
                }

                foreach (var copy in unifiedNodeByDef.Values)
                {
                    if (copy.Def.prerequisites == null) continue;

                    foreach (var prereqDef in copy.Def.prerequisites)
                    {
                        if (unifiedNodeByDef.TryGetValue(prereqDef, out var prereqCopy))
                        {
                            ConnectUnifiedNodes(prereqCopy, copy, subGraph);
                        }
                        else if (AllNodes.TryGetValue(prereqDef, out var canonicalNode))
                        {
                            var proxy = GetOrCreateProxy(canonicalNode, unifiedKey);
                            ConnectUnifiedNodes(proxy, copy, subGraph);
                        }
                    }
                }

                {
                    var visited = new HashSet<ResearchNode>();
                    var recursionStack = new HashSet<ResearchNode>();
                    foreach (var node in subGraph.Nodes.ToList())
                    {
                        if (!visited.Contains(node))
                            DetectAndRemoveCycle(node, visited, recursionStack, subGraph);
                    }
                }

                RemoveRedundantPrerequisitesForSubGraph(subGraph);

                BuildPanelListsForUnified(unifiedNodeByDef, subGraph);

                new SugiyamaLayout().Calculate(subGraph);
                ResearchNode.InvalidateAllPorts();
                _unifiedNodeByDef = unifiedNodeByDef;
                _unifiedSubGraph = subGraph;

                if (Prefs.DevMode) Log.Message($"[YART] Unified bench subgraph built: {subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges");
                return _unifiedSubGraph;
            }
            catch (Exception ex)
            {
                Log.Error($"[YART] GetOrBuildUnifiedBench failed, rolling back: {ex}");
                SubGraphs.Remove(unifiedKey);
                _proxyCache.Remove(unifiedKey);
                _unifiedNodeByDef = null;
                _unifiedSubGraph = null;
                return null;
            }
        }

        private static void ConnectUnifiedNodes(ResearchNode from, ResearchNode to, ResearchSubGraph subGraph)
        {
            if (to.Prerequisites.Contains(from)) return;
            from.Children.Add(to);
            to.Prerequisites.Add(from);
            subGraph.AddEdge(from, to);
        }

        /// <summary>
        /// Transitive Reduction을 적용한다.
        /// </summary>
        private void RemoveRedundantPrerequisitesForSubGraph(ResearchSubGraph subGraph)
        {
            foreach (var node in subGraph.Nodes)
            {
                if (node.Prerequisites.Count < 2) continue;

                var toRemove = new HashSet<ResearchNode>();
                var directPrereqs = node.Prerequisites.ToList();
                foreach (var p in directPrereqs)
                {
                    if (toRemove.Contains(p)) continue;

                    var ancestorsOfP = GetAllAncestors(p);
                    foreach (var ancestor in ancestorsOfP)
                    {
                        if (directPrereqs.Contains(ancestor))
                        {
                            toRemove.Add(ancestor);
                        }
                    }
                }

                foreach (var remove in toRemove)
                {
                    node.Prerequisites.Remove(remove);
                    remove.Children.Remove(node);

                    var edge = subGraph.Edges.FirstOrDefault(e => e.From == remove && e.To == node);
                    if (edge != null) subGraph.Edges.Remove(edge);
                }
            }
        }

        private void BuildPanelListsForUnified(
            Dictionary<ResearchProjectDef, ResearchNode> unifiedNodeByDef,
            ResearchSubGraph subGraph)
        {
            foreach (var copy in unifiedNodeByDef.Values)
            {
                copy.PanelPrerequisites = new List<ResearchNode>();
                copy.PanelChildren = new List<ResearchNode>();
            }

            foreach (var copy in unifiedNodeByDef.Values)
            {
                AddPanelPrereqsForUnified(copy, copy.Def.prerequisites, unifiedNodeByDef);
                AddPanelPrereqsForUnified(copy, copy.Def.hiddenPrerequisites, unifiedNodeByDef);
            }

            // 정렬
            foreach (var copy in unifiedNodeByDef.Values)
            {
                copy.PanelPrerequisites = copy.PanelPrerequisites.OrderBy(n => n.TechLevel).ThenBy(n => n.Label).ToList();
                copy.PanelChildren = copy.PanelChildren.OrderBy(n => n.TechLevel).ThenBy(n => n.Label).ToList();

                if (AllNodes.TryGetValue(copy.Def, out var canonical) && canonical.ExternalChildren != null)
                {
                    copy.ExternalChildren = canonical.ExternalChildren
                        .Where(c => c.Channel != null && !c.Channel.IsBench)
                        .ToList();
                }
                else
                {
                    copy.ExternalChildren = new List<ResearchNode>();
                }
            }
        }

        private void AddPanelPrereqsForUnified(
            ResearchNode node,
            List<ResearchProjectDef> prereqDefs,
            Dictionary<ResearchProjectDef, ResearchNode> unifiedNodeByDef)
        {
            if (prereqDefs == null) return;
            foreach (var prereqDef in prereqDefs)
            {
                ResearchNode prereqNode;
                bool isUnifiedCopy = unifiedNodeByDef.TryGetValue(prereqDef, out prereqNode);
                if (!isUnifiedCopy)
                {
                    if (!AllNodes.TryGetValue(prereqDef, out prereqNode)) continue;
                }

                if (!node.PanelPrerequisites.Contains(prereqNode)) node.PanelPrerequisites.Add(prereqNode);

                if (isUnifiedCopy && !prereqNode.PanelChildren.Contains(node)) prereqNode.PanelChildren.Add(node);
            }
        }

        public ResearchSubGraph GetSubGraph(GraphKey key)
        {
            return SubGraphs.TryGetValue(key, out var graph) ? graph : null;
        }

        /// <summary>해당 채널의 서브그래프 중 하나라도 실제(더미/프록시 제외) 노드를 갖는가</summary>
        public bool ChannelHasRealNodes(ResearchChannel channel)
        {
            foreach (var kvp in SubGraphs)
            {
                if (kvp.Key.Channel != channel) continue;
                if (kvp.Value.Nodes.Any(n => !n.IsDummy && !n.IsProxy)) return true;
            }
            return false;
        }
    }
}
