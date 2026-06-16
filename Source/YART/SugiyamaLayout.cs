using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;

namespace YART
{
    /// <summary>
    /// Sugiyama 계층 레이아웃 엔진: 랭크 할당 → 교차 최소화 → X/Y 좌표 배정으로 연구 트리를 배치한다.
    ///
    /// 파이프라인: AssignRanks/TightenRanks/BalanceRanks(랭크) → PlaceProxies → CompactRanks
    /// → NormalizeEdges(더미) → MinimizeCrossings(순서) → AssignX → AssignY → UntangleChains → PlaceIsolated.
    /// </summary>
    public class SugiyamaLayout
    {
        private ResearchSubGraph graph;
        private Dictionary<ResearchNode, TechLevel> effectiveTechLevel;
        private Dictionary<int, List<ResearchNode>> isolatedNodesByRank;
        private HashSet<ResearchNode> isolatedNodesSet;
        private bool graphHasEras; // 실노드 중 하나라도 TechLevel가 정의됐는가
        private int lastNonConvergedChains; // ShapeChains 마지막 호출의 미수렴 체인 수
        private Dictionary<ResearchNode, int> subtreeWeight; // 하위 도달 노드 수 (variant 3 center-out 시드용, lazy)

        // 그래프별 적응형 컬럼 높이 예산 (통합 그래프는 per-rank 분포 기반, per-tab은 기본 936). ComputeColumnBudget에서 설정.
        private float columnBudget;

        public void Calculate(ResearchSubGraph graph)
        {
            if (graph.Nodes.Count == 0) return;

            LayoutExport.Capture(graph); // raw 구조 캡처 (더미/랭크 생성 이전) — DevMode 전용 export 버퍼

            this.graph = graph;
            effectiveTechLevel = new Dictionary<ResearchNode, TechLevel>();
            isolatedNodesByRank = new Dictionary<int, List<ResearchNode>>();
            isolatedNodesSet = new HashSet<ResearchNode>();
            subtreeWeight = null;

            var realNodes = graph.Nodes.Where(n => !n.IsDummy && !n.IsProxy).ToList();
            if (realNodes.Count == 0) return;
            graphHasEras = realNodes.Any(n => n.TechLevel != TechLevel.Undefined);

            var topo = TopologicalOrder(realNodes);
            ComputeEffectiveTechLevels(topo);

            var bands = AssignRanks(topo);
            TightenRanks(topo, bands);
            columnBudget = ComputeColumnBudget(topo);
            BalanceRanks(topo, bands);
            PlaceProxies(bands);
            CompactRanks(bands);
            int metricsTotalEdgeSpan = ComputeTotalEdgeSpan(topo);
            NormalizeEdges();
            ExtractIsolated();
            var layers = BuildLayers(0);
            int metricsCrossings = MinimizeCrossings(layers);

            var rankX = AssignX(layers);
            AssignY(layers, rankX);
            if (UntangleChains(layers))
            {
                ShapeChains(layers);
                ResolveDummyRuns(layers);
            }

            PlaceIsolated(layers, rankX, bands);
            ValidateLayerSeparation(layers);

            graph.UpdateBoundingBox();

            graph.Metrics = ComputeLayoutMetrics(layers, metricsCrossings, metricsTotalEdgeSpan);
        }

        private bool InGraph(ResearchNode n) => n.Key.Equals(graph.Key);

        /// <summary>같은 그래프의 실제 선행 노드</summary>
        private IEnumerable<ResearchNode> RealPrereqs(ResearchNode n)
            => n.Prerequisites.Where(p => !p.IsDummy && !p.IsProxy && InGraph(p));

        /// <summary>같은 그래프의 실제 자식 노드</summary>
        private IEnumerable<ResearchNode> RealChildren(ResearchNode n)
            => n.Children.Where(c => !c.IsDummy && !c.IsProxy && InGraph(c));

        /// <summary>레이아웃 그래프 내 상위 이웃</summary>
        private IEnumerable<ResearchNode> Ups(ResearchNode n) => n.Prerequisites.Where(InGraph);

        /// <summary>레이아웃 그래프 내 하위 이웃</summary>
        private IEnumerable<ResearchNode> Downs(ResearchNode n) => n.Children.Where(InGraph);

        private ResearchNode NextTrunkDown(ResearchNode d) => Downs(d).FirstOrDefault(x => x.IsDummy);

        private List<ResearchNode> TrunkTargets(List<ResearchNode> chain)
        {
            var targets = new List<ResearchNode>(2);
            foreach (var d in chain)
            {
                foreach (var c in Downs(d))
                {
                    if (!c.IsDummy) targets.Add(c);
                }
            }
            return targets;
        }

        private float TrunkDestY(List<ResearchNode> chain)
        {
            var ys = new List<float>(2);
            foreach (var d in chain)
            {
                foreach (var c in Downs(d))
                {
                    if (!c.IsDummy) ys.Add(c.Position.y);
                }
            }
            if (ys.Count == 0) return float.NaN;
            ys.Sort();
            int m = ys.Count / 2;
            return (ys.Count & 1) == 1 ? ys[m] : (ys[m - 1] + ys[m]) / 2f;
        }

        /// <summary>노드의 수직 점유 높이 — 더미(엣지 통로)는 실제 노드보다 좁다.</summary>
        private static float NodeHeightOf(ResearchNode n)
            => n.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;

        /// <summary>인접쌍 간격의 스페이싱 성분 — 더미-더미는 LayoutDummySpacing, 그 외는 NodeSpacing.y.</summary>
        private static float SpacingOf(ResearchNode a, ResearchNode b)
            => a.IsDummy && b.IsDummy ? Constraints.LayoutDummySpacing : Constraints.NodeSpacing.y;

        /// <summary>통로 클램프 쌍별 완화량 = 스페이싱 성분 − 시각적 패드 (SeparationOf에서 빼면 시각적 최소 간격).</summary>
        private static float RelaxOf(ResearchNode a, ResearchNode b)
            => Mathf.Max(0f, SpacingOf(a, b) - Constraints.LayoutDummyVisualPad);

        /// <summary>같은 레이어에서 인접한 두 노드 중심 간 최소 수직 간격</summary>
        private static float SeparationOf(ResearchNode a, ResearchNode b)
            => (NodeHeightOf(a) + NodeHeightOf(b)) / 2f + SpacingOf(a, b);


        /// <summary>NormalizeEdges 이전 호출: 실-실 의존 엣지의 rank span 합 (topo는 CompactRanks 이후 Rank).</summary>
        private int ComputeTotalEdgeSpan(IReadOnlyList<ResearchNode> topo)
        {
            int span = 0;
            foreach (var n in topo)
            {
                foreach (var c in RealChildren(n))
                {
                    span += c.Rank - n.Rank; // 항상 양수 (전방 엣지 불변식)
                }
            }
            return span;
        }

        /// <summary>
        /// 레이아웃 완료 후 호출: 더미 체인 Y 프로파일의 bend/variation/unjustifiedBends, 컬럼별 픽셀 maxColumnHeight, RankCost 구성 항을 측정
        /// </summary>
        private LayoutMetrics ComputeLayoutMetrics(
            List<List<ResearchNode>> layers,
            int crossings,
            int totalEdgeSpan)
        {
            try
            {
                var m = new LayoutMetrics();
                m.Crossings = crossings;
                m.TotalEdgeSpan = totalEdgeSpan;
                m.NonConvergedChains = lastNonConvergedChains;

                // bend / verticalVariation / unjustifiedBends: 더미 체인별 Y 프로파일
                float bendEps = Constraints.MetricsBendEpsilon;
                var visitedChainHeads = new HashSet<ResearchNode>();
                foreach (var layer in layers)
                {
                    foreach (var node in layer)
                    {
                        if (node.IsDummy) continue;

                        foreach (var down in Downs(node))
                        {
                            if (!down.IsDummy) continue;

                            if (!visitedChainHeads.Add(down)) continue;

                            // Y 시퀀스 구성: 소스 실노드 → 트렁크 더미들 → 첫 실타깃
                            float srcY = node.Position.y;
                            var dummyNodes = new List<ResearchNode>();
                            var cur = down;
                            while (cur != null)
                            {
                                dummyNodes.Add(cur);
                                cur = NextTrunkDown(cur);
                            }
                            int cn = dummyNodes.Count;

                            var chainY = new List<float>();
                            chainY.Add(srcY);
                            foreach (var dn in dummyNodes) chainY.Add(dn.Position.y);
                            var tail = down;
                            while (NextTrunkDown(tail) != null) tail = NextTrunkDown(tail);
                            var firstRealTarget = Downs(tail).FirstOrDefault(x => !x.IsDummy);
                            if (firstRealTarget != null) chainY.Add(firstRealTarget.Position.y);

                            int prevSign = 0;
                            for (int i = 1; i < chainY.Count; i++)
                            {
                                float dy = chainY[i] - chainY[i - 1];
                                m.VerticalVariation += Mathf.Abs(dy);
                                if (Mathf.Abs(dy) < bendEps) continue;
                                int sign = dy > 0 ? 1 : -1;
                                if (prevSign != 0 && sign != prevSign) m.BendCount++;
                                prevSign = sign;
                            }

                            // UnjustifiedBends: 더미 구간 방향 전환이 이동 창으로 정당화되는지 판별
                            if (cn < 2) continue; // 더미 1개 이하 — 굽힘 불가
                            float chainSrcY = srcY;
                            float chainDstY = firstRealTarget != null ? firstRealTarget.Position.y : float.NaN;
                            if (float.IsNaN(chainDstY)) continue; // 타깃 없음 — 스킵

                            float rawDirF = chainDstY - chainSrcY;
                            int chainDir = Mathf.Abs(rawDirF) < bendEps ? 0 : (rawDirF > 0f ? 1 : -1);

                            // 더미 전용 lo/hi 재계산 (최종 이웃 위치 기준 근사).
                            float monoFloor = chainSrcY;
                            float monoCeil  = chainSrcY;
                            int prevSignU = 0;
                            bool prevForced = false;
                            for (int j = 0; j < cn; j++)
                            {
                                var dn = dummyNodes[j];
                                var dnLayer = layers[dn.Rank];
                                int dnIdx = dn.VOrder;
                                float loJ = dnIdx > 0
                                    ? dnLayer[dnIdx - 1].Position.y + SeparationOf(dnLayer[dnIdx - 1], dn)
                                    : float.NegativeInfinity;
                                float hiJ = dnIdx + 1 < dnLayer.Count
                                    ? dnLayer[dnIdx + 1].Position.y - SeparationOf(dn, dnLayer[dnIdx + 1])
                                    : float.PositiveInfinity;

                                // 이 멤버가 단조 추세 밖으로 강제됐는가
                                bool curForced = loJ > hiJ // 창 역전
                                    || (chainDir >= 0 && hiJ < monoFloor)  // DOWN: 천장이 플로어 아래 → 강제 하강
                                    || (chainDir <= 0 && loJ > monoCeil);  // UP:   바닥이 실링 위 → 강제 상승

                                float dY = chainY[j + 1] - chainY[j]; // chainY[0]=srcY 이므로 j+1이 더미 Y
                                if (Mathf.Abs(dY) >= bendEps)
                                {
                                    int curSign = dY > 0f ? 1 : -1;
                                    if (prevSignU != 0 && curSign != prevSignU)
                                    {
                                        // 진입(curForced) 또는 직전 강제로부터의 복귀(prevForced)면 정당
                                        if (!curForced && !prevForced) m.UnjustifiedBends++;
                                    }
                                    prevSignU = curSign;
                                }
                                prevForced = curForced;

                                // 단조 추적값 갱신 — 단조 방향으로만 움직인다 (무조건 리셋하면 복구 굽힘 오분류)
                                float placed = chainY[j + 1];
                                if (placed > monoFloor) monoFloor = placed; // DOWN: 플로어는 상승만
                                if (placed < monoCeil)  monoCeil  = placed; // UP:   실링은 하강만
                            }
                        }
                    }
                }

                // maxColumnHeight: 컬럼(랭크)별 실제 픽셀 범위
                var rankMin = new Dictionary<int, float>();
                var rankMax = new Dictionary<int, float>();
                foreach (var node in graph.Nodes)
                {
                    if (isolatedNodesSet.Contains(node)) continue;
                    float half = NodeHeightOf(node) / 2f; // Position.y는 중심 — 박스 범위는 ±높이/2
                    float top = node.Position.y - half;
                    float bot = node.Position.y + half;
                    float cur2;
                    if (!rankMin.TryGetValue(node.Rank, out cur2) || top < cur2) rankMin[node.Rank] = top;
                    if (!rankMax.TryGetValue(node.Rank, out cur2) || bot > cur2) rankMax[node.Rank] = bot;
                }
                float maxColH = 0f;
                foreach (var kvp in rankMin)
                {
                    float h = rankMax[kvp.Key] - kvp.Value;
                    if (h > maxColH) maxColH = h;
                }
                m.MaxColumnHeight = maxColH;

                // RankCost 구성 항 (= BalanceRanks 비용 함수)
                float budget = columnBudget;
                float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
                float overflowCost = 0f;
                foreach (var kvp2 in rankMin)
                {
                    float load = rankMax[kvp2.Key] - kvp2.Value;
                    float overPx = Mathf.Max(0f, load - budget);
                    float overRows = overPx / rowReal;
                    overflowCost += overRows * overRows;
                }
                m.OverflowCost = overflowCost;

                m.UsedRankCount = rankMin.Count; // 비고립 노드를 포함하는 랭크 수 (rankMin이 이미 고립 제외)

                m.RankCost = m.OverflowCost
                    + Constraints.RankCostSpanWeight  * m.TotalEdgeSpan
                    + Constraints.RankCostWidthWeight * m.UsedRankCount;

                return m;
            }
            catch (Exception ex)
            {
                Log.Warning("[YART] LayoutMetrics 계측 중 예외 (레이아웃 동작에는 영향 없음): " + ex.Message);
                return null;
            }
        }

