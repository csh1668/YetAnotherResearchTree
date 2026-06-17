using System;
using RimWorld;
using Verse;

namespace YART.Data
{
    /// <summary>
    /// 서브그래프 식별자: 병렬 채널(Channel) + 바닐라 연구 탭(Tab),
    /// 또는 사용자/내장 프리셋(병합 그래프)을 가리키는 PresetId.
    /// </summary>
    public readonly struct GraphKey : IEquatable<GraphKey>
    {
        /// <summary>통합 벤치 뷰 전용 id</summary>
        public const string AllTabsId = "__yart_all__";

        public readonly ResearchChannel Channel;
        public readonly ResearchTabDef Tab;

        public readonly string PresetId;

        public bool IsPreset => PresetId != null;

        private GraphKey(string presetId)
        {
            Channel = ChannelRegistry.Bench;
            Tab = null;
            PresetId = presetId;
        }

        public GraphKey(ResearchChannel channel, ResearchTabDef tab = null)
        {
            Channel = channel;
            Tab = channel != null && channel.IsBench ? (tab ?? ResearchTabDefOf.Main) : null;
            PresetId = null;
        }

        public static GraphKey UnifiedBench => new GraphKey(AllTabsId);

        public static GraphKey ForPreset(string presetId) => new GraphKey(presetId);

        public static GraphKey For(ResearchProjectDef def)
        {
            var channel = ChannelRegistry.Of(def);
            return new GraphKey(channel, channel.IsBench ? def.tab : null);
        }

        public bool Equals(GraphKey other)
        {
            return Channel == other.Channel && Tab == other.Tab && PresetId == other.PresetId;
        }

        public override bool Equals(object obj)
        {
            return obj is GraphKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            int channelHash = Channel != null ? Channel.Id.GetHashCode() : 0;
            int tabHash = Tab != null ? Tab.shortHash : 0;
            int presetHash = PresetId != null ? PresetId.GetHashCode() : 0;
            return channelHash ^ tabHash ^ presetHash;
        }

        public override string ToString()
        {
            string channelId = Channel != null ? Channel.Id : "null";
            if (IsPreset) return PresetId == AllTabsId ? $"{channelId}/AllTabs" : $"{channelId}/Preset:{PresetId}";
            return Tab != null ? $"{channelId}/{Tab.defName}" : channelId;
        }
    }
}
