using System;
using RimWorld;
using Verse;

namespace YART.Data
{
    /// <summary>
    /// 서브그래프 식별자: 병렬 채널(Channel) + 바닐라 연구 탭(Tab).
    /// </summary>
    public readonly struct GraphKey : IEquatable<GraphKey>
    {
        public readonly ResearchChannel Channel;
        public readonly ResearchTabDef Tab;

        public readonly bool IsUnified;

        private struct UnifiedMarker { }

        private GraphKey(ResearchChannel channel, UnifiedMarker _)
        {
            Channel = channel;
            Tab = null;
            IsUnified = true;
        }

        public GraphKey(ResearchChannel channel, ResearchTabDef tab = null)
        {
            Channel = channel;
            Tab = channel != null && channel.IsBench ? (tab ?? ResearchTabDefOf.Main) : null;
            IsUnified = false;
        }

        public static GraphKey UnifiedBench => new GraphKey(ChannelRegistry.Bench, default(UnifiedMarker));

        public static GraphKey For(ResearchProjectDef def)
        {
            var channel = ChannelRegistry.Of(def);
            return new GraphKey(channel, channel.IsBench ? def.tab : null);
        }

        public bool Equals(GraphKey other)
        {
            return Channel == other.Channel && Tab == other.Tab && IsUnified == other.IsUnified;
        }

        public override bool Equals(object obj)
        {
            return obj is GraphKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            int channelHash = Channel != null ? Channel.Id.GetHashCode() : 0;
            int tabHash = Tab != null ? Tab.shortHash : 0;
            int unifiedBit = IsUnified ? unchecked((int)0x9E3779B9) : 0;
            return channelHash ^ tabHash ^ unifiedBit;
        }

        public override string ToString()
        {
            string channelId = Channel != null ? Channel.Id : "null";
            if (IsUnified) return $"{channelId}/Unified";
            return Tab != null ? $"{channelId}/{Tab.defName}" : channelId;
        }
    }
}