        private List<ResearchNode> TopologicalOrder(IReadOnlyList<ResearchNode> nodes)
        {
            var indeg = new Dictionary<ResearchNode, int>(nodes.Count);
            foreach (var n in nodes) indeg[n] = RealPrereqs(n).Count();

            var queue = new Queue<ResearchNode>(nodes.Where(n => indeg[n] == 0));
            var result = new List<ResearchNode>(nodes.Count);

            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                result.Add(v);
                foreach (var c in RealChildren(v))
                {
                    if (indeg.TryGetValue(c, out int d) && --indeg[c] == 0 && d > 0)
                        queue.Enqueue(c);
                }
            }

            // 사이클은 그래프 빌드 단계에서 제거되지만, 만일을 대비한 방어
            if (result.Count < nodes.Count)
            {
                Log.Warning($"[YART] Layout: topological order incomplete ({result.Count}/{nodes.Count}) in {graph.Key}.");
                var seen = new HashSet<ResearchNode>(result);
                result.AddRange(nodes.Where(n => !seen.Contains(n)));
            }
            return result;
        }

        /// <summary>유효 TechLevel = max(자신, 조상 전체) 계산 — 밴드 구간 겹침을 원천 차단한다.</summary>
        private void ComputeEffectiveTechLevels(IEnumerable<ResearchNode> topo)
        {
            foreach (var v in topo)
            {
                var tl = v.TechLevel;
                foreach (var p in RealPrereqs(v))
                {
                    if (effectiveTechLevel.TryGetValue(p, out var ptl) && ptl > tl) tl = ptl;
                }
                effectiveTechLevel[v] = tl;
                v.EffectiveTechLevelInternal = tl; // 색을 유효 시대 구역에 맞추는 옵션이 참조
            }
        }

        /// <summary>TechLevel 밴드별 longest-path 랭크 할당 (밴드 base = 이전 밴드 max + 1).</summary>
        private Dictionary<TechLevel, (int lo, int hi)> AssignRanks(IEnumerable<ResearchNode> topo)
        {
            var bands = new Dictionary<TechLevel, (int lo, int hi)>();
            int nextBase = 0;

            // GroupBy는 그룹 내 위상 순서를 보존함
            foreach (var band in topo.GroupBy(v => effectiveTechLevel[v]).OrderBy(g => g.Key))
            {
                int baseRank = nextBase;
                int maxRank = baseRank;

                foreach (var v in band)
                {
                    int r = baseRank;
                    foreach (var p in RealPrereqs(v))
                    {
                        if (p.Rank + 1 > r) r = p.Rank + 1;
                    }
                    v.Rank = r;
                    if (r > maxRank) maxRank = r;
                }

                bands[band.Key] = (baseRank, maxRank);
                nextBase = maxRank + 1;
            }
            return bands;
        }

        /// <summary>Rank tightening: 각 노드를 밴드+선행/자식 제약 안에서 총 엣지 길이가 줄어드는 쪽으로 당긴다.</summary>
        private void TightenRanks(IEnumerable<ResearchNode> topo, IReadOnlyDictionary<TechLevel, (int lo, int hi)> bands)
        {
            var reverseTopo = new List<ResearchNode>(topo);
            reverseTopo.Reverse();

            for (int pass = 0; pass < Constraints.LayoutRankTightenMaxPasses; pass++)
            {
                bool changed = false;

                foreach (var v in reverseTopo)
                {
                    var band = bands[effectiveTechLevel[v]];
                    int lo = band.lo, hi = band.hi;
                    int indeg = 0, outdeg = 0;

                    foreach (var p in RealPrereqs(v))
                    {
                        indeg++;
                        if (p.Rank + 1 > lo) lo = p.Rank + 1;
                    }
                    foreach (var c in RealChildren(v))
                    {
                        outdeg++;
                        if (c.Rank - 1 < hi) hi = c.Rank - 1;
                    }
                    if (hi < lo) continue;

                    int target = outdeg > indeg ? hi
                               : indeg > outdeg ? lo
                               : Mathf.Clamp(v.Rank, lo, hi);

                    if (target != v.Rank)
                    {
                        v.Rank = target;
                        changed = true;
                    }
                }

                if (!changed) break;
            }
        }

        /// <summary>
        /// 컬럼 높이 예산을 그래프별로 산정한다. per-tab 그래프는 기본값(12행=936)이 최적이라 그대로 두고,
        /// 통합 그래프만 per-rank 실노드 수의 P75를 예산으로 — 12행은 통합엔 비현실적으로 작아 limiter가
        /// 무리하게 펼쳐 soup를 만들거나(과펼침) 도달조차 못 한다. 현실적 목표로 피크만 평탄화한다.
        /// </summary>
        private float ComputeColumnBudget(IReadOnlyList<ResearchNode> topo)
        {
            float def = Constraints.LayoutMaxColumnHeight;
            if (!graph.Key.IsUnified) return def; // per-tab: 12행이 최적 (검증됨)

            // per-rank 실노드 수 (AssignRanks+TightenRanks 직후 = limiter 펼침 이전의 자연 분포)
            var counts = new Dictionary<int, int>();
            foreach (var v in topo)
            {
                counts.TryGetValue(v.Rank, out int c);
                counts[v.Rank] = c + 1;
            }
            if (counts.Count == 0) return def;

            var sorted = counts.Values.OrderBy(x => x).ToList();
            int idx = Mathf.Clamp((int)Math.Ceiling(sorted.Count * Constraints.LayoutUnifiedBudgetPercentile) - 1, 0, sorted.Count - 1);
            float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            return Mathf.Max(def, sorted[idx] * rowReal);
        }

        /// <summary>
        /// 높이 제한 레이어링: 가중 높이(실노드 행 + 관통 통로)가 소프트 예산을 넘는 컬럼에서
        /// 슬랙 실노드를 오른쪽으로 밀어 가로↑·세로↓. 통로 포화 컬럼은 MinRows 바닥에서 멈춤.
        /// </summary>
        private void LimitColumnHeights(IReadOnlyList<ResearchNode> topo, Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            if (bands.Count == 0) return;

            float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            float rowCorridor = Constraints.LayoutDummyNodeHeight + Constraints.LayoutDummySpacing;

            // 무한 루프 안전망 — 수락된 밀기는 height(r)을 엄격히 줄이고 이동은 오른쪽 단방향이라
            // 종료는 캡과 무관하게 보장된다. 이 상한 초과는 진짜 버그.
            int rankCount = topo.Count == 0 ? 0 : topo.Max(v => v.Rank) + 1;
            int movesLeft = topo.Count * (rankCount + Constraints.LayoutHeightLimitMaxExtraRanks);
            int extraRanksLeft = Constraints.LayoutHeightLimitMaxExtraRanks;

            // 컬럼 r의 추정 높이. 관통 통로는 소스당 1개(허브 버스). 고립 노드도 행으로 친다.
            float ColumnHeight(int r, out int rows, out int corridors)
            {
                rows = 0;
                corridors = 0;
                foreach (var v in topo)
                {
                    if (v.Rank == r)
                    {
                        rows++;
                    }
                    else if (v.Rank < r)
                    {
                        foreach (var c in RealChildren(v))
                        {
                            if (c.Rank > r) { corridors++; break; } // 소스 v의 통로는 1개
                        }
                    }
                }
                return rows * rowReal + corridors * rowCorridor;
            }

            // 컬럼 r에서 r+1로 밀 최선 후보. 우선순위: 새 통로 수 작은 순 → 리프 우선 → defName 서수(언어 무관 결정성).
            // atBandEdge(r == band.hi)면 자식 제약 생략 — 자식은 후속 밴드라 삽입 시프트 후 r+2 이상이 됨.
            ResearchNode PickCandidate(int r, bool atBandEdge)
            {
                ResearchNode best = null;
                int bestNew = int.MaxValue;
                bool bestLeaf = false;

                foreach (var v in topo)
                {
                    if (v.Rank != r) continue;

                    bool isLeaf = true;
                    if (atBandEdge)
                    {
                        isLeaf = !RealChildren(v).Any();
                    }
                    else
                    {
                        // 자식 제약: 그래프 내 모든 실자식 Rank ≥ r+2 (r+1 자식이 있으면 후보 아님)
                        bool blocked = false;
                        foreach (var c in RealChildren(v))
                        {
                            isLeaf = false;
                            if (c.Rank < r + 2) { blocked = true; break; }
                        }
                        if (blocked) continue;
                    }

                    // 새 통로 수: v를 r+1로 밀 때 새로 r을 관통하는 선행 — p가 이미 관통 중이면 버스 공유로 0개
                    int newCorridors = 0;
                    foreach (var p in RealPrereqs(v))
                    {
                        bool spanning = false;
                        foreach (var c in RealChildren(p))
                        {
                            if (c.Rank > r) { spanning = true; break; }
                        }
                        if (!spanning) newCorridors++;
                    }
                    // 최대 엣지 스팬 캡(0=비활성): 밀어서 가장 늦은 실선행과의 스팬이 상한을 넘으면 제외.
                    if (Constraints.LayoutMaxEdgeSpan > 0)
                    {
                        int latestPrereqRank = -1;
                        foreach (var p in RealPrereqs(v))
                            if (p.Rank > latestPrereqRank) latestPrereqRank = p.Rank;
                        if (latestPrereqRank >= 0 && (r + 1 - latestPrereqRank) > Constraints.LayoutMaxEdgeSpan)
                            continue; // 밀면 스팬 초과 — 이 후보 건너뜀
                    }

                    if (-rowReal + newCorridors * rowCorridor >= 0f) continue; // height(r)을 줄이지 못함

                    if (best == null
                        || newCorridors < bestNew
                        || (newCorridors == bestNew && ((isLeaf && !bestLeaf)
                            || (isLeaf == bestLeaf && string.CompareOrdinal(v.Id, best.Id) < 0))))
                    {
                        best = v;
                        bestNew = newCorridors;
                        bestLeaf = isLeaf;
                    }
                }
                return best;
            }

            // 밴드별·랭크 오름차순 스캔. 밴드 확장으로 hi가 도중에 자라므로 매 반복 재조회.
            bool warnedMoveCap = false;
            bool loggedCorridorFloor = false;
            foreach (var key in bands.Keys.OrderBy(k => k).ToList())
            {
                for (int r = bands[key].lo; r <= bands[key].hi; r++)
                {
                    while (movesLeft > 0)
                    {
                        int rows, corridors;
                        if (ColumnHeight(r, out rows, out corridors) <= columnBudget) break;

                        // 통로 포화 바닥: 실노드가 바닥 이하인데도 예산 초과 = 통로가 예산을 다 먹은 컬럼.
                        // 더 밀면 전량 소개 → 밴드 끝 좌초만 만든다. 의도된 상황이라 DevMode 메시지 1회.
                        if (rows <= Constraints.LayoutHeightLimitMinRows)
                        {
                            if (!loggedCorridorFloor && Prefs.DevMode
                                && corridors * rowCorridor >= columnBudget
                                                             - Constraints.LayoutHeightLimitMinRows * rowReal)
                            {
                                Log.Message($"[YART] Layout: rank {r} in {graph.Key} stays over budget — "
                                    + $"corridor-saturated ({corridors} corridors ≈ {corridors * rowCorridor:F0}px, "
                                    + $"{rows} real rows kept at floor {Constraints.LayoutHeightLimitMinRows}).");
                                loggedCorridorFloor = true;
                            }
                            break;
                        }

                        // r == band.hi면 밴드 경계 — 새 랭크를 삽입해야만 오른쪽으로 수용 가능
                        bool atBandEdge = r == bands[key].hi;
                        if (atBandEdge && extraRanksLeft <= 0) break;

                        var candidate = PickCandidate(r, atBandEdge);
                        if (candidate == null) break; // 줄일 후보 없음 — 이 컬럼 포기 (소프트 예산)

                        if (atBandEdge)
                        {
                            InsertRankAfterBand(key, bands);
                            extraRanksLeft--;
                        }
                        candidate.Rank = r + 1;
                        movesLeft--;
                    }

                    // 전역 이동 예산 소진 = 비정상 신호 — 남은 초과 컬럼은 건너뛰므로 1회 경고.
                    if (!warnedMoveCap && movesLeft <= 0 && ColumnHeight(r, out _, out _) > columnBudget)
                    {
                        Log.Warning($"[YART] Layout: height-limit move cap exhausted in {graph.Key} — remaining over-budget columns skipped.");
                        warnedMoveCap = true;
                    }
                }
            }

            if (Prefs.DevMode)
            {
                ValidateRankInvariants(topo, bands);
            }
        }

