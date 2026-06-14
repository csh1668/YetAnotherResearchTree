using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using Verse;
using YART.Data;

namespace YART
{
    /// <summary>
    /// 그래프 빌드 시 각 서브그래프의 RAW 구조(실노드+실엣지, 더미/랭크 생성 이전)를 메모리에 캡처하고,
    /// 모드 설정의 개발자 버튼을 누르면 바탕화면으로 JSON export한다 — 게임 없이 오프라인 레이아웃 하니스
    /// (Source/LayoutHarness)에서 알고리즘을 반복하기 위함. DevMode에서만 캡처/노출.
    /// 캡처: SugiyamaLayout.Calculate 최상단(레이아웃 전), Begin: GraphBuildPipeline.RunLayoutAndSearch 시작.
    /// </summary>
    public static class LayoutExport
    {
        private static readonly object _lock = new object();
        private static readonly List<string> _graphs = new List<string>(); // 그래프별 JSON 조각

        /// <summary>빌드 시작 시 버퍼 초기화 (재생성 누적 방지).</summary>
        public static void Begin()
        {
            lock (_lock) _graphs.Clear();
        }

        /// <summary>한 서브그래프의 raw 구조 캡처 (Calculate 최상단 = 더미/랭크 생성 이전). DevMode 전용, 스레드 안전.</summary>
        public static void Capture(ResearchSubGraph graph)
        {
            if (!Prefs.DevMode || graph == null) return;

            var sb = new StringBuilder(1024);
            sb.Append("{\"key\":").Append(JsonStr(graph.Key.ToString())).Append(",\"nodes\":[");
            bool first = true;
            foreach (var n in graph.Nodes)
            {
                if (n.IsDummy) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(JsonStr(n.Id))
                  .Append(",\"label\":").Append(JsonStr(n.Label))
                  .Append(",\"tech\":").Append((int)n.TechLevel)
                  .Append(",\"proxy\":").Append(n.IsProxy ? "true" : "false")
                  .Append('}');
            }
            sb.Append("],\"edges\":[");
            first = true;
            foreach (var e in graph.Edges)
            {
                if (e.From.IsDummy || e.To.IsDummy) continue; // raw 단계엔 더미 없지만 방어
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"from\":").Append(JsonStr(e.From.Id))
                  .Append(",\"to\":").Append(JsonStr(e.To.Id)).Append('}');
            }
            sb.Append("]}");

            lock (_lock) _graphs.Add(sb.ToString());
        }

        /// <summary>캡처된 모든 그래프를 바탕화면의 YART_layout_graph.json으로 쓴다 (DevMode 설정 버튼에서 호출).</summary>
        public static void WriteToDesktop()
        {
            // 캡처 버퍼가 비었으면(빌드 시 DevMode가 꺼져 있었으면) 동기 리빌드로 채운다 — 버튼이 DevMode 전용이라 Capture 동작.
            bool empty;
            lock (_lock) empty = _graphs.Count == 0;
            if (empty) GraphBuildPipeline.RebuildNow();

            string json;
            lock (_lock)
            {
                if (_graphs.Count == 0)
                {
                    Messages.Message("YART_Export_Empty".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                var sb = new StringBuilder(64 * 1024);
                sb.Append("{\"graphs\":[");
                for (int i = 0; i < _graphs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_graphs[i]);
                }
                sb.Append("]}");
                json = sb.ToString();
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "YART_layout_graph.json");
            try
            {
                File.WriteAllText(path, json);
                Messages.Message("YART_Export_Done".Translate(path), MessageTypeDefOf.TaskCompletion, false);
                Log.Message("[YART] Layout graph exported to " + path);
            }
            catch (Exception ex)
            {
                Messages.Message("YART_Export_Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false);
                Log.Warning("[YART] Layout export failed: " + ex.Message);
            }
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
