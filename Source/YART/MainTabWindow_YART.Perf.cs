using System;
using System.Diagnostics;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART
{
    /// <summary>
    /// 성능 측정 용 하네스
    /// </summary>
    public partial class MainTabWindow_YART
    {
        internal static bool perfOverlay;
        internal static int perfStress = 1;

        private readonly Stopwatch perfSw = new Stopwatch();
        private double perfFrameMsAvg;
        private double perfFrameAccum;
        private int perfDrawCalls;
        private float perfRateTime;
        private int perfLastGc0;
        private double perfDrawsPerSec;
        private double perfGcPerSec;
        private int perfNodes, perfEdges, perfVisNodes, perfVisEdges;

        // 렌더 구간별 계측: 0=엣지, 1=노드배경(GL), 2=펄스, 3=노드텍스트
        private readonly double[] perfSecAccum = new double[4];
        private readonly double[] perfSecMs = new double[4];
        private readonly Stopwatch perfSecSw = new Stopwatch();
        private int perfSecCur = -1;

        private void PerfSec(int i)
        {
            if (!perfOverlay || Event.current.type != EventType.Repaint) return;
            perfSecCur = i;
            perfSecSw.Restart();
        }

        private void PerfSecEnd()
        {
            if (perfSecCur < 0) return;
            perfSecSw.Stop();
            perfSecAccum[perfSecCur] += perfSecSw.Elapsed.TotalMilliseconds;
            perfSecCur = -1;
        }

        private void PerfBeginDraw()
        {
            if (!perfOverlay) return;
            perfDrawCalls++;
            perfSw.Restart();
        }

        private void PerfEndDraw()
        {
            if (!perfOverlay) return;
            perfSw.Stop();
            double ms = perfSw.Elapsed.TotalMilliseconds;
            // Layout = 디스플레이 프레임의 첫 패스 → 직전 프레임 합계 확정 후 리셋
            if (Event.current.type == EventType.Layout)
            {
                perfFrameMsAvg += (perfFrameAccum - perfFrameMsAvg) * 0.1;
                perfFrameAccum = 0.0;
                for (int i = 0; i < 4; i++)
                {
                    perfSecMs[i] += (perfSecAccum[i] - perfSecMs[i]) * 0.1;
                    perfSecAccum[i] = 0.0;
                }
            }
            perfFrameAccum += ms;

            // 0.5초마다 비율/카운트 갱신
            float now = Time.realtimeSinceStartup;
            float dt = now - perfRateTime;
            if (dt >= 0.5f)
            {
                int gc0 = GC.CollectionCount(0);
                perfDrawsPerSec = perfDrawCalls / dt;
                perfGcPerSec = (gc0 - perfLastGc0) / dt;
                perfDrawCalls = 0;
                perfLastGc0 = gc0;
                perfRateTime = now;
                RecountGraph();
            }
        }

        private void RecountGraph()
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null) { perfNodes = perfEdges = perfVisNodes = perfVisEdges = 0; return; }
            perfNodes = graph.Nodes.Count;
            perfEdges = graph.Edges.Count;
            int vn = 0;
            foreach (var n in graph.Nodes) if (visibleRect.Overlaps(n.Rect)) vn++;
            perfVisNodes = vn;
            int ve = 0;
            foreach (var e in graph.Edges)
                if (visibleRect.Overlaps(e.From.Rect) || visibleRect.Overlaps(e.To.Rect)) ve++;
            perfVisEdges = ve;
        }

        private void DrawPerfOverlay(Rect inRect)
        {
            if (!perfOverlay) return;
            string txt =
                $"YART PERF   stress ×{perfStress}\n" +
                $"frame {perfFrameMsAvg:F2} ms (all passes)   draws {perfDrawsPerSec:F0}/s   gc0 {perfGcPerSec:F1}/s\n" +
                $"edge-Q {perfSecMs[0]:F1}  edge-Flush {perfSecMs[1]:F1}  bg-Q {perfSecMs[2]:F1}  bg-Flush {perfSecMs[3]:F1} ms\n" +
                $"nodes {perfNodes} (vis {perfVisNodes})   edges {perfEdges} (vis {perfVisEdges})";
            Rect r = new Rect(inRect.xMax - 372f, inRect.y + 60f, 368f, 72f);
            Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.72f));
            GUIDrawingUtilities.DrawBorderLines(r, new Color(0.4f, 0.8f, 0.4f, 0.8f), 1f);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.UpperLeft))
            using (Temporary.Color(new Color(0.65f, 1f, 0.65f)))
            {
                Widgets.Label(r.ContractedBy(6f), txt);
            }
        }

        [DebugAction("YART", "Perf: toggle render overlay", allowedGameStates = AllowedGameStates.Playing)]
        private static void TogglePerfOverlay() => perfOverlay = !perfOverlay;

        [DebugAction("YART", "Perf: cycle stress multiplier", allowedGameStates = AllowedGameStates.Playing)]
        private static void CyclePerfStress()
        {
            switch (perfStress)
            {
                case 1: perfStress = 2; break;
                case 2: perfStress = 3; break;
                case 3: perfStress = 5; break;
                case 5: perfStress = 8; break;
                default: perfStress = 1; break;
            }
            Messages.Message($"[YART] render stress ×{perfStress}", MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