        /// <summary>
        /// 전역 rank balancing: LimitColumnHeights를 시드로 실행 → 시드에서 ±1 greedy 정제
        /// </summary>
        private void BalanceRanks(IReadOnlyList<ResearchNode> topo, Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            if (bands.Count == 0) return;

            // ── 1단계: LimitColumnHeights 시드 ──
            LimitColumnHeights(topo, bands);

            float rowReal    = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            float rowCorridor = Constraints.LayoutDummyNodeHeight + Constraints.LayoutDummySpacing;
            float budget     = columnBudget;
            float lambda     = Constraints.RankCostSpanWeight;
            float mu         = Constraints.RankCostWidthWeight;

            // 이동 상한 — limiter 이후 최종 rankCount 기준 재산정.
            int rankCount = topo.Count == 0 ? 0 : topo.Max(v => v.Rank) + 1;
            int movesLeft = topo.Count * rankCount;

            const float EPS = 1e-4f; // float 노이즈 방지 — 이보다 작은 개선은 무시

            // 증분 캐시를 이용해서 시간 복잡도 최적화
            var rowsArr = new int[rankCount];
            var corrArr = new int[rankCount];
            var diffBuf = new int[rankCount + 1];
            var maxCR = new Dictionary<ResearchNode, int>(topo.Count);

            void RebuildCaches()
            {
                System.Array.Clear(rowsArr, 0, rankCount);
                System.Array.Clear(diffBuf, 0, rankCount + 1);
                maxCR.Clear();
                foreach (var v in topo)
                {
                    rowsArr[v.Rank]++;
                    int mcr = v.Rank;
                    foreach (var c in RealChildren(v))
                        if (c.Rank > mcr) mcr = c.Rank;
                    maxCR[v] = mcr;
                    // 소스 v는 (v.Rank, mcr) 배타 구간 = [v.Rank+1, mcr-1] 랭크를 관통 (소스당 1개, 허브 버스)
                    int lo2 = v.Rank + 1, hi2 = mcr - 1;
                    if (lo2 <= hi2) { diffBuf[lo2]++; diffBuf[hi2 + 1]--; }
                }
                int run = 0;
                for (int r = 0; r < rankCount; r++) { run += diffBuf[r]; corrArr[r] = run; }
            }

            // ColHeight 모델과 동일: rows*rowReal + corr*rowCorridor (LimitColumnHeights.ColumnHeight와 일치)
            float HeightOf(int rows, int corr) => rows * rowReal + corr * rowCorridor;
            // overflow 항: 한 랭크의 squared-overflow (원본 OverflowTerm과 동일 연산 순서)
            float OverflowOf(float h)
            {
                float over = h - budget;
                if (over <= 0f) return 0f;
                float overRows = over / rowReal;
                return overRows * overRows;
            }
            // 소스 contribution: (rank < r) && (mcr > r) 이면 r을 관통
            int Contrib(int rank, int mcr, int r) => (rank < r && mcr > r) ? 1 : 0;

            // 노드 v를 oldR→newR(=oldR±1) 이동 시 RankCost delta. 캐시(현재=v@oldR) 기반 O(deg).
            // 이동이 oldR/newR의 corridor에 주는 영향은 v 자신 + v의 부모(maxChildRank 변동)뿐 — 다른 노드는 상쇄.
            float DeltaRankCost(ResearchNode v, int oldR, int newR)
            {
                int mcrV = maxCR[v]; // v 자신의 max child rank는 이동해도 불변 (자식은 안 움직임)

                // corridor delta at oldR / newR: v 자신
                int dCorrOld = Contrib(newR, mcrV, oldR) - Contrib(oldR, mcrV, oldR);
                int dCorrNew = Contrib(newR, mcrV, newR) - Contrib(oldR, mcrV, newR);

                // + v의 부모들 (v 이동으로 maxChildRank가 바뀔 수 있음)
                foreach (var p in RealPrereqs(v))
                {
                    int mcrBefore = maxCR[p];
                    int mcrAfter;
                    if (oldR < mcrBefore)
                    {
                        mcrAfter = mcrBefore; // v가 p의 최대 자식이 아님 → 불변
                    }
                    else
                    {
                        // v.oldR == mcrBefore (v가 p의 최대 자식) → 다른 자식 + v의 newR로 재계산
                        int m = newR;
                        foreach (var c in RealChildren(p))
                            if (!ReferenceEquals(c, v) && c.Rank > m) m = c.Rank;
                        mcrAfter = m;
                    }
                    dCorrOld += Contrib(p.Rank, mcrAfter, oldR) - Contrib(p.Rank, mcrBefore, oldR);
                    dCorrNew += Contrib(p.Rank, mcrAfter, newR) - Contrib(p.Rank, mcrBefore, newR);
                }

                // overflow delta (원본 grouping 유지: (newOld+newNew) − (baseOld+baseNew))
                float hOldBefore = HeightOf(rowsArr[oldR],     corrArr[oldR]);
                float hOldAfter  = HeightOf(rowsArr[oldR] - 1, corrArr[oldR] + dCorrOld);
                float hNewBefore = HeightOf(rowsArr[newR],     corrArr[newR]);
                float hNewAfter  = HeightOf(rowsArr[newR] + 1, corrArr[newR] + dCorrNew);

                float dOverflow = (OverflowOf(hOldAfter) + OverflowOf(hNewAfter))
                                - (OverflowOf(hOldBefore) + OverflowOf(hNewBefore));

                // span delta: prereq당 +(newR−oldR), child당 −(newR−oldR)
                int dSpan = 0;
                foreach (var p in RealPrereqs(v)) dSpan += newR - oldR;
                foreach (var c in RealChildren(v)) dSpan -= newR - oldR;

                // usedRankCount delta: oldR이 비면 −1(현재 v만 존재), newR이 새로우면 +1(현재 0)
                bool oldWillEmpty = rowsArr[oldR] == 1;
                bool newIsNew = rowsArr[newR] == 0;
                int dUsed = (newIsNew ? 1 : 0) - (oldWillEmpty ? 1 : 0);

                return dOverflow + lambda * dSpan + mu * dUsed;
            }

            // ── 2단계: ±1 greedy 정합 루프 (기존 랭크 구조 안에서만 이동, 밴드 끝 삽입 없음) ──
            bool warnedMoveCap = false;
            bool improved = true;
            while (improved && movesLeft > 0)
            {
                RebuildCaches(); // 직전 이터레이션의 커밋 반영 (첫 회는 LimitColumnHeights 시드 상태)
                improved = false;
                float bestDelta = -EPS; // 엄격히 음수인 이동만 채택
                ResearchNode bestNode = null;
                int bestOldR = -1, bestNewR = -1;

                // 위상 순서로 결정적 순회 (tie-break: defName 서수 — 언어 무관)
                foreach (var v in topo)
                {
                    int r = v.Rank;
                    var band = bands[effectiveTechLevel[v]];

                    // 허용 구간 = [maxPrereq+1, minChild−1] ∩ [band.lo, band.hi]
                    int lo = band.lo;
                    int hi = band.hi;
                    foreach (var p in RealPrereqs(v))
                        if (p.Rank + 1 > lo) lo = p.Rank + 1;
                    foreach (var c in RealChildren(v))
                        if (c.Rank - 1 < hi) hi = c.Rank - 1;
                    if (hi < lo) continue; // 이동 불가

                    // 후보 이동: r−1 (왼쪽). 전방성 보존(r−1 ≥ lo), 통로를 줄이는 방향이라 바닥 가드 불필요.
                    if (r - 1 >= lo)
                    {
                        int nr = r - 1;
                        float d = DeltaRankCost(v, r, nr);
                        if (d < bestDelta
                            || (d < bestDelta + EPS * 0.5f && bestNode != null
                                && (string.CompareOrdinal(v.Id, bestNode.Id) < 0
                                    || (string.CompareOrdinal(v.Id, bestNode.Id) == 0 && nr < bestNewR))))
                        {
                            bestDelta = d;
                            bestNode  = v;
                            bestOldR  = r;
                            bestNewR  = nr;
                            improved  = true;
                        }
                    }

                    // 후보 이동: r+1 (오른쪽). 기존 랭크 구조 안에서만 (밴드 끝 삽입 없음).
                    if (r + 1 <= hi)
                    {
                        int nr = r + 1;

                        // 최대 엣지 스팬 캡 (limiter와 동일 정책 — 시드만 캡하면 refine이 다시 흩뿌림)
                        if (Constraints.LayoutMaxEdgeSpan > 0)
                        {
                            int latestPrereqRank = -1;
                            foreach (var p in RealPrereqs(v))
                                if (p.Rank > latestPrereqRank) latestPrereqRank = p.Rank;
                            if (latestPrereqRank >= 0 && (nr - latestPrereqRank) > Constraints.LayoutMaxEdgeSpan) goto skipRight;
                        }

                        // 통로 포화 바닥: 오른쪽 이동으로 실노드가 MinRows 이하로 줄면 금지
                        if (rowsArr[r] <= Constraints.LayoutHeightLimitMinRows) goto skipRight;

                        float d = DeltaRankCost(v, r, nr);
                        bool better = d < bestDelta;
                        if (!better && System.Math.Abs(d - bestDelta) < EPS * 0.5f && bestNode != null)
                        {
                            int cmp = string.CompareOrdinal(v.Id, bestNode.Id);
                            if (cmp < 0 || (cmp == 0 && nr < bestNewR)) better = true;
                        }
                        if (better)
                        {
                            bestDelta = d;
                            bestNode  = v;
                            bestOldR  = r;
                            bestNewR  = nr;
                            improved  = true;
                        }
                        skipRight:;
                    }
                }

                if (!improved || bestNode == null) break;

                bestNode.Rank = bestNewR;
                movesLeft--;

                if (movesLeft <= 0 && !warnedMoveCap)
                {
                    if (Prefs.DevMode)
                        Log.Warning($"[YART] BalanceRanks: refine move cap exhausted in {graph.Key} — remaining improvement skipped.");
                    warnedMoveCap = true;
                    break;
                }
            }

            if (Prefs.DevMode) ValidateRankInvariants(topo, bands);
        }

        /// <summary>밴드 끝(hi) 직후에 빈 랭크 1개 삽입 (후속 랭크/밴드 +1 시프트). 프록시는 아직 미배치라 제외.</summary>
        private void InsertRankAfterBand(TechLevel key, Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            int hi = bands[key].hi;

            foreach (var n in graph.Nodes)
            {
                if (!n.IsProxy && !n.IsDummy && n.Rank > hi) n.Rank++;
            }
            foreach (var k in bands.Keys.ToList())
            {
                var b = bands[k];
                if (k == key) bands[k] = (b.lo, b.hi + 1);
                else if (b.lo > hi) bands[k] = (b.lo + 1, b.hi + 1);
            }
        }

        /// <summary>DevMode 랭크 불변식 검사 (경고만): 전방 엣지, 실노드 밴드 포함, 밴드 비중첩·오름차순.</summary>
        private void ValidateRankInvariants(IReadOnlyList<ResearchNode> topo, IReadOnlyDictionary<TechLevel, (int lo, int hi)> bands)
        {
            foreach (var v in topo)
            {
                var band = bands[effectiveTechLevel[v]];
                if (v.Rank < band.lo || v.Rank > band.hi)
                {
                    Log.Warning($"[YART] Layout: '{v.Label}' rank {v.Rank} outside band [{band.lo},{band.hi}] in {graph.Key}.");
                }
                foreach (var c in RealChildren(v))
                {
                    if (c.Rank <= v.Rank)
                    {
                        Log.Warning($"[YART] Layout: non-forward edge after height limit: '{v.Label}'({v.Rank}) -> '{c.Label}'({c.Rank}) in {graph.Key}.");
                    }
                }
            }

            int prevHi = -1;
            foreach (var k in bands.Keys.OrderBy(x => x))
            {
                var b = bands[k];
                if (b.lo <= prevHi)
                {
                    Log.Warning($"[YART] Layout: band {k} [{b.lo},{b.hi}] overlaps previous band (hi={prevHi}) in {graph.Key}.");
                }
                prevHi = b.hi;
            }
        }

        /// <summary>
        /// 프록시(타 트랙 선행의 대리 노드)를 원본 시대 구역 안에 배치: 같은 시대 실밴드가 있으면
        /// min(자식−1, 밴드.hi)에 클램프, 없으면 첫 상위 실밴드 직전에 전용 컬럼 삽입.
        /// (proxy.TechLevel의 lazy 캐시 동시 접근은 양쪽 다 Def.techLevel 동일값이라 무해한 레이스)
        /// </summary>
        private void PlaceProxies(Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            var proxies = graph.Nodes.Where(n => n.IsProxy).ToList();
            if (proxies.Count == 0) return;

            // 컬럼당 한 시대만 들어가야 AssignX의 컬럼 대표 시대가 오염되지 않는다 (전용 컬럼 삽입의 이유).
            var placed = new HashSet<ResearchNode>();

            foreach (var eraGroup in proxies.GroupBy(p => p.TechLevel).OrderBy(g => g.Key))
            {
                var era = eraGroup.Key;
                bool hasBand = bands.TryGetValue(era, out var band);

                var needColumn = new List<ResearchNode>();
                foreach (var proxy in eraGroup)
                {
                    int minChild = MinChildRank(proxy);
                    if (minChild == int.MaxValue)
                    {
                        proxy.Rank = 0; // 방어: 자식 없는 프록시
                        placed.Add(proxy);
                    }
                    else if (hasBand && minChild - 1 >= band.lo)
                    {
                        proxy.Rank = Mathf.Min(minChild - 1, band.hi);
                        placed.Add(proxy);
                    }
                    else
                    {
                        needColumn.Add(proxy);
                    }
                }
                if (needColumn.Count == 0) continue;

                // 삽입 지점: 자기 밴드가 있으면 밴드 시작 직전, 없으면 첫 상위 밴드 시작 직전
                int insertAt;
                if (hasBand)
                {
                    insertAt = band.lo;
                }
                else
                {
                    insertAt = int.MaxValue;
                    foreach (var kvp in bands)
                    {
                        if (kvp.Key > era && kvp.Value.lo < insertAt) insertAt = kvp.Value.lo;
                    }
                }

                bool inserted = false;
                foreach (var proxy in needColumn)
                {
                    int minChild = MinChildRank(proxy);
                    if (insertAt == int.MaxValue || minChild < insertAt)
                    {
                        // 자식 시대 <= 프록시 시대인 데이터 오류 — 시대 구역 포기, 전방 엣지만 보장
                        proxy.Rank = minChild - 1;
                        placed.Add(proxy);
                        continue;
                    }

                    if (!inserted)
                    {
                        foreach (var n in graph.Nodes)
                        {
                            if ((!n.IsProxy || placed.Contains(n)) && n.Rank >= insertAt) n.Rank++;
                        }
                        foreach (var k in bands.Keys.ToList())
                        {
                            var b = bands[k];
                            if (b.lo >= insertAt) bands[k] = (b.lo + 1, b.hi + 1);
                        }
                        inserted = true;
                    }
                    proxy.Rank = insertAt;
                    placed.Add(proxy);
                }
            }
        }

        /// <summary>같은 그래프 내 자식들의 최소 랭크 (자식 없으면 int.MaxValue).</summary>
        private int MinChildRank(ResearchNode proxy)
        {
            int min = int.MaxValue;
            foreach (var c in proxy.Children.Where(InGraph))
            {
                if (c.Rank < min) min = c.Rank;
            }
            return min;
        }

