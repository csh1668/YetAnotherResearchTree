using System;
using System.Text;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.Sound;
using YART.Data;

namespace YART.Compat
{
    /// <summary>
    /// Zetrith's Multiplayer 호환 어댑터
    ///
    /// 연구 큐를 플레이어마다 동기화 시키는 인터페이스를 제공한다
    /// 큐 메니저의 UI 진입점을 Try*로 제공하여 MP 세션에서는 Cmd_* 메서드로 동기화, SP 세션에서는 기존 Do* 메서드로 직접 실행한다
    /// 모든 인자들은 MP가 네이티브로 직렬화 할 수 있는 타입 (Defs, string, primitive)으로만 구성되어야 한다
    ///
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MultiplayerCompat
    {
        /// <summary>Whether queue mutations must be routed through MP sync (in an active MP session).</summary>
        public static bool InMultiplayer => MP.enabled && MP.IsInMultiplayer;

        static MultiplayerCompat()
        {
            if (!MP.enabled) return;
            try
            {
                MP.RegisterSyncMethod(typeof(MultiplayerCompat), nameof(Cmd_Enqueue));
                MP.RegisterSyncMethod(typeof(MultiplayerCompat), nameof(Cmd_Remove));
                MP.RegisterSyncMethod(typeof(MultiplayerCompat), nameof(Cmd_Reorder));
                MP.RegisterSyncMethod(typeof(MultiplayerCompat), nameof(Cmd_Clear));
                MP.RegisterSyncMethod(typeof(MultiplayerCompat), nameof(Cmd_ClearAndEnqueue));
                MP.RegisterSyncMethod(typeof(MultiplayerCompat), nameof(Cmd_StartNow));
                Log.Message("[YART] Multiplayer API detected — research queue sync registered.");
            }
            catch (Exception e)
            {
                Log.Error("[YART] Multiplayer compat init failed — research queue will desync in MP: " + e);
            }
        }

        // ---- UI 진입점 게이트 (MP면 sync로 보내고 true, 아니면 false=호출자가 SP 코어 실행) ----

        public static bool TryEnqueue(ResearchProjectDef def, bool toFront)
        {
            if (!InMultiplayer) return false;
            ConfirmMemesThenSync(def, () => Cmd_Enqueue(def, toFront));
            return true;
        }

        public static bool TryStartNow(ResearchProjectDef def)
        {
            if (!InMultiplayer) return false;
            ConfirmMemesThenSync(def, () => Cmd_StartNow(def));
            return true;
        }

        public static bool TryRemove(ResearchProjectDef def)
        {
            if (!InMultiplayer) return false;
            Cmd_Remove(def);
            return true;
        }

        public static bool TryReorder(ResearchChannel channel, ResearchProjectDef def, int targetIndex)
        {
            if (!InMultiplayer) return false;
            Cmd_Reorder(channel.Id, def, targetIndex);
            return true;
        }

        public static bool TryClear(ResearchChannel channel)
        {
            if (!InMultiplayer) return false;
            Cmd_Clear(channel.Id);
            return true;
        }

        public static bool TryClearAndEnqueue(ResearchProjectDef def)
        {
            if (!InMultiplayer) return false;
            ConfirmMemesThenSync(def, () => Cmd_ClearAndEnqueue(def));
            return true;
        }

        // ---- MP sync 진입점 (전 클라이언트에서 실행) ----

        public static void Cmd_Enqueue(ResearchProjectDef def, bool toFront) => ResearchQueueManager.Instance?.DoEnqueue(def, toFront);
        public static void Cmd_Remove(ResearchProjectDef def) => ResearchQueueManager.Instance?.DoRemove(def);
        public static void Cmd_StartNow(ResearchProjectDef def) => ResearchQueueManager.Instance?.DoStartNow(def);
        public static void Cmd_Clear(string channelId) => ResearchQueueManager.Instance?.DoClear(ChannelRegistry.ById(channelId));
        public static void Cmd_ClearAndEnqueue(ResearchProjectDef def) => ResearchQueueManager.Instance?.DoEnqueueExclusive(def);
        public static void Cmd_Reorder(string channelId, ResearchProjectDef def, int targetIndex)
            => ResearchQueueManager.Instance?.DoReorder(ChannelRegistry.ById(channelId), def, targetIndex);

        private static void ConfirmMemesThenSync(ResearchProjectDef def, Action syncAction)
        {
            var missing = ResearchQueueManager.ComputeUnlockedDefsThatHaveMissingMemes(def);
            if (missing.Count == 0)
            {
                syncAction();
                return;
            }

            var sb = new StringBuilder();
            sb.Append("ResearchProjectHasDefsWithMissingMemes".Translate(def.LabelCap)).Append(":");
            sb.AppendLine();
            foreach (var (buildable, memes) in missing)
            {
                sb.AppendLine();
                sb.Append("  - ").Append(buildable.LabelCap.Colorize(ColoredText.NameColor))
                  .Append(" (").Append(memes.ToCommaList()).Append(")");
            }

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(sb.ToString(), confirmedAct: () => syncAction()));
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }

        public static void PlayActionSound(SoundDef sound)
        {
            if (sound == null) return;
            if (InMultiplayer && !MP.IsExecutingSyncCommandIssuedBySelf) return;
            sound.PlayOneShotOnCamera();
        }
    }
}
