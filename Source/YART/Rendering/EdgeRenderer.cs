using System.Collections.Generic;
using UnityEngine;
using Verse;
using YART.Utils;

namespace YART.Rendering
{
    /// <summary>
    /// 연구 노드 사이의 엣지들을 그래디언트 베지어 곡선으로 렌더링한다
    /// </summary>
    public static class EdgeRenderer
    {
        private static readonly Dictionary<(Vector2, Vector2, int, float, float), Vector2[]> bezierCache = new Dictionary<(Vector2, Vector2, int, float, float), Vector2[]>();
        private const int MaxCacheSize = 500;
        private static int cacheAccessCount = 0;

        /// <summary>
        /// 두 점을 잇는 베지어 곡선을 그린다
        /// </summary>
        /// <param name="start">시작점 (화면 좌표)</param>
        /// <param name="end">도착점 (화면 좌표)</param>
        /// <param name="startColor">시작 색깔</param>
        /// <param name="endColor">도착 색깔</param>
        /// <param name="width">선 굵기</param>
        /// <param name="zoom">현재 zoom</param>
        /// <param name="alpha">투명도</param>
        /// <param name="crossover">수직 이동이 일어나는 통로 내 위치 (0=출발 직후 ~ 1=도착 직전, 0.5=대칭)</param>
        public static void DrawGradientBezier(Vector2 start, Vector2 end, Color startColor, Color endColor, float width, float zoom, float alpha = 1f, float crossover = 0.5f)
        {
            float dist = Vector2.Distance(start, end);
            if (dist < 1f) return;

            startColor = GenColor.WithAlpha(startColor, startColor.a * alpha);
            endColor = GenColor.WithAlpha(endColor, endColor.a * alpha);

            // LOD
            if (zoom < 0.3f || dist < 30f)
            {
                DrawStraightRibbon(start, end, startColor, endColor, width);
                return;
            }

            // Calculate segment count based on distance and zoom (adaptive LOD)
            int segments = CalculateSegmentCount(dist, zoom);

            var (p1, p2) = ComputeControlPoints(start, end, zoom, crossover);

            // Get or calculate bezier points
            Vector2[] points = GetBezierPoints(start, p1, p2, end, segments);

            EmitRibbon(points, points.Length, startColor, endColor, width);
        }

        /// <summary>
        /// 베지어 제어점 계산. crossover가 0.5면 좌우 대칭(0.45·xDist)이고,
        /// 0에 가까울수록 출발 직후, 1에 가까울수록 도착 직전에 수직 이동을 한다.
        /// 고팬아웃 노드의 엣지들이 통로 내 서로 다른 X에서 교차하도록 분산하는 데 사용 (staggered crossover).
        ///
        /// X 단조성 보장:
        ///   dx = end.x - start.x 방향으로 p1, p2를 배치하므로
        ///   p1.x = start.x + startOffset * sign(dx),
        ///   p2.x = end.x   - endOffset  * sign(dx).
        ///   startOffset + endOffset ≤ 0.9·xDist (cap) 이 성립하면
        ///   start.x ≤ p1.x ≤ p2.x ≤ end.x (역방향이면 부등호 반전).
        ///   핸들 합이 cap를 초과하면 비례 축소해 cap 이하로 만든다 — 축소는 현재
        ///   두 핸들의 비율을 보존한다 (짧은 엣지에서 minOffset이 한쪽을 들어올린
        ///   뒤라면 원래 crossover 비율이 아니라 그 floor 적용 후 비율이 유지됨 —
        ///   staggered 효과가 짧은 엣지에서 약해지지만 X 단조성이 우선).
        ///   축소 후 minOffset 미달은 허용 (X 단조성이 최소 핸들보다 우선).
        /// </summary>
        private static (Vector2 p1, Vector2 p2) ComputeControlPoints(Vector2 start, Vector2 end, float zoom, float crossover)
        {
            float dx = end.x - start.x;
            float xDist = Mathf.Abs(dx);

            // xDist≈0 방어: 수직/제자리 엣지는 제어점을 끝점에 붙인다 (NaN/0분할 없음)
            if (xDist < 1e-3f)
            {
                return (start, end);
            }

            float minOffset = 30f * zoom;
            // 0.9·xDist: 핸들 합의 상한 (양쪽 합이 xDist를 넘으면 제어 다각형이 역전)
            float cap = 0.9f * xDist;

            float startOffset = Mathf.Max(xDist * 0.9f * crossover, minOffset);
            float endOffset   = Mathf.Max(xDist * 0.9f * (1f - crossover), minOffset);

            // 핸들 합이 cap를 초과하면 비례 축소 → X 단조성 보장
            float sum = startOffset + endOffset;
            if (sum > cap)
            {
                float scale = cap / sum;
                startOffset *= scale;
                endOffset   *= scale;
            }

            // 부호 있는 방향으로 핸들 적용 (역방향 엣지도 정확히 처리)
            float sign = dx > 0f ? 1f : -1f;
            return (start + new Vector2(startOffset * sign, 0f), end - new Vector2(endOffset * sign, 0f));
        }