        /// <summary>
        /// 사용 중인 랭크를 0..K−1 연속 정수로 재매핑(빈 열 제거)하고 bands의 (lo, hi)도 함께 재매핑.
        /// 가장자리가 빈 랭크였을 수 있어 안쪽 클램프(lo 이상 첫 사용 / hi 이하 마지막 사용).
        /// </summary>
        private void CompactRanks(Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            var used = graph.Nodes.Select(n => n.Rank).Distinct().OrderBy(r => r).ToList();
            var map = new Dictionary<int, int>(used.Count);
            for (int i = 0; i < used.Count; i++) map[used[i]] = i;

            foreach (var n in graph.Nodes) n.Rank = map[n.Rank];

            // used 리스트의 인덱스가 곧 새 랭크 번호 (map[used[i]] == i)
            foreach (var k in bands.Keys.ToList())
            {
                var b = bands[k];
                int loIdx = used.BinarySearch(b.lo);
                if (loIdx < 0) loIdx = ~loIdx;     // lo 이상 첫 사용 랭크
                int hiIdx = used.BinarySearch(b.hi);
                if (hiIdx < 0) hiIdx = ~hiIdx - 1; // hi 이하 마지막 사용 랭크
                bands[k] = (loIdx, hiIdx);
            }
        }

        /// <summary>
        /// 멀티랭크 엣지를 더미 체인으로 분할하되 같은 From의 엣지들은 트렁크를 공유한다 (허브 버스).
        /// 더미 차수 불변식: in 1 / out ≥ 1 / 더미 자식 ≤ 1.
        /// </summary>
        private void NormalizeEdges()
        {
            // 1패스: 역방향 엣지 드롭 + 멀티랭크 엣지를 소스별로 수집 (소스 첫 등장 순서로 결정성 확보)
            var multiBySource = new Dictionary<ResearchNode, List<ResearchEdge>>();
            var sourceOrder = new List<ResearchNode>();

            foreach (var edge in graph.Edges.ToList())
            {
                var from = edge.From;
                var to = edge.To;
                int rankDiff = to.Rank - from.Rank;

                if (rankDiff == 1) continue;

                if (rankDiff < 1)
                {
                    // 발생하면 안 되는 상황 (사이클 제거 + 랭크 제약 위반) — 레이아웃 보호를 위해 끊는다
                    Log.Warning($"[YART] Layout: non-forward edge {from.Label} -> {to.Label} (diff={rankDiff}). Dropping.");
                    graph.Edges.Remove(edge);
                    from.Children.Remove(to);
                    to.Prerequisites.Remove(from);
                    continue;
                }

                if (!multiBySource.TryGetValue(from, out var list))
                {
                    list = new List<ResearchEdge>();
                    multiBySource[from] = list;
                    sourceOrder.Add(from);
                }
                list.Add(edge);
            }

            // 2패스: 소스별 공유 트렁크 생성 (허브 버스). 원본 멀티랭크 엣지는 전부 트렁크 경유로 대체.
            // (교차를 유발하는 트렁크는 이후 SelectiveUnbundle이 골라 개별 체인으로 푼다.)
            foreach (var from in sourceOrder)
            {
                var edges = multiBySource[from];

                int maxToRank = from.Rank;
                foreach (var e in edges)
                {
                    if (e.To.Rank > maxToRank) maxToRank = e.To.Rank;

                    graph.Edges.Remove(e);
                    from.Children.Remove(e.To);
                    e.To.Prerequisites.Remove(from);
                }

                // 공유 트렁크: from.Rank+1 .. maxToRank−1 (멀티랭크 엣지가 있으므로 길이 ≥ 1)
                var trunk = new ResearchNode[maxToRank - from.Rank - 1];
                var current = from;
                for (int r = from.Rank + 1; r < maxToRank; r++)
                {
                    var dummy = ResearchNode.CreateDummy(graph.Key);
                    dummy.Rank = r;
                    dummy.TechLevel = from.TechLevel;

                    graph.AddNode(dummy);
                    Connect(current, dummy);
                    trunk[r - from.Rank - 1] = dummy;
                    current = dummy;
                }

                // 각 타깃은 자기 랭크 직전 트렁크 더미에서 분기 (rankDiff ≥ 2 → 인덱스 ≥ 0)
                foreach (var e in edges)
                {
                    Connect(trunk[e.To.Rank - from.Rank - 2], e.To);
                }
            }

            if (Prefs.DevMode) ValidateNormalizedEdges();
        }

        /// <summary>
        /// DevMode: NormalizeEdges 직후 더미/엣지 불변식 검사 (경고만) —
        /// 모든 더미는 in-degree 1·out-degree ≥ 1·더미 자식 ≤ 1, 모든 엣지는 정확히 한 랭크 전진.
        /// </summary>
        private void ValidateNormalizedEdges()
        {
            foreach (var n in graph.Nodes)
            {
                if (!n.IsDummy) continue;
                if (Ups(n).Count() != 1)
                {
                    Log.Warning($"[YART] Layout: dummy at rank {n.Rank} has in-degree {Ups(n).Count()} (expected 1) in {graph.Key}.");
                }
                if (!Downs(n).Any())
                {
                    Log.Warning($"[YART] Layout: dummy at rank {n.Rank} has no children in {graph.Key}.");
                }
                // 더미 자식 ≤ 1 — 트렁크 워크(NextTrunkDown) 유일성의 근거
                int dummyChildren = Downs(n).Count(c => c.IsDummy);
                if (dummyChildren > 1)
                {
                    // 미드트렁크 더미의 직속 선행은 더미(라벨 빈 문자열)일 수 있으므로 실소스까지 거슬러 올라간다
                    var src = Ups(n).FirstOrDefault();
                    while (src != null && src.IsDummy) src = Ups(src).FirstOrDefault();
                    Log.Warning($"[YART] Layout: dummy at rank {n.Rank} (source '{src?.Label}') has {dummyChildren} dummy children (expected <= 1) in {graph.Key}.");
                }
            }
            foreach (var e in graph.Edges)
            {
                if (e.To.Rank != e.From.Rank + 1)
                {
                    Log.Warning($"[YART] Layout: non-unit edge after normalize: '{e.From.Label}'({e.From.Rank}) -> '{e.To.Label}'({e.To.Rank}) in {graph.Key}.");
                }
            }
        }

        private void Connect(ResearchNode from, ResearchNode to)
        {
            from.Children.Add(to);
            to.Prerequisites.Add(from);
            graph.AddEdge(from, to);
        }


        /// <summary>
        /// 소스에서 DFS 방문 순으로 레이어를 채운다 (서브트리 군집화 → 교차 최소화 초기해, 고립 노드 제외).
        /// variant 멀티 스타트: 0 = defName순, 1 = 후속 많은 순, 2 = defName 역순,
        /// 3 = 서브트리 무게 center-out(무거운 허브를 컬럼 중앙에 — 팬아웃 상단 쏠림 방지).
        /// </summary>
        private List<List<ResearchNode>> BuildLayers(int variant)
        {
            int rankCount = graph.Nodes.Max(n => n.Rank) + 1;
            var layers = new List<List<ResearchNode>>(rankCount);
            for (int i = 0; i < rankCount; i++) layers.Add(new List<ResearchNode>());

            var visited = new HashSet<ResearchNode>(isolatedNodesSet); // 고립 노드는 PlaceIsolated가 따로 배치
            var stack = new Stack<ResearchNode>();

            var sources = graph.Nodes.Where(n => !isolatedNodesSet.Contains(n) && !Ups(n).Any());
            List<ResearchNode> ordered;
            switch (variant)
            {
                case 1:
                    ordered = sources.OrderBy(n => n.Rank)
                        .ThenByDescending(n => Downs(n).Count())
                        .ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
                    break;
                case 2:
                    ordered = sources.OrderBy(n => n.Rank)
                        .ThenByDescending(n => n.Id, StringComparer.Ordinal).ToList();
                    break;
                case 3:
                {
                    // 랭크별로 무거운 서브트리를 중앙에 (DFS가 컬럼 중앙부터 채우도록)
                    var w = ComputeSubtreeWeights();
                    ordered = sources.GroupBy(n => n.Rank).OrderBy(g => g.Key)
                        .SelectMany(g => CenterOutByWeight(g, w)).ToList();
                    break;
                }
                default:
                    ordered = sources.OrderBy(n => n.Rank)
                        .ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
                    break;
            }

            foreach (var source in ordered)
            {
                stack.Push(source);
                while (stack.Count > 0)
                {
                    var v = stack.Pop();
                    if (!visited.Add(v)) continue;
                    layers[v.Rank].Add(v);

                    var kids = Downs(v).ToList();
                    if (variant == 1) kids.Sort((a, b) => Downs(b).Count().CompareTo(Downs(a).Count()));
                    else if (variant == 2) kids.Reverse();
                    else if (variant == 3) kids = CenterOutByWeight(kids, ComputeSubtreeWeights());
                    for (int i = kids.Count - 1; i >= 0; i--)
                    {
                        if (!visited.Contains(kids[i])) stack.Push(kids[i]);
                    }
                }
            }

            // 방어: 소스에서 도달 불가한 노드 (정상 DAG에선 없음)
            foreach (var n in graph.Nodes)
            {
                if (visited.Add(n)) layers[n.Rank].Add(n);
            }

            SyncVOrder(layers);
            return layers;
        }

        private static void SyncVOrder(List<List<ResearchNode>> layers)
        {
            foreach (var layer in layers)
            {
                for (int i = 0; i < layer.Count; i++) layer[i].VOrder = i;
            }
        }

        /// <summary>
        /// 각 노드의 하위 도달 노드 수(자신 제외, 더미 포함) — 무거운 서브트리를 컬럼 중앙에 놓는 시드(variant 3)용.
        /// defName/구조 기반이라 언어 무관. Rank 내림차순 = 역위상(모든 엣지는 정확히 1랭크 전진)으로 1패스 누적.
        /// </summary>
        private Dictionary<ResearchNode, int> ComputeSubtreeWeights()
        {
            if (subtreeWeight != null) return subtreeWeight;

            var weight = new Dictionary<ResearchNode, int>(graph.Nodes.Count);
            var desc = new Dictionary<ResearchNode, HashSet<ResearchNode>>(graph.Nodes.Count);
            foreach (var n in graph.Nodes.OrderByDescending(x => x.Rank))
            {
                var set = new HashSet<ResearchNode>();
                foreach (var c in Downs(n))
                {
                    set.Add(c);
                    if (desc.TryGetValue(c, out var cd)) set.UnionWith(cd);
                }
                desc[n] = set;
                weight[n] = set.Count;
            }

            subtreeWeight = weight;
            return weight;
        }

        /// <summary>
        /// 무게 내림차순으로 가장 무거운 항목을 가운데, 가벼운 항목을 위아래 바깥으로 교대 배치한 리스트(top→bottom).
        /// 동무게는 Id(defName) 서수로 결정적 정렬 — 언어 무관.
        /// </summary>
        private List<ResearchNode> CenterOutByWeight(IEnumerable<ResearchNode> items, Dictionary<ResearchNode, int> weight)
        {
            var sortedDesc = items
                .OrderByDescending(n => weight.TryGetValue(n, out var w) ? w : 0)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();

            var dq = new LinkedList<ResearchNode>();
            for (int i = 0; i < sortedDesc.Count; i++)
            {
                if ((i & 1) == 0) dq.AddLast(sortedDesc[i]); // 가장 무거운(i=0) 항목이 중앙
                else dq.AddFirst(sortedDesc[i]);
            }
            return new List<ResearchNode>(dq);
        }

        /// <summary>선행·후속이 전혀 없는 고립 노드를 분리한다 (median 정렬의 장애물화 방지 → 나중에 PlaceIsolated).</summary>
        private void ExtractIsolated()
        {
            foreach (var n in graph.Nodes)
            {
                if (n.IsDummy) continue;
                if (Ups(n).Any() || Downs(n).Any()) continue;

                isolatedNodesSet.Add(n);
                if (!isolatedNodesByRank.TryGetValue(n.Rank, out var list))
                {
                    list = new List<ResearchNode>();
                    isolatedNodesByRank[n.Rank] = list;
                }
                list.Add(n);
            }
            foreach (var list in isolatedNodesByRank.Values)
            {
                list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            }
        }

        private static readonly string[] OrderingVariantNames = { "dfs", "degree", "reverse", "weighted" };

        /// <summary>통합 ordering 점수 = 교차 수 + HeightWeight × 높이행수(EstimateMinHeight/gridY).</summary>
        private float OrderingScore(List<List<ResearchNode>> layers, int crossings)
        {
            float gridY = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            return crossings + Constraints.LayoutOrderingHeightWeight * (EstimateMinHeight(layers) / gridY);
        }

        /// <summary>
        /// 교차 최소화: 멀티 스타트(DFS 변형 3종)마다 [wmedian → transpose] 루프를 돌려 OrderingScore
        /// 최소 순서를 채택하고, 작은 레이어 전수(부분집합 DP) 스윕으로 마무리한다.
        /// </summary>
        private int MinimizeCrossings(List<List<ResearchNode>> layers)
        {
            float bestScore = float.MaxValue;
            List<ResearchNode[]> bestSnap = null;

            // ExactSmallLayerSweep의 OrderingScore 평가 비용 계측 (DevMode 전용)
            int sweepScoreEvals = 0;
            long sweepScoreElapsedTicks = 0L;

            for (int variant = 0; variant < OrderingVariantNames.Length; variant++)
            {
                if (variant > 0) Restore(layers, Snapshot(BuildLayers(variant)));
                int cross = RunOrderingLoop(layers);
                float score = OrderingScore(layers, cross);
                if (score < bestScore) // 동점이면 첫 번째(earlier) 변형 유지 — 결정성 보장
                {
                    bestScore = score;
                    bestSnap = Snapshot(layers);
                }
            }

            Restore(layers, bestSnap);

            int improvedLayers;
            ExactSmallLayerSweep(layers, out improvedLayers, ref sweepScoreEvals, ref sweepScoreElapsedTicks);

            // 큰 레이어(DP는 n≤10만)의 local optimum 탈출: vertex sifting (OrderingScore 가드로 높이 퇴행 방지)
            return SiftingPass(layers);
        }

