using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;

namespace YART.Data
{
    public class ResearchQueueManager : GameComponent
    {
        private const int AutoStartCheckInterval = 250;

        private readonly Dictionary<ResearchChannel, List<ResearchProjectDef>> queues
            = new Dictionary<ResearchChannel, List<ResearchProjectDef>>();

        // BeginProject가 유발하는 SetCurrentProject/StopProject 포스트픽스의 재진입 방지
        private bool suppressSync;

        // 밈 충돌 확인 다이얼로그가 떠 있는 프로젝트
        private ResearchProjectDef pendingConfirmation;

        public static ResearchQueueManager Instance => Current.Game?.GetComponent<ResearchQueueManager>();

        public ResearchQueueManager(Game game)
        {
        }

        private List<ResearchProjectDef> GetOrAddQueue(ResearchChannel channel)
        {
            if (!queues.TryGetValue(channel, out var q))
            {
                q = new List<ResearchProjectDef>();
                queues[channel] = q;
            }
            return q;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            foreach (var channel in ChannelRegistry.All)
            {
                var list = GetOrAddQueue(channel);
                Scribe_Collections.Look(ref list, "queue_" + channel.Id, LookMode.Def);
                queues[channel] = list ?? new List<ResearchProjectDef>();
            }
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            foreach (var channel in queues.Keys.ToList())
            {
                queues[channel].RemoveAll(d => d == null || d.IsFinished);
                EnsureTopologicalOrder(channel);
            }
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (Find.TickManager.TicksGame % AutoStartCheckInterval == 0)
            {
                TryStartAllHeads(playSound: false);
            }
        }

        public IReadOnlyList<ResearchProjectDef> GetQueue(ResearchChannel channel) => GetOrAddQueue(channel);


        /// <summary>
        /// 채널 내 1-기준 큐 순번. 큐에 없으면 -1.
        /// </summary>
        public int GetQueuePosition(ResearchProjectDef def)
        {
            if (def == null) return -1;
            int idx = GetOrAddQueue(ChannelOf(def)).IndexOf(def);
            return idx < 0 ? -1 : idx + 1;
        }

        public static ResearchChannel ChannelOf(ResearchProjectDef def)
        {
            var graph = ResearchGraph.Instance;
            if (graph.Initialized && graph.AllNodes.TryGetValue(def, out var node))
            {
                return node.Channel;
            }
            return ResearchNode.DetermineChannel(def);
        }

        /// <summary>채널의 현재 진행 중인 프로젝트.</summary>
        public static ResearchProjectDef GetCurrentProject(ResearchChannel channel)
        {
            return channel.CurrentProject;
        }

        /// <summary>
        /// def 자신 + 미완료 선행 연구(hidden 포함) 전체를 선행-우선(위상) 순서로 반환합니다.
        /// </summary>
        public static List<ResearchProjectDef> CollectMissingChain(ResearchProjectDef def)
        {
            var result = new List<ResearchProjectDef>();
            var visited = new HashSet<ResearchProjectDef>();

            void Visit(ResearchProjectDef d)
            {
                if (d == null || d.IsFinished || !visited.Add(d)) return;
                if (d.prerequisites != null)
                {
                    foreach (var p in d.prerequisites) Visit(p);
                }
                if (d.hiddenPrerequisites != null)
                {
                    foreach (var p in d.hiddenPrerequisites) Visit(p);
                }
                result.Add(d);
            }

            Visit(def);
            return result;
        }

        /// <summary>
        /// def와 미완료 선행 체인을 각자의 채널 큐 끝에 추가합니다. (UI 진입점)
        /// </summary>
        public void EnqueueWithChain(ResearchProjectDef def)
        {
            if (def == null || def.IsFinished) return;
            if (MultiplayerCompat.TryEnqueue(def, toFront: false)) return;
            DoEnqueue(def, toFront: false);
        }

