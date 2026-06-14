using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Rendering;

namespace YART
{
    // 엣지 렌더링: 베지어 본선(포트 분산 + staggered crossover), 활성 경로 체인 펄스
    public partial class MainTabWindow_YART
    {
        private void DrawConnection(ResearchNode from, ResearchNode to, Vector2 offset, float zoom)
        {
            // 포트 분산: 고차수 노드의 엣지가 변 중앙 한 점에서 겹쳐 출발하지 않도록 상대별 포트 사용
            Vector2 startWorld = from.GetOutputAnchor(to);
            Vector2 endWorld = to.GetInputAnchor(from);

            // Culling Check
            float minX = Mathf.Min(startWorld.x, endWorld.x);
            float maxX = Mathf.Max(startWorld.x, endWorld.x);
            float minY = Mathf.Min(startWorld.y, endWorld.y);
            float maxY = Mathf.Max(startWorld.y, endWorld.y);
            float margin = 150f;
            Rect connectionRect = new Rect(minX - margin, minY - margin, (maxX - minX) + margin * 2, (maxY - minY) + margin * 2);

            if (!visibleRect.Overlaps(connectionRect)) return;

            // 화면 좌표 변환
            Vector2 startScreen = (startWorld * zoom) + offset;
            Vector2 endScreen = (endWorld * zoom) + offset;

            // Get gradient colors based on node states
            Color startColor = GetEdgeColor(from);
            Color endColor = GetEdgeColor(to);

            // 포커스/검색 모드: 강조 경로는 상태색 대신 흰색, 나머지는 디밍
            float alpha = 1f;
            int highlight = GetEdgeHighlightState(from, to);
            if (highlight > 0)
            {
                startColor = Constraints.EdgeHighlight;
                endColor = Constraints.EdgeHighlight;
            }
            else if (highlight < 0)
            {
                alpha = Constraints.UnfocusedEdgeOpacity;
            }

            // Line width scales with zoom
            float width = Mathf.Max(Constraints.EdgeLineMinWidth, Constraints.EdgeLineWidth * zoom);

            // Draw gradient bezier curve (펄스는 DrawActivePulses에서 체인 단위로 별도 렌더링)
            EdgeRenderer.DrawGradientBezier(startScreen, endScreen, startColor, endColor, width, zoom, alpha, GetEdgeCrossover(from, to));
        }

        /// <summary>
        /// Staggered crossover: 팬아웃/팬인 내 포트 순번에 따라 수직 이동 X를 통로 전체에 분산.
        /// 양 끝 팬 크기로 가중 평균 — 큰 팬 쪽의 분산 요구가 우선된다.
        /// </summary>
        private static float GetEdgeCrossover(ResearchNode from, ResearchNode to)
        {
            int fanOutWeight = from.Children.Count - 1;
            int fanInWeight = to.Prerequisites.Count - 1;
            if (fanOutWeight + fanInWeight <= 0) return 0.5f;

            float ratio = (from.GetOutputPortRatio(to) * fanOutWeight + to.GetInputPortRatio(from) * fanInWeight)
                          / (fanOutWeight + fanInWeight);
            return Mathf.Lerp(Constraints.EdgeCrossoverMin, Constraints.EdgeCrossoverMax, ratio);
        }

        private static readonly List<Vector2> chainPointsBuffer = new List<Vector2>(160);
        private static readonly List<ResearchNode> activeTargetsBuffer = new List<ResearchNode>(8);

