using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LudeonTK;
using UnityEngine;
using Verse;
using YART.Data;

namespace YART
{
    [StaticConstructorOnStartup]
    public static class GraphBuildPipeline
    {
        private static volatile bool layoutCalculated = false;
        private static Task graphBuildTask;
        private static volatile bool graphReady;

        public static bool GraphReady => graphReady;

        public static ResearchSearchEngine SearchEngine { get; } = new ResearchSearchEngine();

        static GraphBuildPipeline()
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                graphBuildTask = Task.Run(() =>
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        BuildGraphAndLayout();
                        stopwatch.Stop();
                        if (Prefs.DevMode)
                            Log.Message("[YART] Research graph built in background in " + stopwatch.ElapsedMilliseconds + " ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[YART] Background graph build failed: " + ex);
                    }
                });
            });
        }

        /// <summary>
        /// 백그라운드 빌드가 진행 중이면 그대로 두고, 실패했거나 아직 시작 전인 경우에만 동기로 빌드한다.
        /// </summary>
        public static void EnsureBuiltNonBlocking()
        {
            var task = graphBuildTask;
            if (!graphReady && (task == null || task.IsCompleted))
            {
                BuildGraphAndLayout();
            }
        }

        /// <summary>
        /// 백그라운드 빌드가 진행 중이면 완료를 블로킹 대기한다.
        /// </summary>
        public static void EnsureBuilt()
        {
            var task = graphBuildTask;
            if (task != null && !task.IsCompleted)
            {
                var stopwatch = Stopwatch.StartNew();
                task.Wait();
                stopwatch.Stop();
                if (Prefs.DevMode && stopwatch.ElapsedMilliseconds > 1)
                {
                    Log.Message("[YART] Waited " + stopwatch.ElapsedMilliseconds + " ms for background graph build");
                }
            }

            // 백그라운드 실패/미시작 폴백 (예외는 태스크 안에서 처리되므로 여기선 상태로 판단)
            if (!graphReady)
            {
                BuildGraphAndLayout();
            }
        }

        private static void BuildGraphAndLayout()
        {
            if (!ResearchGraph.Instance.Initialized)
            {
                ResearchGraph.Instance.Build();
                layoutCalculated = false;
            }

            if (!layoutCalculated)
            {
                RunLayoutAndSearch();
            }
            else if (!SearchEngine.IsBuilt)
            {
                SearchEngine.Build(ResearchGraph.Instance.AllNodes.Values);
            }

            graphReady = ResearchGraph.Instance.Initialized && layoutCalculated && SearchEngine.IsBuilt;
        }

        private static void RunLayoutAndSearch()
        {
            var subGraphs = ResearchGraph.Instance.SubGraphs.Values.ToList();
            LayoutExport.Begin();
            Parallel.ForEach(subGraphs, subGraph => new SugiyamaLayout().Calculate(subGraph));

            int benchTabCount = ResearchGraph.Instance.SubGraphs.Count(kvp =>
                !kvp.Key.IsUnified && kvp.Key.Channel != null && kvp.Key.Channel.IsBench
                && kvp.Value.Nodes.Any(n => !n.IsDummy && !n.IsProxy));
            if (benchTabCount >= 2)
            {
                ResearchGraph.Instance.GetOrBuildUnifiedBench();
            }

            ResearchNode.InvalidateAllPorts();
            layoutCalculated = true;

            SearchEngine.Build(ResearchGraph.Instance.AllNodes.Values);
        }

        public static int Generation { get; private set; }

        public static void RebuildNow()
        {
            try
            {
                // 레이스 방지
                var task = graphBuildTask;
                if (task != null && !task.IsCompleted)
                {
                    task.Wait();
                }

                var stopwatch = Stopwatch.StartNew();

                // 그래프 전체를 새로 빌드
                graphReady = false;
                layoutCalculated = false;
                SearchEngine.Invalidate();
                ResearchGraph.Instance.Build();

                RunLayoutAndSearch();

                // 베지어 캐시 무효화
                Rendering.EdgeRenderer.ClearCache();

                graphReady = ResearchGraph.Instance.Initialized && layoutCalculated && SearchEngine.IsBuilt;
                Generation++;

                stopwatch.Stop();
                if (Prefs.DevMode)
                    Log.Message("[YART] RebuildNow completed in " + stopwatch.ElapsedMilliseconds + " ms (generation " + Generation + ")");
            }
            catch (Exception ex)
            {
                // 실패 시 플래그를 일관된 상태로 — graphReady false 유지로 빌딩 안내 표시
                graphReady = false;
                layoutCalculated = false;
                Log.Error("[YART] RebuildNow failed: " + ex);
            }
        }

        /// <summary>
        /// 플레이데이터 라이브 재로드(언어 변경 = ClearAllPlayData + LoadAllPlayData) 후 호출.
        /// </summary>
        public static void OnPlayDataReloaded()
        {
            graphReady = false;
            layoutCalculated = false;
            SearchEngine.Invalidate(); // 언어 재감지

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                graphBuildTask = Task.Run(() =>
                {
                    try
                    {
                        ResearchGraph.Instance.Build();
                        RunLayoutAndSearch();
                        Rendering.EdgeRenderer.ClearCache();
                        Generation++;
                        graphReady = ResearchGraph.Instance.Initialized && layoutCalculated && SearchEngine.IsBuilt;
                        if (Prefs.DevMode)
                            Log.Message("[YART] Rebuilt after play-data/language reload (generation " + Generation + ")");
                    }
                    catch (Exception ex)
                    {
                        graphReady = false;
                        Log.Error("[YART] Reload rebuild failed: " + ex);
                    }
                });
            });
        }

        public static void RebuildNonBlocking()
        {
            graphReady = false;
            var prev = graphBuildTask;
            graphBuildTask = Task.Run(() =>
            {
                try
                {
                    if (prev != null && !prev.IsCompleted) prev.Wait(); // 이전 빌드 완료 대기 (백그라운드)

                    var stopwatch = Stopwatch.StartNew(); // 이전 빌드 대기는 제외, 순수 리빌드 비용만 측정
                    layoutCalculated = false;
                    SearchEngine.Invalidate();
                    ResearchGraph.Instance.Build();
                    RunLayoutAndSearch();
                    Rendering.EdgeRenderer.ClearCache();
                    Generation++;
                    graphReady = ResearchGraph.Instance.Initialized && layoutCalculated && SearchEngine.IsBuilt;
                    stopwatch.Stop();
                    if (Prefs.DevMode)
                        Log.Message("[YART] Non-blocking rebuild done in " + stopwatch.ElapsedMilliseconds + " ms (generation " + Generation + ")");
                }
                catch (Exception ex)
                {
                    graphReady = false;
                    Log.Error("[YART] Non-blocking rebuild failed: " + ex);
                }
            });
        }

        /// <summary>
        /// 서브그래프별 컬럼(랭크) 높이 분포 덤프
        /// </summary>
        [DebugAction("YART", "Column height dump", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpColumnHeights()
        {
            EnsureBuilt();

            float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            float rowDummy = Constraints.LayoutDummyNodeHeight + Constraints.LayoutDummySpacing;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[YART] Column height dump (budget {Constraints.LayoutMaxColumnHeight:F0} px; rank: real + dummy = est px):");

            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                var subGraph = kvp.Value;
                if (subGraph.Nodes.Count == 0) continue;

                var byRank = new SortedDictionary<int, (int real, int dummy)>();
                foreach (var node in subGraph.Nodes)
                {
                    byRank.TryGetValue(node.Rank, out var c);
                    if (node.IsDummy) c.dummy++;
                    else c.real++;
                    byRank[node.Rank] = c;
                }

                float maxPx = 0f;
                int maxRank = 0;
                sb.AppendLine($"=== {kvp.Key} ({subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges) ===");
                foreach (var entry in byRank)
                {
                    float px = entry.Value.real * rowReal + entry.Value.dummy * rowDummy;
                    if (px > maxPx) { maxPx = px; maxRank = entry.Key; }
                    sb.AppendLine($"  rank {entry.Key,3}: {entry.Value.real,3} real + {entry.Value.dummy,3} dummy = {px,5:F0} px");
                }
                sb.AppendLine($"  max: rank {maxRank} = {maxPx:F0} px");
            }

            Log.Message(sb.ToString());
        }

        /// <summary>
        /// 서브그래프별 레이아웃 품질 지표 덤프
        /// </summary>
        [DebugAction("YART", "Layout metrics dump", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpLayoutMetrics()
        {
            EnsureBuilt();

            var sb = new System.Text.StringBuilder();
            float dumpBudget = Constraints.LayoutMaxColumnHeight;
            float dumpRowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            sb.AppendLine($"[YART] Layout metrics dump (budget {dumpBudget:F0} px = {dumpBudget / dumpRowReal:F1} rows; λ={Constraints.RankCostSpanWeight} μ={Constraints.RankCostWidthWeight}):");

            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                var subGraph = kvp.Value;
                var m = subGraph.Metrics;

                sb.AppendLine($"=== {kvp.Key} ({subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges) ===");

                if (m == null)
                {
                    sb.AppendLine("  (no layout)");
                    continue;
                }

                var bb = subGraph.BoundingBox;
                float refW = Constraints.MetricsRefViewportW;
                float refH = Constraints.MetricsRefViewportH;
                float fitZoom = (bb.width > 0f && bb.height > 0f)
                    ? Mathf.Min(refW / bb.width, refH / bb.height)
                    : 0f;

                sb.AppendLine($"  crossings:          {m.Crossings}");
                sb.AppendLine($"  width x height:     {bb.width:F0} x {bb.height:F0} px");
                sb.AppendLine($"  fitZoom:            {fitZoom:F3}");
                sb.AppendLine($"  maxColumnHeight:    {m.MaxColumnHeight:F0} px");
                sb.AppendLine($"  totalEdgeSpan:      {m.TotalEdgeSpan}");
                sb.AppendLine($"  bendCount:          {m.BendCount}");
                sb.AppendLine($"  unjustifiedBends:   {m.UnjustifiedBends}");
                sb.AppendLine($"  verticalVariation:  {m.VerticalVariation:F0} px");
                sb.AppendLine($"  nonConvergedChains: {m.NonConvergedChains}");
                sb.AppendLine($"  overflowCost:       {m.OverflowCost:F2}  (단위: 행초과²)");
                sb.AppendLine($"  usedRankCount:      {m.UsedRankCount}");
                sb.AppendLine($"  rankCost:           {m.RankCost:F2}  (= overflow + λ·span + μ·width)");
            }

            Log.Message(sb.ToString());
        }

        [DebugAction("YART", "Dump graph structure", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpGraphStructure()
        {
            EnsureBuilt();

            Log.Message("[YART] ========== GRAPH STRUCTURE DUMP ==========");

            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                var graphKey = kvp.Key;
                var subGraph = kvp.Value;

                Log.Message($"[YART] === SubGraph: {graphKey} ({subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges) ===");

                // 레이어별로 노드 정리
                var layers = new Dictionary<int, List<ResearchNode>>();
                foreach (var node in subGraph.Nodes)
                {
                    if (!layers.ContainsKey(node.Rank))
                        layers[node.Rank] = new List<ResearchNode>();
                    layers[node.Rank].Add(node);
                }

                foreach (var rank in layers.Keys.OrderBy(r => r))
                {
                    var layer = layers[rank].OrderBy(n => n.VOrder).ToList();
                    Log.Message($"[YART] --- Rank {rank} ({layer.Count} nodes) ---");

                    foreach (var node in layer)
                    {
                        string prereqs = string.Join(", ", node.Prerequisites.Select(p => p.IsDummy ? $"D({p.Rank},{p.VOrder})" : p.Id));
                        string children = string.Join(", ", node.Children.Select(c => c.IsDummy ? $"D({c.Rank},{c.VOrder})" : c.Id));

                        if (node.IsDummy)
                        {
                            Log.Message($"[YART]   DUMMY: Rank={node.Rank}, VOrder={node.VOrder}, Pos=({node.Position.x:F1},{node.Position.y:F1}) | In=[{prereqs}] Out=[{children}]");
                        }
                        else
                        {
                            Log.Message($"[YART]   {node.Id}: Rank={node.Rank}, VOrder={node.VOrder}, Pos=({node.Position.x:F1},{node.Position.y:F1}) | In=[{prereqs}] Out=[{children}]");
                        }
                    }
                }
            }

            Log.Message("[YART] ========== END DUMP ==========");
        }
    }
}