        /// <summary>
        /// def와 미완료 선행 체인을 각자의 채널 큐 "앞쪽"에 선행-우선 순서로 삽입합니다. (UI 진입점)
        /// </summary>
        public void EnqueueWithChainToFront(ResearchProjectDef def)
        {
            if (def == null || def.IsFinished) return;
            if (MultiplayerCompat.TryEnqueue(def, toFront: true)) return;
            DoEnqueue(def, toFront: true);
        }

        internal void DoEnqueue(ResearchProjectDef def, bool toFront)
        {
            if (def == null || def.IsFinished) return;

            if (!toFront)
            {
                foreach (var d in CollectMissingChain(def))
                {
                    var q = GetOrAddQueue(ChannelOf(d));
                    if (!q.Contains(d)) q.Add(d);
                }
            }
            else
            {
                // CollectMissingChain은 선행-우선(위상) 순서. 채널별로 묶어 앞에서부터 그 순서대로 삽입.
                var byChannel = new Dictionary<ResearchChannel, List<ResearchProjectDef>>();
                foreach (var d in CollectMissingChain(def))
                {
                    var ch = ChannelOf(d);
                    if (!byChannel.TryGetValue(ch, out var list))
                    {
                        list = new List<ResearchProjectDef>();
                        byChannel[ch] = list;
                    }
                    list.Add(d);
                }

                foreach (var kvp in byChannel)
                {
                    var q = GetOrAddQueue(kvp.Key);
                    foreach (var d in kvp.Value) q.Remove(d);
                    for (int i = 0; i < kvp.Value.Count; i++) q.Insert(i, kvp.Value[i]);
                }
            }

            foreach (var channel in queues.Keys.ToList())
            {
                EnsureTopologicalOrder(channel);
            }

            // 큐에 추가하면 연구 시작 사운드(바닐라 ResearchStart) 재생
            // 실제로 머리가 시작될 때 BeginProject는 무음(playSound:false)으로 두어 이중 재생 방지.
            TryStartAllHeads(playSound: false);
            MultiplayerCompat.PlayActionSound(SoundDefOf.ResearchStart);
        }

        /// <summary>
        /// 채널 큐를 비웁니다. 이 채널에서 진행 중이던 연구도 중단합니다 (UI 진입점)
        /// </summary>
        public void ClearQueue(ResearchChannel channel)
        {
            if (channel == null) return;
            if (MultiplayerCompat.TryClear(channel)) return;
            DoClear(channel);
        }

        internal void DoClear(ResearchChannel channel)
        {
            var q = GetOrAddQueue(channel);
            if (q.Count == 0) return;

            q.Clear();

            var current = GetCurrentProject(channel);
            if (current != null && !current.IsFinished)
            {
                suppressSync = true;
                try { Find.ResearchManager.StopProject(current); }
                finally { suppressSync = false; }
            }

            ResearchNode.InvalidateAllStates();
        }

        /// <summary>
        /// 큐 머리로 옮기고 즉시 시작합니다. (UI 진입점)
        /// </summary>
        public void StartNow(ResearchProjectDef def)
        {
            if (def == null || !def.CanStartNow) return;
            if (MultiplayerCompat.TryStartNow(def)) return;
            DoStartNow(def);
        }

        internal void DoStartNow(ResearchProjectDef def)
        {
            if (def == null || !def.CanStartNow) return;

            var q = GetOrAddQueue(ChannelOf(def));
            q.Remove(def);
            q.Insert(0, def);
            TryBeginProject(def, playSound: true);
        }

        /// <summary>
        /// 큐에서 제거합니다. (UI 진입점)
        /// </summary>
        public void Remove(ResearchProjectDef def)
        {
            if (def == null) return;
            if (MultiplayerCompat.TryRemove(def)) return;
            DoRemove(def);
        }

        internal void DoRemove(ResearchProjectDef def)
        {
            if (def == null) return;

            bool wasCurrent = Find.ResearchManager.IsCurrentProject(def);

            foreach (var q in queues.Values) q.Remove(def);

            // 의존 항목 연쇄 제거
            var dependents = queues.Values
                .SelectMany(q => q)
                .Where(d => CollectMissingChain(d).Contains(def))
                .ToList();
            foreach (var dep in dependents)
            {
                foreach (var q in queues.Values) q.Remove(dep);
            }

            if (wasCurrent)
            {
                suppressSync = true;
                try { Find.ResearchManager.StopProject(def); }
                finally { suppressSync = false; }
            }

            ResearchNode.InvalidateAllStates();
            TryStartAllHeads(playSound: false);
        }