        /// <summary>
        /// 단일 [wmedian → transpose] 루프. stale 카운터(루프 수렴)는 교차 수 기준, 스냅샷 채택은
        /// 매 반복 OrderingScore 기준 — 교차가 늘어도 높이가 크게 줄면 채택한다. 최선 스냅샷의 교차 수 반환.
        /// </summary>
        private int RunOrderingLoop(List<List<ResearchNode>> layers)
        {
            int initCross = TotalCrossings(layers);
            // bestScore=최소 OrderingScore, bestCross=그 스냅샷의 교차(반환), bestCrossSoFar=stale용 교차 하한
            float bestScore = OrderingScore(layers, initCross);
            int bestCross = initCross;
            int bestCrossSoFar = initCross;
            var bestOrder = Snapshot(layers);
            int stale = 0;

            for (int iter = 0;
                 iter < Constraints.LayoutOrderingMaxIterations && bestCrossSoFar > 0 && stale < Constraints.LayoutOrderingPatience;
                 iter++)
            {
                WMedian(layers, forward: iter % 2 == 0);
                Transpose(layers);

                int crossings = TotalCrossings(layers);

                // stale 카운터: 교차 수 기준 (루프 수렴 제어)
                if (crossings < bestCrossSoFar)
                {
                    bestCrossSoFar = crossings;
                    stale = 0;
                }
                else
                {
                    stale++;
                }

                // 스냅샷 채택 기준 = OrderingScore
                float score = OrderingScore(layers, crossings);
                if (score < bestScore) // 동점이면 earlier(더 낮은 교차) 스냅샷 유지 — 결정성
                {
                    bestScore = score;
                    bestCross = crossings;
                    bestOrder = Snapshot(layers);
                }
            }

            Restore(layers, bestOrder);
            return bestCross;
        }

