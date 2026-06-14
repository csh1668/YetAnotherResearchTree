using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RimWorld;
using Verse;
using YART;
using YART.Data;

namespace LayoutHarness
{
    /// <summary>
    /// 레이아웃 하네스
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var opt = ParseArgs(args);
            if (opt == null) { PrintUsage(); return 1; }

            if (!File.Exists(opt.InPath))
            {
                Console.Error.WriteLine($"입력 파일 없음: {opt.InPath}");
            }

            var dump = LoadDump(opt.InPath);
            if (opt.List)
            {
                Console.WriteLine($"{dump.Count} graphs in {opt.InPath}:");
                foreach (var g in dump)
                    Console.WriteLine($"  {g.Key,-32} nodes={g.Nodes.Count,-5} edges={g.Edges.Count}");
                return 0;
            }

            var picked = dump.FirstOrDefault(g => g.Key == opt.Graph)
                       ?? dump.FirstOrDefault(g => g.Key.IndexOf(opt.Graph, StringComparison.OrdinalIgnoreCase) >= 0);
            if (picked == null)
            {
                Console.Error.WriteLine($"그래프 '{opt.Graph}' 없음. --list 로 키 확인.");
                return 1;
            }

            Prefs.DevMode = opt.DevMode;

            var graph = BuildGraph(picked);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            new YART.SugiyamaLayout().Calculate(graph);
            sw.Stop();

            Console.WriteLine($"=== {picked.Key}  ({sw.ElapsedMilliseconds} ms) ===");
            PrintMetrics(graph);
            if (opt.Dump) DumpRanks(graph);