        /// <summary>
        /// 큐 바 드래그 재정렬 (UI 진입점)
        /// </summary>
        public void Reorder(ResearchChannel channel, ResearchProjectDef def, int targetIndex)
        {
            if (channel == null || def == null) return;
            if (MultiplayerCompat.TryReorder(channel, def, targetIndex)) return;
            DoReorder(channel, def, targetIndex);
        }

        internal void DoReorder(ResearchChannel channel, ResearchProjectDef def, int targetIndex)
        {
            var q = GetOrAddQueue(channel);
            var oldHead = q.Count > 0 ? q[0] : null;

            if (!q.Remove(def)) return;
            q.Insert(Mathf.Clamp(targetIndex, 0, q.Count), def);
            EnsureTopologicalOrder(channel);

            var newHead = q.Count > 0 ? q[0] : null;
            if (newHead != null && newHead != oldHead
                && !Find.ResearchManager.IsCurrentProject(newHead)
                && newHead.CanStartNow)
            {
                // 채널을 새 머리로 전환 (SetCurrentProject가 기존 프로젝트를 대체)
                TryBeginProject(newHead, playSound: true);
            }
            else
            {
                TryStartAllHeads(playSound: false);
            }
        }

        /// <summary>
        /// 모든 채널에 대해: 채널이 비어 있고 큐 머리가 시작 가능하면 시작
        /// </summary>
        public void TryStartAllHeads(bool playSound, bool announce = false)
        {
            if (Current.ProgramState != ProgramState.Playing) return;

            foreach (var kvp in queues)
            {
                var q = kvp.Value;
                q.RemoveAll(d => d == null || d.IsFinished);
                if (q.Count == 0) continue;
                if (GetCurrentProject(kvp.Key) != null) continue;

                var head = q[0];
                if (!head.CanStartNow) continue; // 막힌 머리는 스킵하지 않고 대기 (UI에 표시)

                TryBeginProject(head, playSound, announce);
            }
        }

