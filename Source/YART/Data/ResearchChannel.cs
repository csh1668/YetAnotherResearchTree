using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Compat;

namespace YART.Data
{
    /// <summary>
    /// 병렬 연구 채널 어댑터
    /// </summary>
    public abstract class ResearchChannel
    {
        public abstract string Id { get; }
        public abstract string Label { get; }
        public abstract Color Color { get; }
        /// <summary>이 def가 이 채널에서 진행되는가 (레지스트리 순서대로 첫 매치)</summary>
        public abstract bool Matches(ResearchProjectDef def);
        /// <summary>채널에서 현재 진행 중인 프로젝트 (없으면 null)</summary>
        public abstract ResearchProjectDef CurrentProject { get; }
        /// <summary>벤치 채널 여부 (idle 경고 톤, 연구 탭 드롭다운은 벤치 전용)</summary>
        public virtual bool IsBench => false;
        /// <summary>칩 툴팁에 덧붙일 채널 특이사항 (없으면 null)</summary>
        public virtual string Tooltip => null;
    }

    /// <summary>바닐라 벤치 채널 (ResearchManager.currentProj)</summary>
    public sealed class BenchChannel : ResearchChannel
    {
        public override string Id => "Bench";
        // 바닐라 연구 버튼 라벨 (자동 현지화: 한국어 "연구")
        public override string Label => MainButtonDefOf.Research.LabelCap;
        public override Color Color => new Color(0.35f, 0.65f, 0.95f); // Blue
        public override bool IsBench => true;
        public override bool Matches(ResearchProjectDef def) => true;
        public override ResearchProjectDef CurrentProject => Find.ResearchManager.GetProject();
    }

    /// <summary>
    /// 아노말리 지식 채널 — KnowledgeCategoryDef당 1개
    /// </summary>
    public sealed class KnowledgeChannel : ResearchChannel
    {
        public readonly KnowledgeCategoryDef Category;
        private readonly Color color;
        private readonly string label;
        private readonly string id; // GraphKey 해시가 Id를 쓰므로 매 접근 할당 방지

        public KnowledgeChannel(KnowledgeCategoryDef category, int paletteIndex)
        {
            Category = category;
            if (category == KnowledgeCategoryDefOf.Basic) color = new Color(0.85f, 0.35f, 0.35f);
            else if (category == KnowledgeCategoryDefOf.Advanced) color = new Color(0.75f, 0.25f, 0.55f);
            else color = Constraints.ChannelPalette[paletteIndex % Constraints.ChannelPalette.Length];
            // 바닐라 아노말리 연구 탭 라벨 + 카테고리 (자동 현지화: 한국어 "이상 현상: 기본/고급")
            string anomalyPrefix = ResearchTabDefOf.Anomaly != null ? (string)ResearchTabDefOf.Anomaly.LabelCap : "Anomaly";
            label = anomalyPrefix + ": " + Category.LabelCap;
            id = "Knowledge_" + Category.defName;
        }

        public override string Id => id;
        public override string Label => label;
        public override Color Color => color;
        public override bool Matches(ResearchProjectDef def) => def.knowledgeCategory == Category;
        public override ResearchProjectDef CurrentProject => Find.ResearchManager.GetProject(Category);
        public override string Tooltip => Category.overflowCategory != null
            ? $"Excess knowledge overflows to: {Category.overflowCategory.LabelCap}"
            : null;
    }

    // GravshipChannel(VGE Gravship 채널)은 Compat\GravshipCompat.cs로 이동.

    /// <summary>
    /// 채널 레지스트리
    /// </summary>
    public static class ChannelRegistry
    {
        private static List<ResearchChannel> all;
        private static ResearchChannel bench;
        private static readonly object initLock = new object();

        public static IReadOnlyList<ResearchChannel> All
        {
            get { EnsureInit(); return all; }
        }

        public static ResearchChannel Bench
        {
            get { EnsureInit(); return bench; }
        }

        private static void EnsureInit()
        {
            if (all != null) return;
            lock (initLock)
            {
                if (all != null) return;

                var list = new List<ResearchChannel>();
                int paletteIndex = 0;
                foreach (var category in DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading)
                {
                    list.Add(new KnowledgeChannel(category, paletteIndex++));
                }
                if (GravshipCompat.IsVanillaGravshipExpandedLoaded.Value)
                {
                    list.Add(new GravshipChannel());
                }
                bench = new BenchChannel();
                list.Add(bench);

                if (Prefs.DevMode) Log.Message($"[YART] Channels: {string.Join(", ", list.Select(c => c.Id))}");
                all = list;
            }
        }

        public static ResearchChannel Of(ResearchProjectDef def)
        {
            EnsureInit();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Matches(def)) return all[i];
            }
            return bench;
        }

        /// <summary>채널 Id로 조회 미상이면 Bench 폴백.</summary>
        public static ResearchChannel ById(string id)
        {
            EnsureInit();
            if (string.IsNullOrEmpty(id)) return bench;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Id == id) return all[i];
            }
            return bench;
        }
    }
}