        /// <summary>
        /// 활성 경로(완료된 선행 → 연구 중)마다 단일 에너지 펄스를 그린다.
        /// 더미 체인을 포함한 실제 노드 사이의 전체 경로를 하나의 폴리라인으로 이어,
        /// 세그먼트별 펄스 난립과 구간별 속도 차이를 없앤다. 노드/더미 위에 그려지도록
        /// 노드 배경 플러시 이후에 호출할 것.
        /// 허브 버스: 한 머리 엣지의 트렁크가 여러 타깃을 거느릴 수 있고(멀티 트랙 동시
        /// 연구로 둘 이상이 InProgress일 수 있음), 활성 타깃마다 분기 경로가 다르므로
        /// (소스, 활성 타깃) 쌍 단위로 펄스를 그린다.
        /// </summary>
        private void DrawActivePulses(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            foreach (var edge in graph.Edges)
            {
                if (edge.From.IsDummy) continue; // 체인의 머리 세그먼트에서만 시작
                if (edge.From.State != ResearchNodeState.Completed) continue; // 머리 엣지의 From은 항상 실노드

                // 활성 판정과 타깃 수집을 한 번의 트렁크 워크로 — 타깃이 없으면 비활성 엣지
                CollectActiveTargets(edge.To, activeTargetsBuffer);
                if (activeTargetsBuffer.Count == 0) continue;

                foreach (var target in activeTargetsBuffer)
                {
                    // 포커스/검색 디밍 중에는 관련 (소스, 타깃) 쌍에만 펄스 — 같은 트렁크의
                    // 다른 타깃 경로는 별개로 판정한다 (기존 세그먼트 펄스와 동일 정책)
                    if (hoveredNode != null)
                    {
                        if (!focusedNodes.Contains(edge.From) || !focusedNodes.Contains(target)) continue;
                    }
                    else if (matchedDefs.Count > 0)
                    {
                        if (!matchedDefs.Contains(edge.From.Def) || !matchedDefs.Contains(target.Def)) continue;
                    }

                    // 타깃 전용 경로 폴리라인 구성 (스크린 좌표, 각 구간은 본선과 동일 곡선):
                    // 분기점(더미)에서는 자식 중 타깃이 있으면 그쪽으로, 아니면 다음 트렁크 더미로
                    chainPointsBuffer.Clear();
                    var prevNode = edge.From;
                    Vector2 prevOut = edge.From.GetOutputAnchor(edge.To);
                    chainPointsBuffer.Add((prevOut * zoom) + offset);

                    var cur = edge.To;
                    while (cur != null)
                    {
                        Vector2 inAnchor = cur.GetInputAnchor(prevNode);
                        AppendCurve(chainPointsBuffer, (prevOut * zoom) + offset, (inAnchor * zoom) + offset,
                            zoom, GetEdgeCrossover(prevNode, cur));
                        if (!cur.IsDummy) break;

                        // 더미 내부 직선 통로
                        chainPointsBuffer.Add((cur.OutputAnchor * zoom) + offset);
                        prevNode = cur;
                        prevOut = cur.OutputAnchor;
                        cur = NextHopToward(cur, target);
                    }
                    if (cur == null) continue; // 방어: 타깃은 같은 트렁크에서 수집되므로 항상 도달함

                    Color pulseColor = Color.Lerp(GetEdgeColor(target), Color.white, 0.55f);
                    pulseColor.a = 1f;
                    EdgeRenderer.DrawChainPulse(chainPointsBuffer, pulseColor, zoom);
                }
            }
        }

        /// <summary>
        /// 트렁크(더미 체인)를 따라 도달 가능한 실노드 타깃 중 InProgress인 것들을 수집.
        /// 트렁크는 더미 자식 0~1개의 단일 경로, 실노드 분기는 잎 — 반복문으로 충분하다.
        /// 트렁크 전진은 첫 더미 자식 기준 — 레이아웃의 NextTrunkDown(FirstOrDefault)과
        /// 동일한 선택이라, 불변식이 깨져도 양쪽이 같은 트렁크를 걷는다.
        /// </summary>
        private static void CollectActiveTargets(ResearchNode n, List<ResearchNode> result)
        {
            result.Clear();
            while (n != null)
            {
                if (!n.IsDummy)
                {
                    if (n.State == ResearchNodeState.InProgress) result.Add(n);
                    return;
                }
                ResearchNode nextTrunk = null;
                var children = n.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (c.IsDummy) { if (nextTrunk == null) nextTrunk = c; }
                    else if (c.State == ResearchNodeState.InProgress) result.Add(c);
                }
                n = nextTrunk;
            }
        }