        /// <summary>
        /// 두 점을 잇는 단일 세그먼트 리본
        /// </summary>
        public static void DrawStraightRibbon(Vector2 start, Vector2 end, Color startColor, Color endColor, float width)
        {
            straightBuffer[0] = start;
            straightBuffer[1] = end;
            EmitRibbon(straightBuffer, 2, startColor, endColor, width);
        }

        private static readonly Vector2[] straightBuffer = new Vector2[2];

        private static void EmitRibbon(Vector2[] pts, int count, Color startColor, Color endColor, float width)
        {
            if (count < 2) return;
            float half = width * 0.5f;

            var uvBottom = new Vector2(0.5f, 0f);
            var uvTop = new Vector2(0.5f, 1f);

            Vector2 prevDir = (pts[1] - pts[0]).normalized;
            Vector2 prevOff = new Vector2(-prevDir.y, prevDir.x) * half;
            Vector2 prevPt = pts[0];
            Color prevColor = startColor;

            for (int i = 1; i < count; i++)
            {
                Vector2 dir = (pts[i] - pts[i - 1]).normalized;
                Vector2 normal = new Vector2(-dir.y, dir.x);

                Vector2 off;
                if (i < count - 1)
                {
                    // 접합점: 이웃 접선 평균의 법선으로 마이터. 급각에서 길이 폭주 방지를 위해 2배 클램프
                    Vector2 m = (dir + (pts[i + 1] - pts[i]).normalized).normalized;
                    Vector2 mN = new Vector2(-m.y, m.x);
                    float dot = Vector2.Dot(mN, normal);
                    off = mN * (half * (dot > 0.5f ? 1f / dot : 2f));
                }
                else
                {
                    off = normal * half;
                }

                float t = i / (float)(count - 1);
                Color color = Color.Lerp(startColor, endColor, t);

                GLTexturedQuadBatcher.QueueFreeQuad(Assets.EdgeLine,
                    prevPt - prevOff, prevPt + prevOff, pts[i] + off, pts[i] - off,
                    uvBottom, uvTop, uvTop, uvBottom,
                    prevColor, prevColor, color, color);

                prevPt = pts[i];
                prevOff = off;
                prevColor = color;
            }
        }

        /// <summary>
        /// 엣지 본선과 동일한 곡선의 폴리라인 점들을 반환. (체인 펄스 경로 구성용)
        /// 직선 LOD 구간(초저줌/짧은 거리)이면 null — 호출자가 끝점만 이으면 본선과 일치한다.
        /// </summary>
        public static Vector2[] GetCurvePoints(Vector2 start, Vector2 end, float zoom, float crossover)
        {
            float dist = Vector2.Distance(start, end);
            if (zoom < 0.3f || dist < 30f) return null;

            int segments = CalculateSegmentCount(dist, zoom);
            var (p1, p2) = ComputeControlPoints(start, end, zoom, crossover);
            return GetBezierPoints(start, p1, p2, end, segments);
        }

        private static Vector2[] tailBuffer = new Vector2[256];
        private static readonly List<float> chainArcLengths = new List<float>(160);