            string outPath = opt.OutPath ?? Sanitize(picked.Key) + ".svg";
            File.WriteAllText(outPath, SvgRenderer.Render(graph));
            Console.WriteLine($"SVG -> {Path.GetFullPath(outPath)}");
            return 0;
        }

        private static ResearchSubGraph BuildGraph(DumpGraph d)
        {
            var key = new GraphKey(d.Key, d.Key.IndexOf("Unified", StringComparison.OrdinalIgnoreCase) >= 0);
            var g = new ResearchSubGraph(key);
            var byId = new Dictionary<string, ResearchNode>(d.Nodes.Count);
            foreach (var n in d.Nodes)
            {
                var node = new ResearchNode
                {
                    Id = n.Id,
                    Label = n.Label ?? n.Id,
                    TechLevel = (TechLevel)n.Tech,
                    IsProxy = n.Proxy,
                    Key = key,
                };
                g.AddNode(node);
                byId[n.Id] = node;
            }
            foreach (var e in d.Edges)
            {
                if (!byId.TryGetValue(e.From, out var from) || !byId.TryGetValue(e.To, out var to)) continue;
                from.Children.Add(to);
                to.Prerequisites.Add(from);
                g.AddEdge(from, to);
            }
            return g;
        }

        private static void PrintMetrics(ResearchSubGraph g)
        {
            int real = g.Nodes.Count(n => !n.IsDummy && !n.IsProxy);
            int proxy = g.Nodes.Count(n => n.IsProxy);
            int dummy = g.Nodes.Count(n => n.IsDummy);
            int ranks = g.Nodes.Count == 0 ? 0 : g.Nodes.Max(n => n.Rank) + 1;
            int overlaps = CountSeparationViolations(g);
            int visual = CountVisualOverlaps(g);
            float realMaxCol = MaxColumnPx(g);
            var m = g.Metrics;

            int isolated = g.Nodes.Count(n => !n.IsDummy && !n.IsProxy && n.Prerequisites.Count == 0 && n.Children.Count == 0);
            Console.WriteLine($"  nodes: {real} real ({isolated} isolated) + {proxy} proxy + {dummy} dummy   ranks: {ranks}");
            Console.WriteLine($"  VISUAL OVERLAPS (box 침범): {visual}        <-- 진짜 '안 깔끔' 지표 (Fix A 타깃, 낮을수록 좋음)");
            Console.WriteLine($"  sep-violations (참고: 양성 통로 포함): {overlaps}");
            Console.WriteLine($"  maxColumnHeight: {realMaxCol:F0} px");
            if (m != null)
            {
                Console.WriteLine($"  RankCost={m.RankCost:F1} (overflow={m.OverflowCost:F1}, span={m.TotalEdgeSpan}, usedRanks={m.UsedRankCount})");
                Console.WriteLine($"  crossings={m.Crossings}  bends={m.BendCount} (unjustified={m.UnjustifiedBends})  nonConvergedChains={m.NonConvergedChains}");
            }
        }

        private static void DumpRanks(ResearchSubGraph g)
        {
            float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            int ranks = g.Nodes.Count == 0 ? 0 : g.Nodes.Max(n => n.Rank) + 1;
            var rows = new List<(int rank, int real, int dummy, float ext)>();
            for (int r = 0; r < ranks; r++)
            {
                var layer = g.Nodes.Where(n => n.Rank == r).ToList();
                if (layer.Count == 0) continue;
                float top = float.MaxValue, bot = float.MinValue;
                foreach (var n in layer)
                {
                    float half = (n.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y) / 2f;
                    top = Math.Min(top, n.Position.y - half);
                    bot = Math.Max(bot, n.Position.y + half);
                }
                rows.Add((r, layer.Count(n => !n.IsDummy), layer.Count(n => n.IsDummy), bot - top));
            }
            Console.WriteLine("  --- 가장 높은 랭크 8개 (ext=실제 px, packed=real*78=노드수 하한, spread=ext/packed) ---");
            foreach (var x in rows.OrderByDescending(r => r.ext).Take(8))
            {
                float packed = x.real * rowReal;
                float spread = packed > 0 ? x.ext / packed : 0;
                Console.WriteLine($"  rank {x.rank,2}: {x.real,3} real + {x.dummy,3} dummy   ext={x.ext,6:F0}px  packed={packed,6:F0}  spread={spread:F2}x");
            }
        }

        private static float MaxColumnPx(ResearchSubGraph g)
        {
            var min = new Dictionary<int, float>();
            var max = new Dictionary<int, float>();
            foreach (var n in g.Nodes)
            {
                float half = (n.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y) / 2f;
                float top = n.Position.y - half, bot = n.Position.y + half;
                if (!min.TryGetValue(n.Rank, out var t) || top < t) min[n.Rank] = top;
                if (!max.TryGetValue(n.Rank, out var b) || bot > b) max[n.Rank] = bot;
            }
            float best = 0f;
            foreach (var r in min.Keys)
            {
                float h = max[r] - min[r];
                if (h > best) best = h;
            }
            return best;
        }

        // SugiyamaLayout.CountSeparationViolations와 동일
        private static int CountSeparationViolations(ResearchSubGraph g)
        {
            int count = 0;
            foreach (var grp in g.Nodes.GroupBy(n => n.Rank))
            {
                var layer = grp.OrderBy(n => n.VOrder).ToList();
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    float gap = layer[i + 1].Position.y - layer[i].Position.y;
                    if (gap < SeparationOf(layer[i], layer[i + 1]) - 0.5f) count++;
                }
            }
            return count;
        }

        // SugiyamaLayout.ValidateLayerSeparation와 동일
        private static int CountVisualOverlaps(ResearchSubGraph g)
        {
            int count = 0;
            foreach (var grp in g.Nodes.GroupBy(n => n.Rank))
            {
                var layer = grp.OrderBy(n => n.VOrder).ToList();
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    float ha = layer[i].IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
                    float hb = layer[i + 1].IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
                    float visualMin = (ha + hb) / 2f - 0.5f;
                    float gap = layer[i + 1].Position.y - layer[i].Position.y;
                    if (gap < visualMin)
                    {
                        count++;
                        Console.Error.WriteLine($"  overlap rank {layer[i].Rank}: [{Desc(layer[i])} y={layer[i].Position.y:F0} vo={layer[i].VOrder}] | [{Desc(layer[i + 1])} y={layer[i + 1].Position.y:F0} vo={layer[i + 1].VOrder}]  gap={gap:F0} need={visualMin:F0}");
                    }
                }
            }
            return count;
        }

        private static string Desc(ResearchNode n)
            => n.IsDummy ? "D" : n.IsProxy ? ("P:" + n.Label) : (n.Prerequisites.Count == 0 && n.Children.Count == 0 ? "ISO:" + n.Label : n.Label);

        private static float SeparationOf(ResearchNode a, ResearchNode b)
        {
            float ha = a.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
            float hb = b.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
            float spacing = (a.IsDummy && b.IsDummy) ? Constraints.LayoutDummySpacing : Constraints.NodeSpacing.y;
            return (ha + hb) / 2f + spacing;
        }

        private static List<DumpGraph> LoadDump(string path)
        {
            var list = new List<DumpGraph>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var g in doc.RootElement.GetProperty("graphs").EnumerateArray())
            {
                var dg = new DumpGraph { Key = g.GetProperty("key").GetString() };
                foreach (var n in g.GetProperty("nodes").EnumerateArray())
                {
                    dg.Nodes.Add(new DumpNode
                    {
                        Id = n.GetProperty("id").GetString(),
                        Label = n.GetProperty("label").GetString(),
                        Tech = n.GetProperty("tech").GetInt32(),
                        Proxy = n.GetProperty("proxy").GetBoolean(),
                    });
                }
                foreach (var e in g.GetProperty("edges").EnumerateArray())
                    dg.Edges.Add(new DumpEdge { From = e.GetProperty("from").GetString(), To = e.GetProperty("to").GetString() });
                list.Add(dg);
            }
            return list;
        }

        // ── args ──
        private sealed class Options
        {
            public string InPath = "YART_layout_graph.json";
            public string Graph = "Unified";
            public string OutPath;
            public bool DevMode;
            public bool List;
            public bool Dump;
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--in": o.InPath = args[++i]; break;
                    case "--graph": o.Graph = args[++i]; break;
                    case "--out": o.OutPath = args[++i]; break;
                    case "--devmode": o.DevMode = true; break;
                    case "--list": o.List = true; break;
                    case "--dump": o.Dump = true; break;
                    case "-h": case "--help": return null;
                    default: Console.Error.WriteLine($"알 수 없는 인자: {args[i]}"); return null;
                }
            }
            return o;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("LayoutHarness");
            Console.WriteLine("  --in <path>     입력 그래프 JSON (기본: YART_layout_graph.json)");
            Console.WriteLine("  --graph <key>   대상 그래프 키 또는 부분일치 (기본: Unified)");
            Console.WriteLine("  --out <path>    SVG 출력 경로 (기본: <graph>_<fix>.svg)");
            Console.WriteLine("  --devmode       Prefs.DevMode=true (불변식 경고 출력)");
            Console.WriteLine("  --list          JSON 안의 그래프 목록만 출력");
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('/', '_');
        }

        // DTO
        private sealed class DumpGraph { public string Key; public List<DumpNode> Nodes = new(); public List<DumpEdge> Edges = new(); }
        private sealed class DumpNode { public string Id; public string Label; public int Tech; public bool Proxy; }
        private sealed class DumpEdge { public string From; public string To; }
    }
}
