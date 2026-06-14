// 게임(UnityEngine / Verse / RimWorld) 및 YART 타입의 헤드리스 대체 shim.
// SugiyamaLayout.cs / Constraints.cs 가 실제로 사용하는 표면만 제공한다 (레이아웃 전용).
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public static class Mathf
    {
        public static float Floor(float v) => MathF.Floor(v);
        public static float Ceil(float v) => MathF.Ceiling(v);
        public static float Round(float v) => MathF.Round(v, MidpointRounding.ToEven); // Unity Mathf.Round = 은행가 반올림
        public static float Abs(float v) => MathF.Abs(v);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color gray => new Color(0.5f, 0.5f, 0.5f, 1f);
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public float xMin => x;
        public float yMin => y;
        public float xMax => x + width;
        public float yMax => y + height;
        public static Rect zero => new Rect(0, 0, 0, 0);
    }
}

namespace Verse
{
    public static class Log
    {
        public static void Message(string s) { if (Prefs.DevMode) Console.Error.WriteLine("[msg] " + s); }
        public static void Warning(string s) => Console.Error.WriteLine("[warn] " + s);
        public static void Error(string s) => Console.Error.WriteLine("[err] " + s);
    }

    public static class Prefs
    {
        public static bool DevMode = false;
    }
}

namespace RimWorld
{
    // RimWorld.TechLevel (Verse 아님 — Constraints.cs가 using RimWorld만으로 해석). 값은 게임과 일치.
    public enum TechLevel : byte
    {
        Undefined = 0, Animal = 1, Neolithic = 2, Medieval = 3,
        Industrial = 4, Spacer = 5, Ultra = 6, Archotech = 7
    }
}

namespace YART
{
    // SugiyamaLayout가 토글을 읽는 진입점
    public sealed class YARTModSettings
    {
        public bool fixOverflowChasing; // Fix D (A/B/C는 효과 없어 revert됨)
    }

    public static class YARTMod
    {
        public static YARTModSettings Settings = new YARTModSettings();
    }

    // 인게임 export 훅의 no-op 대체 (하니스는 이미 파일에서 그래프를 읽으므로 캡처 불필요)
    public static class LayoutExport
    {
        public static void Begin() { }
        public static void Capture(YART.Data.ResearchSubGraph graph) { }
        public static void Flush() { }
    }
}

namespace YART.Data
{
    using RimWorld;
    using UnityEngine;

    // 레이아웃이 Key.Equals 만 사용하므로 문자열 기반 값 동등성으로 충분
    public readonly struct GraphKey : IEquatable<GraphKey>
    {
        public readonly string Id;
        public readonly bool IsUnified;
        public GraphKey(string id, bool unified = false) { Id = id; IsUnified = unified; }
        public bool Equals(GraphKey o) => Id == o.Id && IsUnified == o.IsUnified;
        public override bool Equals(object o) => o is GraphKey g && Equals(g);
        public override int GetHashCode() => (Id?.GetHashCode() ?? 0) ^ (IsUnified ? 1 : 0);
        public override string ToString() => Id;
    }

    // 레이아웃 전용 노드 (게임의 상태/큐/렌더 결합 제거)
    public sealed class ResearchNode
    {
        public bool IsDummy { get; private set; }
        public bool IsProxy { get; set; }
        public string Id { get; set; }
        public string Label { get; set; }
        public TechLevel TechLevel { get; set; }
        public GraphKey Key { get; set; }
        public int Rank { get; set; }
        public int VOrder { get; set; }
        public Vector2 Position { get; set; }
        public TechLevel? EffectiveTechLevelInternal { get; set; }
        public List<ResearchNode> Prerequisites { get; } = new List<ResearchNode>();
        public List<ResearchNode> Children { get; } = new List<ResearchNode>();

        public ResearchNode() { }

        public static ResearchNode CreateDummy(GraphKey key)
            => new ResearchNode { IsDummy = true, Key = key, Label = "" };
    }

    public sealed class ResearchEdge
    {
        public ResearchNode From { get; }
        public ResearchNode To { get; }
        public ResearchEdge(ResearchNode from, ResearchNode to) { From = from; To = to; }
    }

    public sealed class LayoutMetrics
    {
        public int Crossings;
        public int TotalEdgeSpan;
        public int BendCount;
        public float VerticalVariation;
        public int NonConvergedChains;
        public int UnjustifiedBends;
        public float MaxColumnHeight;
        public float OverflowCost;
        public int UsedRankCount;
        public float RankCost;
    }

    public sealed class ResearchSubGraph
    {
        public GraphKey Key { get; }
        public List<ResearchNode> Nodes { get; } = new List<ResearchNode>();
        public List<ResearchEdge> Edges { get; } = new List<ResearchEdge>();
        public Dictionary<TechLevel, float> TechLevelBoundaries { get; set; } = new Dictionary<TechLevel, float>();
        public LayoutMetrics Metrics { get; set; }

        public ResearchSubGraph(GraphKey key) { Key = key; }

        public void AddNode(ResearchNode n) { if (!Nodes.Contains(n)) Nodes.Add(n); }
        public void AddEdge(ResearchNode from, ResearchNode to) => Edges.Add(new ResearchEdge(from, to));
        public void UpdateBoundingBox() { /* 렌더러가 직접 bbox 계산 — no-op */ }
    }
}