        /// <summary>
        /// 작은 레이어(≤ LayoutExactLayerMaxNodes) 전수 최적화: 인접 레이어 고정, 레이어 내 순서를
        /// 부분집합 DP(dp[S] = 상단 |S|칸 최소 교차)로 전역 최소화. 인접쌍 스왑이 못 벗어나는 국소해 탈출.
        /// 레이어 교체는 전역 OrderingScore 개선 시에만 채택(높이 악화 시 롤백). 개선 없을 때까지 스윕.
        /// </summary>
        private int ExactSmallLayerSweep(List<List<ResearchNode>> layers, out int improvedLayers,
            ref int scoreEvalCount, ref long scoreElapsedTicks)
        {
            improvedLayers = 0;
            bool improvedAny = true;
            int guard = 0;
            while (improvedAny && guard++ < 4)
            {
                improvedAny = false;
                foreach (var layer in layers)
                {
                    int n = layer.Count;
                    if (n < 3 || n > Constraints.LayoutExactLayerMaxNodes) continue; // 2개는 transpose가 이미 최적

                    var cost = new long[n, n];
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            if (i != j) cost[i, j] = PairCrossings(layer[i], layer[j]);
                        }
                    }

                    long current = 0;
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = i + 1; j < n; j++) current += cost[i, j];
                    }
                    if (current == 0) continue; // 이미 무교차

                    int full = 1 << n;
                    var dp = new long[full];
                    var pick = new int[full];
                    for (int s = 1; s < full; s++) dp[s] = long.MaxValue;

                    for (int s = 0; s < full; s++)
                    {
                        if (dp[s] == long.MaxValue) continue;
                        for (int v = 0; v < n; v++)
                        {
                            if ((s & (1 << v)) != 0) continue;
                            long add = 0;
                            for (int u = 0; u < n; u++)
                            {
                                if ((s & (1 << u)) != 0) add += cost[u, v];
                            }
                            int ns = s | (1 << v);
                            if (dp[s] + add < dp[ns])
                            {
                                dp[ns] = dp[s] + add;
                                pick[ns] = v;
                            }
                        }
                    }

                    if (dp[full - 1] >= current) continue; // 교차 개선 없음 — 순서 유지

                    // DP가 교차 개선을 찾음 — 적용 전 전역 점수 기록 (DevMode이면 Stopwatch 계측)
                    float scoreBefore;
                    long t0 = 0L;
                    if (Prefs.DevMode)
                    {
                        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        scoreBefore = OrderingScore(layers, TotalCrossings(layers));
                        scoreEvalCount++;
                    }
                    else
                    {
                        scoreBefore = OrderingScore(layers, TotalCrossings(layers));
                    }

                    // 새 순서를 레이어에 임시 적용 (롤백용 원래 순서/VOrder 저장)
                    var prevOrder = layer.ToArray();
                    var prevVOrders = new int[n];
                    for (int i = 0; i < n; i++) prevVOrders[i] = prevOrder[i].VOrder;

                    var order = new ResearchNode[n];
                    int cur = full - 1;
                    for (int slot = n - 1; slot >= 0; slot--)
                    {
                        int v = pick[cur];
                        order[slot] = layer[v];
                        cur &= ~(1 << v);
                    }
                    layer.Clear();
                    layer.AddRange(order);
                    for (int i = 0; i < n; i++) layer[i].VOrder = i;

                    // 적용 후 전역 점수 평가
                    float scoreAfter;
                    if (Prefs.DevMode)
                    {
                        scoreAfter = OrderingScore(layers, TotalCrossings(layers));
                        scoreEvalCount++;
                        scoreElapsedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    }
                    else
                    {
                        scoreAfter = OrderingScore(layers, TotalCrossings(layers));
                    }

                    if (scoreAfter < scoreBefore) // 통합 점수 개선 — 채택 확정
                    {
                        improvedLayers++;
                        improvedAny = true;
                    }
                    else
                    {
                        // 교차는 줄었으나 높이 비용이 상쇄 — 롤백
                        layer.Clear();
                        layer.AddRange(prevOrder);
                        for (int i = 0; i < n; i++) layer[i].VOrder = prevVOrders[i];
                    }
                }
            }
            return TotalCrossings(layers);
        }

        private static List<ResearchNode[]> Snapshot(List<List<ResearchNode>> layers)
            => layers.Select(l => l.ToArray()).ToList();

        private static void Restore(List<List<ResearchNode>> layers, List<ResearchNode[]> snapshot)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                layers[i].Clear();
                layers[i].AddRange(snapshot[i]);
            }
            SyncVOrder(layers);
        }

        /// <summary>DOT의 wmedian 스윕 ("weighted"는 median value 보간을 뜻함 — 엣지 종류별 가중치 아님).</summary>
        private void WMedian(List<List<ResearchNode>> layers, bool forward)
        {
            if (forward)
            {
                for (int r = 1; r < layers.Count; r++) ReorderByMedian(layers[r], useUp: true);
            }
            else
            {
                for (int r = layers.Count - 2; r >= 0; r--) ReorderByMedian(layers[r], useUp: false);
            }
        }

        private void ReorderByMedian(List<ResearchNode> layer, bool useUp)
        {
            int n = layer.Count;
            if (n < 2) return;

            var medians = new float[n];
            for (int i = 0; i < n; i++)
            {
                var positions = (useUp ? Ups(layer[i]) : Downs(layer[i]))
                    .Select(u => u.VOrder).ToList();
                positions.Sort();
                medians[i] = MedianValue(positions);
            }

            // 이웃이 없는 노드(median < 0)는 제자리에 고정, 나머지를 median 순으로 안정 정렬
            var result = new ResearchNode[n];
            var movable = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (medians[i] < 0f) result[i] = layer[i];
                else movable.Add(i);
            }

            var sorted = movable.OrderBy(i => medians[i]).ToList(); // OrderBy는 안정 정렬
            int next = 0;
            for (int i = 0; i < n; i++)
            {
                if (result[i] == null) result[i] = layer[sorted[next++]];
            }

            layer.Clear();
            layer.AddRange(result);
            for (int i = 0; i < n; i++) layer[i].VOrder = i;
        }

        /// <summary>DOT 논문의 median value: 짝수 차수일 때 좌우 밀도로 보간</summary>
        private static float MedianValue(List<int> positions)
        {
            int n = positions.Count;
            if (n == 0) return -1f;

            int m = n / 2;
            if ((n & 1) == 1) return positions[m];
            if (n == 2) return (positions[0] + positions[1]) / 2f;

            int left = positions[m - 1] - positions[0];
            int right = positions[n - 1] - positions[m];
            if (left + right == 0) return (positions[m - 1] + positions[m]) / 2f;
            return (positions[m - 1] * right + positions[m] * left) / (float)(left + right);
        }

        private void Transpose(List<List<ResearchNode>> layers)
        {
            bool improved = true;
            int sweep = 0;

            while (improved && sweep++ < Constraints.LayoutTransposeMaxSweeps)
            {
                improved = false;
                foreach (var layer in layers)
                {
                    for (int i = 0; i + 1 < layer.Count; i++)
                    {
                        var v = layer[i];
                        var w = layer[i + 1];
                        if (PairCrossings(w, v) < PairCrossings(v, w))
                        {
                            layer[i] = w;
                            layer[i + 1] = v;
                            w.VOrder = i;
                            v.VOrder = i + 1;
                            improved = true;
                        }
                    }
                }
            }
        }

        /// <summary>a가 b 위(작은 VOrder)에 있을 때 두 노드의 엣지끼리 발생하는 교차 수</summary>
        /// <summary>
        /// Vertex sifting: 각 레이어의 노드를 레이어 내 모든 위치로 통과시키며(누적 교차 델타) 최소 교차 자리에
        /// 안착시킨다. 인접쌍 transpose나 소형 레이어 DP가 못 벗어나는 local optimum을 탈출 — 특히 큰 레이어.
        /// 레이어 변경은 전역 OrderingScore 개선 시에만 채택(높이 퇴행 롤백). 개선 없을 때까지 스윕.
        /// </summary>
        private int SiftingPass(List<List<ResearchNode>> layers)
        {
            bool improvedAny = true;
            int guard = 0;
            while (improvedAny && guard++ < Constraints.LayoutSiftingMaxPasses)
            {
                improvedAny = false;
                foreach (var layer in layers)
                {
                    int n = layer.Count;
                    if (n < 3) continue; // 2개 이하는 transpose가 이미 최적

                    var prevOrder = layer.ToArray();
                    var prevV = new int[n];
                    for (int i = 0; i < n; i++) prevV[i] = prevOrder[i].VOrder;
                    float scoreBefore = OrderingScore(layers, TotalCrossings(layers));

                    if (!SiftLayer(layer)) continue;

                    float scoreAfter = OrderingScore(layers, TotalCrossings(layers));
                    if (scoreAfter < scoreBefore - 1e-4f)
                    {
                        improvedAny = true;
                    }
                    else
                    {
                        layer.Clear();
                        layer.AddRange(prevOrder);
                        for (int i = 0; i < n; i++) layer[i].VOrder = prevV[i];
                    }
                }
            }
            return TotalCrossings(layers);
        }

        /// <summary>
        /// 한 레이어를 sifting: PairCrossings 비용행렬(레이어 내부 순서 무관)을 캐시한 뒤, 각 노드를 레이어 내
        /// 모든 위치로 슬라이드하며 누적 교차를 추적해 최소 자리로 옮긴다. 바뀌었으면 true.
        /// </summary>
        private bool SiftLayer(List<ResearchNode> layer)
        {
            int n = layer.Count;
            var nodes = layer.ToArray();
            var index = new Dictionary<ResearchNode, int>(n);
            for (int i = 0; i < n; i++) index[nodes[i]] = i;

            // cost[i,j] = nodes[i]가 nodes[j] 위(작은 VOrder)일 때 둘 사이 교차. 인접 레이어 VOrder만 보므로 불변.
            var cost = new int[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (i != j) cost[i, j] = PairCrossings(nodes[i], nodes[j]);

            bool changed = false;
            foreach (var v in nodes)
            {
                int vi = index[v];
                int curPos = layer.IndexOf(v);

                var others = new List<ResearchNode>(n - 1);
                foreach (var x in layer)
                    if (!ReferenceEquals(x, v)) others.Add(x);

                // v를 맨 위(p=0)에 둘 때 교차 = Σ cost[v, other]
                long running = 0;
                for (int j = 0; j < others.Count; j++) running += cost[vi, index[others[j]]];
                long best = running;
                int bestP = 0;
                // p가 늘면 v가 others[p-1] 아래로 — 그 쌍 기여가 cost[v,w] → cost[w,v]로 바뀜
                for (int p = 1; p <= others.Count; p++)
                {
                    int wi = index[others[p - 1]];
                    running += cost[wi, vi] - cost[vi, wi];
                    if (running < best) { best = running; bestP = p; }
                }

                if (bestP != curPos)
                {
                    layer.Clear();
                    for (int j = 0; j <= others.Count; j++)
                    {
                        if (j == bestP) layer.Add(v);
                        if (j < others.Count) layer.Add(others[j]);
                    }
                    for (int i = 0; i < layer.Count; i++) layer[i].VOrder = i;
                    changed = true;
                }
            }
            return changed;
        }

        private int PairCrossings(ResearchNode a, ResearchNode b)
        {
            int crossings = 0;
            foreach (var pa in Ups(a))
            {
                foreach (var pb in Ups(b))
                {
                    if (pa.VOrder > pb.VOrder) crossings++;
                }
            }
            foreach (var ca in Downs(a))
            {
                foreach (var cb in Downs(b))
                {
                    if (ca.VOrder > cb.VOrder) crossings++;
                }
            }
            return crossings;
        }

        private int TotalCrossings(List<List<ResearchNode>> layers)
        {
            int total = 0;
            for (int r = 0; r + 1 < layers.Count; r++)
            {
                total += CountCrossings(layers[r], layers[r + 1]);
            }
            return total;
        }

        /// <summary>두 인접 레이어 간 교차 수 — BIT(펜윅 트리) 역전쌍 카운트, O(E log V)</summary>
        private int CountCrossings(List<ResearchNode> upper, List<ResearchNode> lower)
        {
            if (upper.Count == 0 || lower.Count == 0) return 0;

            var sequence = new List<int>();
            foreach (var w in lower)
            {
                var ps = Ups(w).Select(p => p.VOrder).ToList();
                ps.Sort();
                sequence.AddRange(ps);
            }
            if (sequence.Count < 2) return 0;

            var bit = new int[upper.Count + 1];
            int inversions = 0, seen = 0;

            foreach (var x in sequence)
            {
                int lessOrEqual = 0;
                for (int i = x + 1; i > 0; i -= i & (-i)) lessOrEqual += bit[i];
                inversions += seen - lessOrEqual;

                for (int i = x + 1; i <= upper.Count; i += i & (-i)) bit[i]++;
                seen++;
            }
            return inversions;
        }

        /// <summary>랭크별 X 좌표 + TechLevel 경계에 간격/구분선 할당 (graph.TechLevelBoundaries 채움).</summary>
        private float[] AssignX(List<List<ResearchNode>> layers)
        {
            int rankCount = layers.Count;
            var rankX = new float[rankCount];

            graph.TechLevelBoundaries.Clear();

            float currentX = 0f;
            TechLevel lastTechLevel = TechLevel.Undefined;
            bool firstRank = true;

            for (int r = 0; r < rankCount; r++)
            {
                // 랭크 대표 TechLevel: 실제 노드의 유효 TechLevel 최대값 (더미/프록시 제외, 없으면 직전 값 유지)
                TechLevel tl = lastTechLevel;
                bool hasReal = false;
                foreach (var n in layers[r])
                {
                    if (n.IsDummy || n.IsProxy) continue;
                    hasReal = true;
                    if (effectiveTechLevel.TryGetValue(n, out var etl) && etl > tl) tl = etl;
                }
                // 프록시 전용 컬럼만 원본 시대를 구역에 반영 (실노드 랭크에선 제외 유지).
                // 시대 없는 그래프에선 프록시도 기여 안 함 — 외부 선행 하나로 전체가 칠해지는 문제 방지.
                if (!hasReal && graphHasEras)
                {
                    foreach (var n in layers[r])
                    {
                        if (!n.IsProxy) continue;
                        if (n.TechLevel > tl) tl = n.TechLevel;
                    }
                }
                // 분리해 둔 고립 노드도 포함 (밴드 시작 컬럼이 고립 노드뿐일 수 있음)
                if (isolatedNodesByRank.TryGetValue(r, out var isolatedHere))
                {
                    foreach (var n in isolatedHere)
                    {
                        if (effectiveTechLevel.TryGetValue(n, out var etl) && etl > tl) tl = etl;
                    }
                }

                if (!firstRank && tl > lastTechLevel)
                {
                    float boundaryX = currentX + Constraints.NodeSpacing.x * Constraints.LayoutTechLevelBoundaryOffset;
                    graph.TechLevelBoundaries[tl] = boundaryX;
                    currentX += Constraints.NodeSpacing.x * Constraints.LayoutTechLevelGapMultiplier;
                }
                else
                {
                    currentX += Constraints.NodeSpacing.x;
                }

                if (firstRank)
                {
                    currentX = 0f;
                    // 첫 시대도 경계 등록 — 그래프 시작 시대(원시 등)에도 구분선/라벨/구역 틴트가 표시되게 함
                    if (tl != TechLevel.Undefined)
                    {
                        graph.TechLevelBoundaries[tl] =
                            -Constraints.NodeSpacing.x * Constraints.LayoutTechLevelBoundaryOffset;
                    }
                }

                rankX[r] = currentX;
                currentX += Constraints.NodeSize.x;

                lastTechLevel = tl;
                firstRank = false;
            }

            return rankX;
        }

        private void AssignY(List<List<ResearchNode>> layers, float[] rankX)
        {
            float gridY = Constraints.NodeSize.y + Constraints.NodeSpacing.y;

            // Y 좌표 배정 (SpringRelaxY) — 실+더미 전체 finalY 반환
            var finalY = SpringRelaxY(layers);

            // 실제 노드 기준 수직 중앙 정렬. 스냅은 실제 노드만(더미는 좁은 통로 간격 유지).
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var node in graph.Nodes)
            {
                if (node.IsDummy || isolatedNodesSet.Contains(node)) continue;
                float y = finalY[node];
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            float mid = (minY <= maxY) ? (minY + maxY) / 2f : 0f;

            foreach (var node in graph.Nodes)
            {
                if (isolatedNodesSet.Contains(node)) continue;
                float y = finalY[node] - mid;
                // round-half-up 유지 — Mathf.Round는 은행가 반올림이라 x.5 경계 두 노드가 한 행으로 합쳐짐
                float placed = node.IsDummy ? y : Mathf.Floor(y / gridY + 0.5f) * gridY;
                node.Position = new Vector2(rankX[node.Rank], placed);
            }

            // 더미 체인 성형 → 잔여 분리 위반 해소 (실제 노드는 그리드 고정, 더미만 이동)
            ShapeChains(layers);
            ResolveDummyRuns(layers);
        }

        /// <summary>
        /// 반복 바리센터 스프링 + PAV 투영으로 모든 노드(실+더미)의 Y를 결정한다
        /// </summary>
        private Dictionary<ResearchNode, float> SpringRelaxY(List<List<ResearchNode>> layers)
        {
            float selfAnchor = Constraints.LayoutSpringSelfAnchor;
            int passes = Constraints.LayoutSpringPasses;
            // Tikhonov 중심 정규화 λ_base: barycenter 분모에만 더해 각 노드를 0(중심선)으로 당긴다 → 드리프트·높이↓.
            // 분자엔 0을 더하는 셈이라 엣지 항과 조인트로 최소화(후처리 시프트가 아니라 단일 볼록해 → fault-line 없음).
            // 매 패스 높이 슬랙에 비례해 lambdaEff로 스케일 → 바닥 그래프는 자동 무효, 여유 그래프만 압축.
            float centerAnchor = Constraints.LayoutSpringCenterAnchor;

            // 1단계: 레이어별 비겹침 초기 Y
            var finalY = new Dictionary<ResearchNode, float>(graph.Nodes.Count);
            foreach (var layer in layers)
            {
                float y = 0f;
                for (int i = 0; i < layer.Count; i++)
                {
                    if (i > 0) y += SeparationOf(layer[i - 1], layer[i]);
                    finalY[layer[i]] = y;
                }
            }

            // 2단계: K 반복 Gauss-Seidel 스프링 + PAV 투영
            // desired 배열: 레이어 임시 버퍼 (최대 레이어 크기만큼 재사용)
            int maxLayerSize = 0;
            foreach (var layer in layers)
                if (layer.Count > maxLayerSize) maxLayerSize = layer.Count;

            var desired = new float[maxLayerSize];
            var prefix = new float[maxLayerSize];

            // 트리 높이 하한 H_floor = 가장 빽빽한 레이어의 박스 스팬 (어떤 배치도 이보다 낮을 수 없음).
            // centerAnchor를 이 슬랙(여유)에 비례시켜, 바닥에 가까운 그래프(Unified)는 λ→0(슬랜트 무손해),
            // 드리프트 여유가 있는 그래프(vanMain)만 당긴다. 매 패스 현재 높이로 재계산 → 컴팩트해질수록 λ 자동 감소(자기조절).
            float hFloor = 0f;
            foreach (var layer in layers)
            {
                if (layer.Count == 0) continue;
                float span = 0f;
                for (int i = 1; i < layer.Count; i++) span += SeparationOf(layer[i - 1], layer[i]);
                float box = span + NodeHeightOf(layer[0]) / 2f + NodeHeightOf(layer[layer.Count - 1]) / 2f;
                if (box > hFloor) hFloor = box;
            }

            float CurHeight()
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var kv in finalY)
                {
                    float half = NodeHeightOf(kv.Key) / 2f;
                    if (kv.Value - half < lo) lo = kv.Value - half;
                    if (kv.Value + half > hi) hi = kv.Value + half;
                }
                return hi >= lo ? hi - lo : 0f;
            }

            for (int pass = 0; pass < passes; pass++)
            {
                // 패스마다 레이어 순회 방향 교대 (빠른 수렴)
                bool forward = (pass % 2 == 0);
                int layerCount = layers.Count;

                // 적응형 λ: 현재 높이가 H_floor 대비 얼마나 여유 있나 → [0,1] 슬랙비. 여유 없으면 λ→0.
                float slackRatio = hFloor > 1e-3f ? Mathf.Clamp((CurHeight() - hFloor) / hFloor, 0f, 1f) : 0f;
                float lambdaEff = centerAnchor * slackRatio;

                for (int li = 0; li < layerCount; li++)
                {
                    int layerIdx = forward ? li : (layerCount - 1 - li);
                    var layer = layers[layerIdx];
                    int n = layer.Count;
                    if (n == 0) continue;

                    // desired[v] = (현재Y·selfAnchor + Σ 이웃Y) / (selfAnchor + centerAnchor + 차수)
                    // centerAnchor는 분모에만 → 0 쪽 Tikhonov 항 (높이/드리프트 억제, 엣지항과 조인트 최소화)
                    for (int i = 0; i < n; i++)
                    {
                        var v = layer[i];
                        float sum = finalY[v] * selfAnchor;
                        float weight = selfAnchor + lambdaEff;

                        foreach (var u in Ups(v))
                        {
                            sum += finalY[u];
                            weight += 1f;
                        }
                        foreach (var u in Downs(v))
                        {
                            sum += finalY[u];
                            weight += 1f;
                        }

                        desired[i] = sum / weight; // degree==0이면 selfAnchor만 남아 desired = currentY
                    }

                    // PAV 투영: 가변 갭 prefix를 빼 잔차 d[i]=desired[i]−prefix[i]로 만들고(desired에 덮어씀),
                    // 비감소 등온 회귀 → 다시 prefix를 더해 복원. y[i+1] ≥ y[i] + sep 보장.
                    prefix[0] = 0f;
                    for (int i = 1; i < n; i++)
                        prefix[i] = prefix[i - 1] + SeparationOf(layer[i - 1], layer[i]);

                    for (int i = 0; i < n; i++)
                        desired[i] -= prefix[i];

                    SpringPavProject(desired, n);

                    // 음수 클램프 금지: 음수 Y가 정상 좌표계라 y≥0 강제는 레이어 간 정렬을 깬다
                    for (int i = 0; i < n; i++)
                    {
                        finalY[layer[i]] = desired[i] + prefix[i];
                    }
                }
            }

            return finalY;
        }


        /// <summary>
        /// 균일-가중 등온 PAV (비감소 등온 회귀, L2 최소화): buf[0..count-1]를 in-place로 비감소
        /// 블록 평균 수열로 변환한다. 블록 스택 유지, 단조 위반 시 가중 평균 머지, O(n) amortized.
        /// </summary>
        private static void SpringPavProject(float[] buf, int count)
        {
            // 블록 스택 (start, end, sum, weight) — 상한 = count
            var sStart  = new int[count];
            var sEnd    = new int[count];
            var sSum    = new float[count];
            var sWeight = new float[count];
            int top = 0; // 스택 사용 수

            for (int i = 0; i < count; i++)
            {
                // 새 원소를 단일 블록으로 푸시
                sStart[top]  = i;
                sEnd[top]    = i;
                sSum[top]    = buf[i];
                sWeight[top] = 1f;
                top++;

                // 위반 머지: 앞 블록 avg > 뒤 블록 avg
                while (top >= 2)
                {
                    float avgPrev = sSum[top - 2] / sWeight[top - 2];
                    float avgCur  = sSum[top - 1] / sWeight[top - 1];
                    if (avgPrev <= avgCur + 1e-6f) break; // 단조 OK

                    // 머지: 앞 블록에 뒤 블록 흡수 (sStart[top-2]는 그대로)
                    sEnd[top - 2]    = sEnd[top - 1];
                    sSum[top - 2]   += sSum[top - 1];
                    sWeight[top - 2] += sWeight[top - 1];
                    top--;
                }
            }

            // 각 블록 원소를 블록 평균으로 덮어씀 (음수 클램프 없음 — 잔차는 음수 가능)
            for (int b = 0; b < top; b++)
            {
                float avg = sSum[b] / sWeight[b];
                for (int i = sStart[b]; i <= sEnd[b]; i++)
                    buf[i] = avg;
            }
        }

        /// <summary>DevMode: 시각적 겹침(노드 박스 침범)만 검사 — 스냅 ±gridY/2 오차는 허용.</summary>
        private void ValidateLayerSeparation(List<List<ResearchNode>> layers)
        {
            if (!Prefs.DevMode) return;

            foreach (var layer in layers)
            {
                for (int i = 1; i < layer.Count; i++)
                {
                    float visualMin = (NodeHeightOf(layer[i - 1]) + NodeHeightOf(layer[i])) / 2f - 0.5f;
                    if (layer[i].Position.y - layer[i - 1].Position.y < visualMin)
                    {
                        Log.Warning($"[YART] Layout: overlap in rank {layer[i].Rank}: " +
                                    $"'{layer[i - 1].Label}' y={layer[i - 1].Position.y} / '{layer[i].Label}' y={layer[i].Position.y}");
                    }
                }
            }
        }

        /// <summary>패킹 유닛: 실노드 1개 또는 더미 체인 전체. EstimateMinHeight의 최소 높이 추정에서 사용.</summary>
        private class LayoutUnit
        {
            public readonly List<ResearchNode> Members = new List<ResearchNode>();
            public readonly List<(LayoutUnit unit, float sep)> Above = new List<(LayoutUnit, float)>();
            public readonly List<(LayoutUnit unit, float sep)> Below = new List<(LayoutUnit, float)>();
            public readonly List<LayoutUnit> Linked = new List<LayoutUnit>(); // 엣지로 연결된 유닛
        }

        /// <summary>패킹 유닛 그래프 구성: 유닛 생성 + 레이어 내 인접 분리 제약(Above/Below)과 엣지 연결(Linked).</summary>
        private List<LayoutUnit> BuildUnits(List<List<ResearchNode>> layers, out Dictionary<ResearchNode, LayoutUnit> unitOf)
        {
            unitOf = new Dictionary<ResearchNode, LayoutUnit>();
            var units = new List<LayoutUnit>();

            foreach (var layer in layers)
            {
                foreach (var node in layer)
                {
                    if (unitOf.ContainsKey(node)) continue;

                    var unit = new LayoutUnit();
                    if (node.IsDummy)
                    {
                        // 체인 머리까지 거슬러 올라간 뒤 트렁크 전체 수집 (위 in-degree 1, 아래 NextTrunkDown)
                        var head = node;
                        while (true)
                        {
                            var p = Ups(head).FirstOrDefault();
                            if (p != null && p.IsDummy) head = p;
                            else break;
                        }
                        var cur = head;
                        while (cur != null)
                        {
                            unit.Members.Add(cur);
                            unitOf[cur] = unit;
                            cur = NextTrunkDown(cur);
                        }
                    }
                    else
                    {
                        unit.Members.Add(node);
                        unitOf[node] = unit;
                    }
                    units.Add(unit);
                }
            }

            foreach (var layer in layers)
            {
                for (int i = 1; i < layer.Count; i++)
                {
                    var ua = unitOf[layer[i - 1]];
                    var ub = unitOf[layer[i]];
                    if (ReferenceEquals(ua, ub)) continue;
                    float sep = SeparationOf(layer[i - 1], layer[i]);
                    ua.Below.Add((ub, sep));
                    ub.Above.Add((ua, sep));
                }

                foreach (var node in layer)
                {
                    var un = unitOf[node];
                    foreach (var child in Downs(node))
                    {
                        if (!unitOf.TryGetValue(child, out var uc) || ReferenceEquals(un, uc)) continue;
                        un.Linked.Add(uc);
                        uc.Linked.Add(un);
                    }
                }
            }
            return units;
        }

        /// <summary>
        /// 현재 레이어 순서의 최소 배치 높이 추정 (유닛 분리 제약의 packUp 임계 경로). 멀티 스타트
        /// 선택 점수의 높이 항(LayoutOrderingHeightWeight). 사이클은 결정적으로 끊어 항상 비교 가능.
        /// </summary>
        private float EstimateMinHeight(List<List<ResearchNode>> layers)
        {
            Dictionary<ResearchNode, LayoutUnit> unitOf;
            var units = BuildUnits(layers, out unitOf);
            if (units.Count == 0) return 0f;

            // AssignY 이전이라 좌표 없음 — 스윕 순서는 사이클 허용 위상 순서
            int skipped;
            var yUp = SweepPackUp(CycleTolerantUnitOrder(units), out skipped);
            float max = 0f;
            foreach (var v in yUp.Values)
            {
                if (v > max) max = v;
            }
            return max;
        }

        /// <summary>유닛 분리 제약 그래프의 사이클 허용 위상 순서 (큐가 마르면 units 인덱스 최소 유닛을 강제 해제).</summary>
        private static List<LayoutUnit> CycleTolerantUnitOrder(List<LayoutUnit> units)
        {
            var indeg = new Dictionary<LayoutUnit, int>(units.Count);
            foreach (var u in units) indeg[u] = u.Above.Count;

            var order = new List<LayoutUnit>(units.Count);
            var ordered = new HashSet<LayoutUnit>();
            var ready = new Queue<LayoutUnit>();
            foreach (var u in units)
            {
                if (indeg[u] == 0) ready.Enqueue(u);
            }

            int cursor = 0;
            while (order.Count < units.Count)
            {
                LayoutUnit next = null;
                while (ready.Count > 0)
                {
                    // 강제 해제된 유닛이 뒤늦게 indeg 0이 되어 다시 들어올 수 있다 — 중복 무시
                    var c = ready.Dequeue();
                    if (!ordered.Contains(c)) { next = c; break; }
                }
                if (next == null)
                {
                    while (ordered.Contains(units[cursor])) cursor++;
                    next = units[cursor];
                }

                ordered.Add(next);
                order.Add(next);
                foreach (var (below, sep) in next.Below)
                {
                    if (--indeg[below] == 0 && !ordered.Contains(below)) ready.Enqueue(below);
                }
            }
            return order;
        }

        /// <summary>처리 순서를 따라 위쪽 제약만 누적하는 packUp 스윕 (미처리 위 파트너 제약은 건너뛰고 개수만 카운트).</summary>
        private static Dictionary<LayoutUnit, float> SweepPackUp(List<LayoutUnit> order, out int skippedPairs)
        {
            var yUp = new Dictionary<LayoutUnit, float>(order.Count);
            skippedPairs = 0;
            foreach (var u in order)
            {
                float v = 0f;
                foreach (var (above, sep) in u.Above)
                {
                    float ya;
                    if (yUp.TryGetValue(above, out ya))
                    {
                        if (ya + sep > v) v = ya + sep;
                    }
                    else
                    {
                        skippedPairs++;
                    }
                }
                yUp[u] = v;
            }
            return yUp;
        }

        /// <summary>모든 더미 체인(트렁크 — 머리→꼬리 순)을 수집. NextTrunkDown으로 트렁크만 따라간다.</summary>
        private List<List<ResearchNode>> CollectChains()
        {
            var chains = new List<List<ResearchNode>>();
            var seen = new HashSet<ResearchNode>();
            foreach (var n in graph.Nodes)
            {
                if (!n.IsDummy || seen.Contains(n)) continue;

                var head = n;
                while (true)
                {
                    var p = Ups(head).FirstOrDefault();
                    if (p != null && p.IsDummy) head = p;
                    else break;
                }
                var chain = new List<ResearchNode>();
                var cur = head;
                while (cur != null)
                {
                    chain.Add(cur);
                    seen.Add(cur);
                    cur = NextTrunkDown(cur);
                }
                chains.Add(chain);
            }
            return chains;
        }

        /// <summary>UntangleChains의 멀티-랭크 스왑 단위 = 더미 체인(+ 단일 타깃이면 종착 실노드; 분기 트렁크는 더미만).</summary>
        private class Strip
        {
            public readonly List<ResearchNode> Members = new List<ResearchNode>(); // 연속 랭크 순
            public int StartRank;
            public float DestY; // 목적지 행 = 분기 타깃들의 median 행 (첫 AssignY 결과, 단일 타깃이면 그 행)
            public int EndRank => StartRank + Members.Count - 1;
            public ResearchNode MemberAt(int rank) => Members[rank - StartRank];
        }

        /// <summary>
        /// 순서 단계가 남긴 "목적지 행 역전" 해소: 첫 AssignY 행 좌표로 역전을 찾아 인접 공유 구간을
        /// 통째로 스왑하고, 경계 교차/통로 수용량이 악화되면 되돌린다. 바뀌었으면 true (호출자가
        /// ShapeChains+ResolveDummyRuns만 재실행, 실노드는 고정).
        /// </summary>
        private bool UntangleChains(List<List<ResearchNode>> layers)
        {
            var strips = new List<Strip>();
            foreach (var chain in CollectChains())
            {
                var targets = TrunkTargets(chain);
                if (targets.Count == 0) continue; // 방어: 종착 없는 체인
                var strip = new Strip { StartRank = chain[0].Rank };
                strip.Members.AddRange(chain);
                if (targets.Count == 1)
                {
                    // 단일 타깃: 종착 실노드까지 스트립에 포함 (꼬리 더미 다음 랭크의 자리도 스왑 대상)
                    strip.Members.Add(targets[0]);
                    strip.DestY = targets[0].Position.y;
                }
                else
                {
                    // 분기 트렁크: 단일 종착 없음 — 더미 구간만 스왑(타깃은 고정), 목적지는 median 행
                    strip.DestY = TrunkDestY(chain);
                }
                strips.Add(strip);
            }
            if (strips.Count < 2) return false;

            // 버블링(한 체인이 여러 이웃을 연쇄로 지나침)을 위해 넉넉히 — 안정되면 조기 종료
            int maxPasses = Mathf.Min(16, strips.Count);
            bool changed = false;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                bool swappedThisPass = false;
                for (int i = 0; i < strips.Count; i++)
                {
                    for (int j = i + 1; j < strips.Count; j++)
                    {
                        if (TryUntanglePair(layers, strips[i], strips[j])) swappedThisPass = true;
                    }
                }
                if (swappedThisPass) changed = true;
                else break;
            }
            return changed;
        }

        /// <summary>
        /// 두 스트립이 인접한 연속 구간마다, 위 스트립의 목적지 행이 아래 스트립보다
        /// 낮으면(역전) 구간 전체를 스왑해 보고 경계 교차가 늘지 않을 때만 채택한다.
        /// </summary>
        private bool TryUntanglePair(List<List<ResearchNode>> layers, Strip a, Strip b)
        {
            int first = Mathf.Max(a.StartRank, b.StartRank);
            int last = Mathf.Min(a.EndRank, b.EndRank);
            if (last < first) return false;

            bool swapped = false;
            int r = first;
            while (r <= last)
            {
                var ma = a.MemberAt(r);
                var mb = b.MemberAt(r);
                int diff = mb.VOrder - ma.VOrder;
                if (ReferenceEquals(ma, mb) || (diff != 1 && diff != -1)) { r++; continue; }

                // 같은 방향으로 인접이 유지되는 연속 구간 [r, end)
                int end = r + 1;
                while (end <= last)
                {
                    var na = a.MemberAt(end);
                    var nb = b.MemberAt(end);
                    if (ReferenceEquals(na, nb) || nb.VOrder - na.VOrder != diff) break;
                    end++;
                }

                var upper = diff > 0 ? a : b;
                var lower = diff > 0 ? b : a;
                if (upper.DestY > lower.DestY + 0.5f) // 위 스트립이 더 아래 행으로 가려 함 — 역전
                {
                    int beforeCross = BoundaryCrossings(layers, r, end - 1);
                    int beforeBad = InfeasibleRuns(layers, r, end - 1);
                    SwapRun(layers, a, b, r, end - 1);
                    // 교차가 늘거나 수용량 없는 좁은 통로로 밀려 들어가면(겹침 영구화) 거부
                    if (BoundaryCrossings(layers, r, end - 1) > beforeCross
                        || InfeasibleRuns(layers, r, end - 1) > beforeBad)
                    {
                        SwapRun(layers, a, b, r, end - 1); // 되돌림
                    }
                    else
                    {
                        swapped = true;
                    }
                }
                r = end;
            }
            return swapped;
        }

        /// <summary>[s..e]에서 위/아래가 실노드로 막힌 더미 런 중 완화 간격으로도 안 들어가는(수용량 초과) 런의 수.</summary>
        private int InfeasibleRuns(List<List<ResearchNode>> layers, int s, int e)
        {
            int bad = 0;
            for (int r = Mathf.Max(0, s); r <= Mathf.Min(layers.Count - 1, e); r++)
            {
                var layer = layers[r];
                int i = 0;
                while (i < layer.Count)
                {
                    if (!layer[i].IsDummy) { i++; continue; }
                    int start = i;
                    while (i < layer.Count && layer[i].IsDummy) i++;

                    var above = start > 0 ? layer[start - 1] : null;
                    var below = i < layer.Count ? layer[i] : null;
                    if (above == null || below == null) continue; // 한쪽이 열려 있으면 항상 수용 가능

                    float need = SeparationOf(above, layer[start]) - RelaxOf(above, layer[start]);
                    for (int j = start + 1; j < i; j++)
                    {
                        need += SeparationOf(layer[j - 1], layer[j]) - RelaxOf(layer[j - 1], layer[j]);
                    }
                    need += SeparationOf(layer[i - 1], below) - RelaxOf(layer[i - 1], below);
                    if (above.Position.y + need > below.Position.y + 0.5f) bad++;
                }
            }
            return bad;
        }

        /// <summary>구간 [s..e]의 양 끝 바깥 경계를 포함한 인접 레이어 교차 수 합.</summary>
        private int BoundaryCrossings(List<List<ResearchNode>> layers, int s, int e)
        {
            int from = Mathf.Max(0, s - 1);
            int to = Mathf.Min(layers.Count - 2, e);
            int total = 0;
            for (int r = from; r <= to; r++)
            {
                total += CountCrossings(layers[r], layers[r + 1]);
            }
            return total;
        }

        /// <summary>구간 [s..e]에서 두 스트립의 멤버 자리를 레이어/VOrder 양쪽 모두 교환.</summary>
        private static void SwapRun(List<List<ResearchNode>> layers, Strip a, Strip b, int s, int e)
        {
            for (int r = s; r <= e; r++)
            {
                var ma = a.MemberAt(r);
                var mb = b.MemberAt(r);
                var layer = layers[r];
                int ia = ma.VOrder, ib = mb.VOrder;
                layer[ia] = mb;
                layer[ib] = ma;
                ma.VOrder = ib;
                mb.VOrder = ia;
            }
        }

        /// <summary>전체 레이어에서 인접 쌍이 SeparationOf 미만으로 겹친 쌍의 수.</summary>
        private int CountSeparationViolations(List<List<ResearchNode>> layers)
        {
            int count = 0;
            foreach (var layer in layers)
            {
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    float gap = layer[i + 1].Position.y - layer[i].Position.y;
                    if (gap < SeparationOf(layer[i], layer[i + 1]) - 0.5f) count++;
                }
            }
            return count;
        }

        /// <summary>체인 목록 전체의 bend 수 (= 더미 Y 시퀀스 ΔY 부호 전환). ShapeChains 패스별 진동 비용으로도 사용.</summary>
        private int CountAllChainBends(List<List<ResearchNode>> chains)
        {
            float eps = Constraints.MetricsBendEpsilon;
            int total = 0;
            foreach (var chain in chains)
            {
                var src = Ups(chain[0]).FirstOrDefault();
                if (src == null) continue;
                float prev = src.Position.y;
                int prevSign = 0;
                foreach (var d in chain)
                {
                    float dy = d.Position.y - prev;
                    prev = d.Position.y;
                    if (Mathf.Abs(dy) < eps) continue;
                    int sign = dy > 0 ? 1 : -1;
                    if (prevSign != 0 && sign != prevSign) total++;
                    prevSign = sign;
                }
            }
            return total;
        }

        /// <summary>더미 Y를 LayoutChainHashQuantum으로 양자화한 결정적 해시 (FNV-1a) — ShapeChains 사이클 감지용.</summary>
        private static int HashDummyPositions(List<List<ResearchNode>> chains)
        {
            float q = Constraints.LayoutChainHashQuantum;
            // FNV-1a 32bit
            uint hash = 2166136261u;
            foreach (var chain in chains)
            {
                foreach (var d in chain)
                {
                    int qi = (int)Mathf.Round(d.Position.y / q);
                    hash = (hash ^ (uint)qi) * 16777619u;
                }
            }
            return (int)hash;
        }

        /// <summary>모든 더미 Y를 chain 순 flat array로 스냅샷 (복원은 RestoreDummySnapshot).</summary>
        private static float[] SnapshotDummyPositions(List<List<ResearchNode>> chains)
        {
            int total = 0;
            foreach (var chain in chains) total += chain.Count;
            var snap = new float[total];
            int idx = 0;
            foreach (var chain in chains)
                foreach (var d in chain)
                    snap[idx++] = d.Position.y;
            return snap;
        }

        /// <summary>T2.2: SnapshotDummyPositions로 저장한 스냅샷을 노드에 복원.</summary>
        private static void RestoreDummySnapshot(List<List<ResearchNode>> chains, float[] snap)
        {
            int idx = 0;
            foreach (var chain in chains)
                foreach (var d in chain)
                {
                    float y = snap[idx++];
                    if (y != d.Position.y)
                        d.Position = new Vector2(d.Position.x, y);
                }
        }

        /// <summary>
        /// 스냅 이후 체인 성형: 더미 체인을 랭크별 이동 창 안에서 최소 꺾임 폴리라인(suffix/prefix 휴리스틱)으로
        /// 맞춘다. 이웃 체인도 함께 움직여 왕복 스윕으로 수렴. 패스별 bend 해시로 사이클 감지·최저 bend
        /// 스냅샷 보존(비정상 종료 시 복원). 잔여 위반은 ResolveDummyRuns 몫.
        /// </summary>
        private void ShapeChains(List<List<ResearchNode>> layers)
        {
            var chains = CollectChains();
            if (chains.Count == 0)
            {
                lastNonConvergedChains = 0;
                return;
            }

            // 초기 상태를 "best" 스냅샷으로 설정
            int bestBend = CountAllChainBends(chains);
            float[] bestSnap = SnapshotDummyPositions(chains);
            var seenHashes = new HashSet<int>();
            seenHashes.Add(HashDummyPositions(chains));

            bool cycleDetected = false;
            int finalMovedCount = 0;

            // 수렴 루프: 최대 진폭이 eps 아래로 떨어질 때까지 (또는 패스 상한)
            for (int pass = 0; pass < Constraints.LayoutChainShapeMaxPasses; pass++)
            {
                bool forward = pass % 2 == 0;
                int movedCount = 0;
                for (int i = 0; i < chains.Count; i++)
                {
                    var chain = chains[forward ? i : chains.Count - 1 - i];
                    float delta = FitChain(chain, layers);
                    if (delta > Constraints.LayoutChainConvergeEps) movedCount++;
                }
                finalMovedCount = movedCount;

                // 패스 후 bend 비용 및 해시 갱신 (strict < — 최초 최소 bend 상태 유지)
                int curBend = CountAllChainBends(chains);
                if (curBend < bestBend)
                {
                    bestBend = curBend;
                    bestSnap = SnapshotDummyPositions(chains);
                }

                if (movedCount == 0) break; // 수렴 — 현재 상태가 곧 결과

                // 2-cycle 감지: 상태 해시가 이전에 본 것과 같으면 사이클
                int h = HashDummyPositions(chains);
                if (!seenHashes.Add(h))
                {
                    cycleDetected = true;
                    break;
                }
            }

            // 안전망 복원은 비정상 종료(사이클/패스 소진) 시에만 — 정상 수렴은 고정점이라 손대지 않는다
            // (bend 동률이어도 위치가 다른 이전 스냅샷으로 덮으면 회귀).
            bool abnormalExit = cycleDetected || finalMovedCount > 0;
            if (abnormalExit) RestoreDummySnapshot(chains, bestSnap);

            lastNonConvergedChains = (finalMovedCount == 0 && !cycleDetected) ? 0 : finalMovedCount;
        }

        /// <summary>
        /// 체인 하나를 랭크별 창 [lo, hi] 안에서 최소 꺾임으로 배치 (suffix=도착 행 / prefix=출발 행 휴리스틱).
        /// 이번 호출에서 움직인 멤버의 최대 |dY| 반환 (LayoutChainConvergeEps 초과 = 미수렴).
        /// </summary>
        private float FitChain(List<ResearchNode> chain, List<List<ResearchNode>> layers)
        {
            int n = chain.Count;
            var src = Ups(chain[0]).FirstOrDefault();
            float dstY = TrunkDestY(chain);
            if (src == null || float.IsNaN(dstY)) return 0f; // 방어: 소스 없음 / 실타깃 0개
            float srcY = src.Position.y;

            // 랭크별 창: 같은 레이어 위/아래 이웃과의 분리 제약 (실노드 고정, 더미는 현재 위치)
            var lo = new float[n];
            var hi = new float[n];
            for (int j = 0; j < n; j++)
            {
                var d = chain[j];
                var layer = layers[d.Rank];
                int idx = d.VOrder; // SyncVOrder가 레이어 인덱스와의 동기화를 보장
                lo[j] = idx > 0
                    ? layer[idx - 1].Position.y + SeparationOf(layer[idx - 1], d)
                    : float.NegativeInfinity;
                hi[j] = idx + 1 < layer.Count
                    ? layer[idx + 1].Position.y - SeparationOf(d, layer[idx + 1])
                    : float.PositiveInfinity;
            }

            // suffix/prefix 휴리스틱: 도착 행이 들어가는 최대 접미사 + 그 앞 출발 행이 들어가는 최대 접두사
            int sufFrom = n;
            for (int j = n - 1; j >= 0 && lo[j] <= dstY && dstY <= hi[j]; j--) sufFrom = j;
            int preTo = 0;
            for (int j = 0; j < sufFrom && lo[j] <= srcY && srcY <= hi[j]; j++) preTo = j + 1;

            float prev = srcY;
            float maxDelta = 0f;
            for (int j = 0; j < n; j++)
            {
                float target = j >= sufFrom ? dstY : j < preTo ? srcY : prev;
                float computedY = lo[j] <= hi[j] ? Mathf.Clamp(target, lo[j], hi[j]) : (lo[j] + hi[j]) / 2f;
                prev = computedY;
                float y = computedY;
                float delta = Mathf.Abs(y - chain[j].Position.y);
                if (delta > maxDelta) maxDelta = delta;
                if (y != chain[j].Position.y) chain[j].Position = new Vector2(chain[j].Position.x, y);
            }
            return maxDelta;
        }

        /// <summary>스냅 이후 잔여 분리 위반 해소: 연속 더미 구간을 위/아래 실노드 사이 창에 클램프(안 되면 완화 간격 재시도).</summary>
        private void ResolveDummyRuns(List<List<ResearchNode>> layers)
        {
            foreach (var layer in layers)
            {
                int i = 0;
                while (i < layer.Count)
                {
                    if (!layer[i].IsDummy) { i++; continue; }
                    int start = i;
                    while (i < layer.Count && layer[i].IsDummy) i++;

                    var above = start > 0 ? layer[start - 1] : null;
                    var below = i < layer.Count ? layer[i] : null;
                    if (!TryClampDummyRun(layer, start, i - start, above, below, relaxed: false))
                    {
                        TryClampDummyRun(layer, start, i - start, above, below, relaxed: true);
                    }
                }
            }
        }

        /// <summary>
        /// 더미 구간을 경계 노드 사이에 전방 스윕 클램프(현재 위치 최대 유지, 위반만 해소).
        /// relaxed면 RelaxOf만큼 빼 시각적 최소 간격으로 시도. 창이 역전되면 변경 없이 false.
        /// </summary>
        private static bool TryClampDummyRun(List<ResearchNode> layer, int start, int count,
            ResearchNode above, ResearchNode below, bool relaxed)
        {
            float Sep(ResearchNode a, ResearchNode b)
                => SeparationOf(a, b) - (relaxed ? RelaxOf(a, b) : 0f);

            // 전방 누적 최소(lo) / 후방 누적 최대(hi) — 경계 실노드는 고정
            var lo = new float[count];
            var hi = new float[count];
            for (int j = 0; j < count; j++)
            {
                lo[j] = j == 0
                    ? (above != null ? above.Position.y + Sep(above, layer[start]) : float.NegativeInfinity)
                    : lo[j - 1] + Sep(layer[start + j - 1], layer[start + j]);
            }
            for (int j = count - 1; j >= 0; j--)
            {
                hi[j] = j == count - 1
                    ? (below != null ? below.Position.y - Sep(layer[start + j], below) : float.PositiveInfinity)
                    : hi[j + 1] - Sep(layer[start + j], layer[start + j + 1]);
            }
            // 타당성 사전 검사 — 역전된 창이 하나라도 있으면 배치 불가, 변경 없이 false
            for (int j = 0; j < count; j++)
            {
                if (lo[j] > hi[j]) return false;
            }

            float prevPlaced = 0f;
            for (int j = 0; j < count; j++)
            {
                var d = layer[start + j];
                float floorY = lo[j];
                if (j > 0)
                {
                    float sep = Sep(layer[start + j - 1], d);
                    if (prevPlaced + sep > floorY) floorY = prevPlaced + sep;
                }
                float yNew = Mathf.Clamp(d.Position.y, floorY, hi[j]);
                if (yNew != d.Position.y)
                {
                    d.Position = new Vector2(d.Position.x, yNew);
                }
                prevPlaced = yNew;
            }
            return true;
        }

        /// <summary>
        /// 고립 노드(차수 0)를 같은 시대 밴드 안 컬럼들의 바닥에 그리디 분산 — 잔여 예산
        /// (LayoutMaxColumnHeight − 픽셀 높이), 후보는 |r−홈| 오름차순(동률 오른쪽 우선),
        /// 전부 차면 홈 바닥에 초과 스택. 밴드 제한이 시대 구역을 지킨다.
        /// </summary>
        private void PlaceIsolated(List<List<ResearchNode>> layers, float[] rankX,
            Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            if (isolatedNodesByRank.Count == 0) return;
            float gridY = Constraints.NodeSize.y + Constraints.NodeSpacing.y;

            // 컬럼별 잔여 예산 — 배치된 노드들의 픽셀 범위 기준 (도착 1개당 gridY 근사 차감)
            var remaining = new float[layers.Count];
            for (int r = 0; r < layers.Count; r++)
            {
                float extent = 0f;
                if (layers[r].Count > 0)
                {
                    float top = float.MaxValue, bottom = float.MinValue;
                    foreach (var n in layers[r])
                    {
                        float half = NodeHeightOf(n) / 2f;
                        if (n.Position.y - half < top) top = n.Position.y - half;
                        if (n.Position.y + half > bottom) bottom = n.Position.y + half;
                    }
                    extent = bottom - top;
                }
                remaining[r] = columnBudget - extent;
            }

            // 1) 그리디 배정: 노드마다 첫 여유 후보 컬럼 (없으면 홈). 좌표는 2)에서 일괄 결정.
            var arrivals = new Dictionary<int, List<ResearchNode>>();
            foreach (int home in isolatedNodesByRank.Keys.OrderBy(r => r))
            {
                foreach (var node in isolatedNodesByRank[home])
                {
                    // 프록시는 effectiveTechLevel 미스 → node.TechLevel 폴백 (자식 없는 프록시도 고립 추출 가능)
                    var era = effectiveTechLevel.TryGetValue(node, out var etl) ? etl : node.TechLevel;
                    int target = -1;
                    if (bands.TryGetValue(era, out var band))
                    {
                        // 밴드 안에서 잔여 예산이 가장 큰(가장 빈) 컬럼에 배치 → 높이 균형.
                        // base 랭크는 보통 pile이라 잔여가 작거나 음수 → 고립 노드가 자연히 회피하고 빈 컬럼으로 분산.
                        // 매 배치 후 remaining을 깎으므로 다음 노드는 새 최빈 컬럼을 골라 라운드로빈 분산. 동률은 home에 가까운 쪽.
                        float bestRem = float.NegativeInfinity;
                        for (int r = band.lo; r <= band.hi; r++)
                        {
                            if (!NoHigherEraOccupant(layers[r], era)) continue;
                            float rem = remaining[r];
                            if (rem > bestRem
                                || (rem == bestRem && (target < 0 || System.Math.Abs(r - home) < System.Math.Abs(target - home))))
                            {
                                bestRem = rem;
                                target = r;
                            }
                        }
                    }
                    if (target < 0) target = home; // 밴드 없음/전부 막힘 — 홈 바닥에 스택

                    remaining[target] -= gridY;
                    if (!arrivals.TryGetValue(target, out var list))
                    {
                        list = new List<ResearchNode>();
                        arrivals[target] = list;
                    }
                    list.Add(node);
                }
            }

            // 2) 컬럼별 좌표 결정 + 레이어 편입
            foreach (int rank in arrivals.Keys.OrderBy(r => r))
            {
                var nodes = arrivals[rank];
                var layer = layers[rank];

                float nextY;
                if (layer.Count > 0)
                {
                    // 컬럼 전체에서 가장 아래 노드 중심 밑으로 스택 (VOrder 마지막이 최하단이 아닐 수 있음 — 더미 스냅 오차)
                    float bottom = float.MinValue;
                    foreach (var nn in layer)
                        if (nn.Position.y > bottom) bottom = nn.Position.y;
                    nextY = Mathf.Ceil(bottom / gridY) * gridY + gridY;
                }
                else
                {
                    nextY = Mathf.Floor(-(nodes.Count - 1) / 2f + 0.5f) * gridY;
                }

                foreach (var n in nodes)
                {
                    n.Rank = rank; // 분산 배치한 목적 랭크로 갱신 (홈과 다를 수 있음 — Rank/레이어 일관성, 메트릭·렌더)
                    n.Position = new Vector2(rankX[rank], nextY);
                    layer.Add(n);
                    nextY += gridY;
                }
            }
            SyncVOrder(layers);
        }

        /// <summary>고립 분산 후보 컬럼 가드: era보다 높은 시대의 점유자(주로 데이터 오류 프록시)가 있으면 false.</summary>
        private bool NoHigherEraOccupant(List<ResearchNode> layer, TechLevel era)
        {
            foreach (var n in layer)
            {
                if (n.IsDummy) continue;
                var tl = n.IsProxy ? n.TechLevel
                    : effectiveTechLevel.TryGetValue(n, out var etl) ? etl : n.TechLevel;
                if (tl > era) return false;
            }
            return true;
        }

    }
}