        /// <summary>
        /// 시작 전에 바닐라(MainTabWindow_Research.AttemptBeginResearch)와 동일한
        /// Ideology 밈 충돌 검사를 수행합니다. 충돌이 있으면 확인 다이얼로그를 띄우고,
        /// 확인 시 시작 / 취소 시 큐에서 제거합니다.
        /// </summary>
        private void TryBeginProject(ResearchProjectDef proj, bool playSound, bool announce = false)
        {
            // 멀티플레이 중일 경우 우선 시작 후 이후에 밈 충돌 검사
            if (MultiplayerCompat.InMultiplayer)
            {
                BeginProject(proj, playSound, announce);
                return;
            }

            if (pendingConfirmation == proj) return; // 이미 확인 대기 중

            var missing = ComputeUnlockedDefsThatHaveMissingMemes(proj);
            if (missing.Count == 0)
            {
                BeginProject(proj, playSound, announce);
                return;
            }

            pendingConfirmation = proj;

            var sb = new StringBuilder();
            sb.Append("ResearchProjectHasDefsWithMissingMemes".Translate(proj.LabelCap)).Append(":");
            sb.AppendLine();
            foreach (var (buildable, memes) in missing)
            {
                sb.AppendLine();
                sb.Append("  - ").Append(buildable.LabelCap.Colorize(ColoredText.NameColor))
                  .Append(" (").Append(memes.ToCommaList()).Append(")");
            }

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                sb.ToString(),
                confirmedAct: delegate
                {
                    pendingConfirmation = null;
                    BeginProject(proj, playSound, announce);
                },
                cancelAct: delegate
                {
                    pendingConfirmation = null;
                    DoRemove(proj);
                }));
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }

        /// <summary>
        /// 바닐라 MainTabWindow_Research.ComputeUnlockedDefsThatHaveMissingMemes와 동일
        /// </summary>
        internal static List<(BuildableDef, List<string>)> ComputeUnlockedDefsThatHaveMissingMemes(ResearchProjectDef project)
        {
            var result = new List<(BuildableDef, List<string>)>();
            if (!ModsConfig.IdeologyActive) return result;
            if (Faction.OfPlayer.ideos?.PrimaryIdeo == null) return result;

            foreach (var unlockedDef in project.UnlockedDefs)
            {
                if (!(unlockedDef is BuildableDef buildable) || buildable.canGenerateDefaultDesignator)
                {
                    continue;
                }

                List<string> missingMemes = null;
                foreach (var meme in DefDatabase<MemeDef>.AllDefsListForReading)
                {
                    if (!Faction.OfPlayer.ideos.HasAnyIdeoWithMeme(meme) && meme.AllDesignatorBuildables.Contains(buildable))
                    {
                        if (missingMemes == null) missingMemes = new List<string>();
                        missingMemes.Add(meme.LabelCap);
                    }
                }

                if (missingMemes != null)
                {
                    result.Add((buildable, missingMemes));
                }
            }
            return result;
        }

        private void BeginProject(ResearchProjectDef proj, bool playSound, bool announce = false)
        {
            suppressSync = true;
            try
            {
                if (playSound) MultiplayerCompat.PlayActionSound(SoundDefOf.ResearchStart);
                Find.ResearchManager.SetCurrentProject(proj);
                TutorSystem.Notify_Event("StartResearchProject");
            }
            finally
            {
                suppressSync = false;
            }
            ResearchNode.InvalidateAllStates();

            if (announce)
            {
                Messages.Message($"Next research started: {proj.LabelCap}", MessageTypeDefOf.PositiveEvent);
            }
        }

        public void Notify_ProjectFinished(ResearchProjectDef proj)
        {
            foreach (var q in queues.Values) q.Remove(proj);
            ResearchNode.InvalidateAllStates();
            TryStartAllHeads(playSound: false, announce: true);
        }

        public void Notify_ProjectStopped(ResearchProjectDef proj)
        {
            if (suppressSync) return;
            // 외부(바닐라/타 모드) StopProject 반응 — 이미 결정론적 컨텍스트이므로 코어를 직접 호출(재-sync 금지).
            DoRemove(proj);
        }

        /// <summary>
        /// 외부에서 바뀐 경우 반영
        /// </summary>
        public void Notify_CurrentProjectChanged(ResearchProjectDef proj)
        {
            if (suppressSync || proj == null || proj.IsFinished) return;

            var q = GetOrAddQueue(ChannelOf(proj));
            q.Remove(proj);
            q.Insert(0, proj);
            ResearchNode.InvalidateAllStates();
        }

        /// <summary>
        /// 큐 내 항목들이 "미완료 선행은 항상 앞"이 되도록 위상 정렬합니다.
        /// (기존 큐 순서를 우선순위로 보존)
        /// </summary>
        private void EnsureTopologicalOrder(ResearchChannel channel)
        {
            var q = GetOrAddQueue(channel);
            if (q.Count < 2) return;

            var ordered = new List<ResearchProjectDef>(q.Count);
            var visited = new HashSet<ResearchProjectDef>();

            void Visit(ResearchProjectDef d)
            {
                if (d == null || !visited.Add(d)) return;
                if (d.prerequisites != null)
                {
                    foreach (var p in d.prerequisites.Where(p => !p.IsFinished && q.Contains(p)))
                        Visit(p);
                }
                if (d.hiddenPrerequisites != null)
                {
                    foreach (var p in d.hiddenPrerequisites.Where(p => !p.IsFinished && q.Contains(p)))
                        Visit(p);
                }
                ordered.Add(d);
            }

            foreach (var d in q.ToList()) Visit(d);

            q.Clear();
            q.AddRange(ordered);
        }
    }
}