        /// <summary>분기점(더미)에서 타깃 방향 다음 홉: 자식 중 타깃이 있으면 타깃, 아니면 다음 트렁크 더미.</summary>
        private static ResearchNode NextHopToward(ResearchNode dummy, ResearchNode target)
        {
            ResearchNode nextTrunk = null;
            var children = dummy.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (ReferenceEquals(c, target)) return c;
                if (c.IsDummy && nextTrunk == null) nextTrunk = c; // 첫 더미 = NextTrunkDown과 동일 선택
            }
            return nextTrunk;
        }

        /// <summary>
        /// 본선과 동일한 곡선의 점들을 폴리라인 버퍼에 이어 붙인다. (첫 점은 직전 끝점과 같으므로 생략)
        /// </summary>
        private static void AppendCurve(List<Vector2> pts, Vector2 start, Vector2 end, float zoom, float crossover)
        {
            var curve = EdgeRenderer.GetCurvePoints(start, end, zoom, crossover);
            if (curve == null)
            {
                pts.Add(end); // 직선 LOD 구간
                return;
            }
            for (int i = 1; i < curve.Length; i++)
            {
                pts.Add(curve[i]);
            }
        }

        /// <summary>더미 체인을 거슬러 올라가 실제 출발 노드를 찾는다 (더미는 in-degree 1 — 위 방향은 단일 경로).</summary>
        private static ResearchNode ResolveRealSource(ResearchNode n)
        {
            while (n != null && n.IsDummy)
            {
                n = n.Prerequisites.Count > 0 ? n.Prerequisites[0] : null;
            }
            return n;
        }

        /// <summary>
        /// 더미 체인(허브 버스 트렁크)에서 도달 가능한 실제 도착 노드 중 조건을 만족하는
        /// 것이 있는지 검사한다. 트렁크는 더미 자식 0~1개의 단일 경로이고 실노드 분기는
        /// 잎이므로 재귀 없이 순회한다. 엣지마다 매 프레임 불리므로 무할당 — 호출자는
        /// 델리게이트를 캐시할 것 (메서드 그룹 변환도 매 호출 할당이다).
        /// </summary>
        private static bool AnyRealTarget(ResearchNode n, Predicate<ResearchNode> pred)
        {
            while (n != null)
            {
                if (!n.IsDummy) return pred(n);
                ResearchNode nextTrunk = null;
                var children = n.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (c.IsDummy) { if (nextTrunk == null) nextTrunk = c; } // 첫 더미 = NextTrunkDown과 동일 선택
                    else if (pred(c)) return true;
                }
                n = nextTrunk;
            }
            return false;
        }

        // AnyRealTarget용 캐시 델리게이트 (프레임당 할당 방지)
        private Predicate<ResearchNode> matchedDefsPred;

        /// <summary>
        /// 포커스/검색 모드에서 이 엣지(세그먼트)의 강조 상태.
        /// 1 = 강조(흰색), -1 = 디밍, 0 = 모드 비활성.
        /// 검색 매칭은 실제 노드에만 기록되므로, 더미 끝점은 실제 노드까지 따라가
        /// 체인 중간 세그먼트도 양 끝과 동일하게 강조/디밍되도록 한다.
        /// 공유 트렁크는 도달 타깃 중 하나만 매치해도 켜진다 — 그 타깃의 경로이므로 의도된 동작.
        /// </summary>
        private int GetEdgeHighlightState(ResearchNode from, ResearchNode to)
        {
            if (hoveredNode != null)
            {
                // 포커스 수집(focusedNodes)은 경로상 더미도 포함하므로 직접 판정
                return focusedNodes.Contains(from) && focusedNodes.Contains(to) ? 1 : -1;
            }
            if (matchedDefs.Count > 0)
            {
                if (matchedDefsPred == null) matchedDefsPred = n => n != null && matchedDefs.Contains(n.Def);
                var src = ResolveRealSource(from);
                return src != null && matchedDefs.Contains(src.Def) && AnyRealTarget(to, matchedDefsPred) ? 1 : -1;
            }
            return 0;
        }

        /// <summary>
        /// Gets the edge color based on node state and era.
        /// </summary>
        private Color GetEdgeColor(ResearchNode node)
        {
            if (node.IsDummy)
            {
                // For dummy nodes, try to get color from connected nodes
                if (node.Prerequisites.Count > 0)
                    return GetEdgeColor(node.Prerequisites[0]);
                if (node.Children.Count > 0)
                    return GetEdgeColor(node.Children[0]);
                return Constraints.EdgeDefault;
            }

            switch (node.State)
            {
                case ResearchNodeState.Completed:
                    return node.EraAccentColor;
                default:
                    return Constraints.EdgeDefault;
            }
        }
    }
}