        /// <summary>
        /// 실제 노드 사이의 전체 경로(더미 체인 포함) 폴리라인 위를 흐르는 단일 에너지 펄스.
        /// 월드 길이 기준 등속으로 이동하고, 도착 후 PulseArriveDelay만큼 쉬었다가 다시 출발한다.
        /// </summary>
        /// <param name="pts">스크린 좌표 폴리라인 (체인 전체)</param>
        public static void DrawChainPulse(List<Vector2> pts, Color color, float zoom, float alpha = 1f)
        {
            int n = pts.Count;
            if (n < 2 || alpha <= 0.01f) return;

            // 누적 호 길이
            chainArcLengths.Clear();
            chainArcLengths.Add(0f);
            float total = 0f;
            for (int i = 1; i < n; i++)
            {
                total += Vector2.Distance(pts[i - 1], pts[i]);
                chainArcLengths.Add(total);
            }
            if (total < 8f) return;

            // 등속 주행 + 도착 딜레이. 월드 길이는 줌과 무관해 줌 변경 시 위상이 튀지 않는다.
            float worldLen = total / Mathf.Max(zoom, 0.01f);
            float travelTime = worldLen / YART.Data.Constraints.PulseSpeed;
            float cycleTime = travelTime + YART.Data.Constraints.PulseArriveDelay;
            float tCycle = Time.realtimeSinceStartup % cycleTime;
            if (tCycle >= travelTime) return; // 도착 후 대기 구간

            float headArc = (tCycle / travelTime) * total;
            float tailArc = Mathf.Max(0f, headArc - YART.Data.Constraints.PulseTailLength * total);

            // 꼬리 리본 (꼬리→머리로 갈수록 진하게)
            // 고정 개수 샘플링이 아니라 폴리라인의 실제 정점들을 그대로 따라간다 —
            // 균일 샘플링은 곡선 구간에서 정점 사이를 현(직선)으로 잇는 아티팩트를 만든다.
            Color dim = new Color(color.r, color.g, color.b, 0f);
            Color headColor = color;
            headColor.a *= alpha;

            if (tailBuffer.Length < n + 2) tailBuffer = new Vector2[Mathf.NextPowerOfTwo(n + 2)];
            int m = 0;
            tailBuffer[m++] = PointAtArc(pts, chainArcLengths, tailArc);
            for (int i = 0; i < n; i++)
            {
                float a = chainArcLengths[i];
                if (a > tailArc && a < headArc) tailBuffer[m++] = pts[i];
            }
            tailBuffer[m++] = PointAtArc(pts, chainArcLengths, headArc);

            float tailWidth = Mathf.Max(YART.Data.Constraints.EdgeLineMinWidth,
                YART.Data.Constraints.EdgeLineWidth * zoom) * 1.5f;
            EmitRibbon(tailBuffer, m, dim, headColor, tailWidth);

            // 헤드: 글로우 스프라이트
            Vector2 head = tailBuffer[m - 1];
            float glowSize = Mathf.Max(10f, 16f * zoom);
            var glowRect = new Rect(head.x - glowSize / 2f, head.y - glowSize / 2f, glowSize, glowSize);
            Color glowColor = color;
            glowColor.a = 0.9f * alpha;
            GLTexturedQuadBatcher.QueueQuad(Assets.GlowRadial, glowRect, glowColor);
        }

        /// <summary>
        /// 폴리라인 위 호 길이 arc 지점의 좌표를 반환. (cum = 누적 호 길이, pts와 같은 개수)
        /// </summary>
        private static Vector2 PointAtArc(List<Vector2> pts, List<float> cum, float arc)
        {
            int idx = cum.BinarySearch(arc);
            if (idx < 0) idx = ~idx;
            if (idx <= 0) return pts[0];
            if (idx >= pts.Count) return pts[pts.Count - 1];

            float segLen = cum[idx] - cum[idx - 1];
            float f = segLen > 0.0001f ? (arc - cum[idx - 1]) / segLen : 0f;
            return Vector2.Lerp(pts[idx - 1], pts[idx], f);
        }

        public static int CalculateSegmentCount(float distance, float zoom)
        {
            float density;
            if (zoom < 0.5f)
                density = 25f;
            else if (zoom < 0.8f)
                density = 18f;
            else
                density = 12f;

            int segments = Mathf.CeilToInt(distance / density);
            return Mathf.Clamp(segments, 8, 40);
        }

        public static Vector2[] GetBezierPoints(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int segments)
        {
            Vector2 keyStart = new Vector2(Mathf.Round(p0.x), Mathf.Round(p0.y));
            Vector2 keyEnd = new Vector2(Mathf.Round(p3.x), Mathf.Round(p3.y));
            var cacheKey = (keyStart, keyEnd, segments, Mathf.Round(p1.x), Mathf.Round(p2.x));

            if (bezierCache.TryGetValue(cacheKey, out Vector2[] cached))
            {
                return cached;
            }

            // Calculate new points
            Vector2[] points = new Vector2[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                points[i] = CalculateCubicBezierPoint(t, p0, p1, p2, p3);
            }

            // Add to cache with cleanup if needed
            cacheAccessCount++;
            if (cacheAccessCount > 100)
            {
                CleanupCacheIfNeeded();
                cacheAccessCount = 0;
            }

            if (bezierCache.Count < MaxCacheSize)
            {
                bezierCache[cacheKey] = points;
            }

            return points;
        }

        /// <summary>
        /// Calculates a point on a cubic bezier curve.
        /// </summary>
        private static Vector2 CalculateCubicBezierPoint(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            float u = 1 - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            Vector2 p = uuu * p0;           // (1-t)^3 * P0
            p += 3 * uu * t * p1;           // 3(1-t)^2 * t * P1
            p += 3 * u * tt * p2;           // 3(1-t) * t^2 * P2
            p += ttt * p3;                   // t^3 * P3

            return p;
        }

        private static void CleanupCacheIfNeeded()
        {
            if (bezierCache.Count > MaxCacheSize / 2)
            {
                // Simple cleanup: clear all (could be improved with LRU)
                bezierCache.Clear();
            }
        }

        public static void ClearCache()
        {
            bezierCache.Clear();
            cacheAccessCount = 0;
        }
    }
}
