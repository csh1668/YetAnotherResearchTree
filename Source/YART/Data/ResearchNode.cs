using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace YART.Data
{
    public enum ResearchNodeState : byte
    {
        /// <summary>
        /// 선행 조건이 충족되지 않아 연구 불가
        /// </summary>
        Locked,

        /// <summary>
        /// 선행 조건 충족, 연구 시작 가능
        /// </summary>
        Available,

        /// <summary>
        /// 현재 연구 중
        /// </summary>
        InProgress,

        /// <summary>
        /// 연구 완료
        /// </summary>
        Completed
    }

    /// <summary>
    /// ResearchProjectDef를 래핑하여 UI 렌더링 및 그래프 레이아웃에 필요한 정보를 관리한다.
    /// </summary>
    public class ResearchNode
    {
        /// <summary>
        /// 원본 ResearchProjectDef 참조
        /// </summary>
        public ResearchProjectDef Def { get; }

        /// <summary>
        /// 노드가 속한 병렬 연구 채널
        /// </summary>
        public ResearchChannel Channel { get; }

        /// <summary>이 노드가 속한 바닐라 연구 탭 (벤치 채널 전용, 비-벤치는 null)</summary>
        public ResearchTabDef Tab { get; }

        // 프리셋/통합 병합 그래프 키처럼 Channel/Tab으로 재구성할 수 없는 특수 키를 보존하기 위한 오버라이드. null이면 Channel/Tab으로 계산
        private readonly GraphKey? _keyOverride;

        /// <summary>이 노드가 속한 서브그래프 키</summary>
        public GraphKey Key => _keyOverride.HasValue ? _keyOverride.Value : new GraphKey(Channel, Tab);

        public bool IsDummy { get; }

        public string Id
        {
            get
            {
                if (IsDummy) return $"Dummy_{GetHashCode()}";
                return IsProxy ? $"Proxy_{OriginalNode.Id}" : Def.defName;
            }
        }

        // 미발견 연구의 마스킹 라벨
        private static string _unknownLabel;
        private static string UnknownLabel => _unknownLabel ?? (_unknownLabel = string.Format("({0})", "UnknownResearch".Translate()));

        public string Label
        {
            get
            {
                if (IsDummy) return string.Empty;
                if (IsProxy) return OriginalNode.Label;
                if (IsHidden) return UnknownLabel;
                return Def.label.CapitalizeFirst();
            }
        }

        /// <summary>
        /// 이 노드가 다른 구역의 연구를 참조하는 프록시 노드인지 여부
        /// </summary>
        public bool IsProxy { get; }

        /// <summary>
        /// 프록시 노드인 경우, 원본 노드에 대한 참조
        /// </summary>
        public ResearchNode OriginalNode { get; }

        /// <summary>
        /// Sugiyama 알고리즘에서 할당된 계층(Rank) 값
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// 같은 Rank 내에서의 수직 순서 (인덱스)
        /// </summary>
        public int VOrder { get; set; }

        /// <summary>
        /// 캔버스 상의 위치 (좌상단 기준)
        /// </summary>
        public Vector2 Position { get; set; }
        public Rect Rect => new Rect(Position.x, Position.y, Constraints.NodeSize.x, Constraints.NodeSize.y);
        public Vector2 Center => new Vector2(Position.x + Constraints.NodeSize.x / 2f, Position.y + Constraints.NodeSize.y / 2f);

        /// <summary>
        /// 선행 연구 노드들
        /// </summary>
        public List<ResearchNode> Prerequisites { get; } = new List<ResearchNode>();

        private List<ResearchNode> _cachedAncestors;

        public List<ResearchNode> Ancestors
        {
            get
            {
                if (_cachedAncestors == null)
                {
                    var ancestors = new HashSet<ResearchNode>();

                    void CollectAncestors(ResearchNode curNode)
                    {
                        foreach (var prereq in curNode.Prerequisites.Where(prereq => ancestors.Add(prereq)))
                        {
                            CollectAncestors(prereq);
                        }
                    }

                    CollectAncestors(this);
                    _cachedAncestors = ancestors.ToList();
                }

                return _cachedAncestors;
            }
        }

        /// <summary>
        /// 후속 연구 노드들
        /// </summary>
        public List<ResearchNode> Children { get; } = new List<ResearchNode>();

        // 좌측 패널용 사전 계산 목록 (그래프 빌드에서 채움)
        public List<ResearchNode> PanelPrerequisites { get; set; }
        public List<ResearchNode> PanelChildren { get; set; }

        // 다른 그래프(탭/채널)에 사는 후행 연구들
        public List<ResearchNode> ExternalChildren { get; set; }


        private ResearchNodeState _cachedState = ResearchNodeState.Locked;
        private float _lastStateUpdateTime = float.NegativeInfinity;
        private int _lastStateVersion = -1;

        // 전역 상태 버전
        private static int _globalStateVersion;

        /// <summary>
        /// 모든 노드의 상태 캐시를 즉시 무효화합니다
        /// </summary>
        public static void InvalidateAllStates() => _globalStateVersion++;

        /// <summary>
        /// 현재 연구 상태를 반환합니다. (캐싱 적용)
        /// </summary>
        public ResearchNodeState State
        {
            get
            {
                if (IsDummy) return ResearchNodeState.Locked;
                if (IsProxy) return OriginalNode.State;

                // 백그라운드 스레드(병렬 레이아웃 빌드 등)에서는 재계산 금지 — CalculateState가
                // CanBeQueuedRecursively → CanStartNow → Find.ResearchManager/맵 폰 등 메인스레드 전용
                // API를 건드려 MapPawns.AssertMainThread에 걸린다. 레이아웃은 기하만 쓰고 State에
                // 의존하지 않으므로 캐시값만 반환한다(메인스레드에서 렌더 시 정상 재계산됨).
                if (!UnityData.IsInMainThread) return _cachedState;

                // 0.5초마다 갱신
                if (_lastStateVersion != _globalStateVersion
                    || Time.realtimeSinceStartup - _lastStateUpdateTime > 0.5f)
                {
                    _cachedState = CalculateState();
                    _cachedHidden = Def.IsHidden;
                    _lastStateUpdateTime = Time.realtimeSinceStartup;
                    _lastStateVersion = _globalStateVersion;
                }
                return _cachedState;
            }
        }

        private bool _cachedHidden;

        /// <summary>
        /// 아노말리 미발견 연구인가?
        /// </summary>
        public bool IsHidden
        {
            get
            {
                if (IsDummy) return false;
                if (IsProxy) return OriginalNode.IsHidden;
                // 그래프 빌드 시간 (메인 메뉴 LongEvent)에서 실행하면 NRE 나므로, 방지 수단
                if (Current.ProgramState != ProgramState.Playing) return false;
                _ = State;
                return _cachedHidden;
            }
        }

        private ResearchNodeState CalculateState()
        {
            if (Def.IsFinished) return ResearchNodeState.Completed;
            if (Find.ResearchManager.IsCurrentProject(Def)) return ResearchNodeState.InProgress;
            if (CanBeQueuedRecursively()) return ResearchNodeState.Available;
            return ResearchNodeState.Locked;
        }

        /// <summary>
        /// 연구 진행률 (0.0 ~ 1.0)
        /// </summary>
        public float Progress
        {
            get
            {
                if (IsDummy) return 0f;
                if (IsProxy) return OriginalNode.Progress;
                if (Def.IsFinished) return 1f;
                return Def.ProgressPercent;
            }
        }

        private TechLevel? _techLevelInternal;

        public TechLevel TechLevel
        {
            get
            {
                if (_techLevelInternal != null) return _techLevelInternal.Value;

                if (IsDummy) return TechLevel.Undefined;
                _techLevelInternal = IsProxy ? OriginalNode.TechLevel : Def.techLevel;
                return _techLevelInternal.Value;
            }
            set
            {
                _techLevelInternal = value;
            }
        }

        /// <summary>
        /// 레이아웃의 유효 TechLevel(= max(자신, 조상 전체)) — SugiyamaLayout이 채운다
        /// </summary>
        public TechLevel? EffectiveTechLevelInternal { get; set; }

        /// <summary>색 결정에 쓸 TechLevel</summary>
        private TechLevel ColorTechLevel =>
            YARTMod.Settings.unifyEraColorToEffective && EffectiveTechLevelInternal.HasValue
                ? EffectiveTechLevelInternal.Value
                : TechLevel;

        public Color EraAccentColor
        {
            get
            {
                if (IsDummy) return Constraints.EdgeDefault;
                if (IsProxy) return OriginalNode.EraAccentColor;

                return ActiveColors.Era(ColorTechLevel);
            }
        }

        public ModContentPack SourceMod => IsProxy ? OriginalNode.SourceMod : Def?.modContentPack;

        private List<Def> _cachedUnlockedDefs;

        public List<Def> UnlockedDefs
        {
            get
            {
                if (_cachedUnlockedDefs == null)
                {
                    _cachedUnlockedDefs = new List<Def>();
                    if (IsDummy || IsProxy || Def == null) return _cachedUnlockedDefs;

                    // 1. Explicit unlocks listed in ResearchProjectDef
                    if (Def.UnlockedDefs != null)
                    {
                        _cachedUnlockedDefs.AddRange(Def.UnlockedDefs);
                    }

                    // 2. Scan specific DefDatabases for reverse references (the vanilla way)
                    // Things (Buildings, Items, Plants)
                    foreach (var thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
                    {
                        if (thingDef.researchPrerequisites != null && thingDef.researchPrerequisites.Contains(Def))
                        {
                            _cachedUnlockedDefs.Add(thingDef);
                        }
                        // Check for plant sow research
                        else if (thingDef.plant?.sowResearchPrerequisites != null && thingDef.plant.sowResearchPrerequisites.Contains(Def))
                        {
                            _cachedUnlockedDefs.Add(thingDef);
                        }
                    }

                    // Terrain (Floors)
                    foreach (var terrainDef in DefDatabase<TerrainDef>.AllDefsListForReading)
                    {
                        if (terrainDef.researchPrerequisites != null && terrainDef.researchPrerequisites.Contains(Def))
                        {
                            _cachedUnlockedDefs.Add(terrainDef);
                        }
                    }

                    // Recipes
                    foreach (var recipeDef in DefDatabase<RecipeDef>.AllDefsListForReading)
                    {
                        if (recipeDef.researchPrerequisite == Def || (recipeDef.researchPrerequisites != null && recipeDef.researchPrerequisites.Contains(Def)))
                        {
                            _cachedUnlockedDefs.Add(recipeDef);
                        }
                    }

                    // Distinct to remove duplicates just in case
                    _cachedUnlockedDefs = _cachedUnlockedDefs.Distinct().ToList();
                }
                return _cachedUnlockedDefs;
            }
        }

        /// <summary>
        /// 일반 연구 노드를 생성합니다
        /// </summary>
        public ResearchNode(ResearchProjectDef def, GraphKey key)
        {
            Def = def;
            Channel = key.Channel;
            Tab = key.Tab;
            IsProxy = false;
            OriginalNode = null;
            IsDummy = false;
            _keyOverride = null;
        }

        public ResearchNode(ResearchNode originalNode, GraphKey targetKey)
        {
            Def = originalNode.Def;
            Channel = targetKey.Channel;
            Tab = targetKey.Tab;
            IsProxy = true;
            OriginalNode = originalNode;
            IsDummy = false;
            // 프록시가 속한 그래프 키를 보존
            _keyOverride = targetKey;
        }

        // Private constructor for dummy nodes
        private ResearchNode(GraphKey key, bool isDummy)
        {
            Def = null;
            Channel = key.Channel;
            Tab = key.Tab;
            IsProxy = false;
            OriginalNode = null;
            IsDummy = isDummy;
            // 더미가 속한 그래프 키를 보존
            _keyOverride = key;
        }

        public static ResearchNode CreateMergedCopy(ResearchNode original, GraphKey targetKey)
        {
            return new ResearchNode(original.Def, original.Channel, original.Tab, targetKey);
        }

        private ResearchNode(ResearchProjectDef def, ResearchChannel channel, ResearchTabDef tab, GraphKey keyOverride)
        {
            Def = def;
            Channel = channel;
            Tab = tab;
            IsProxy = false;
            OriginalNode = null;
            IsDummy = false;
            _keyOverride = keyOverride;
        }

        /// <summary>
        /// 이 연구가 연구 큐에 추가될 수 있는지 재귀적으로 확인합니다.
        /// </summary>
        public bool CanBeQueuedRecursively(HashSet<ResearchProjectDef> visited = null)
        {
            if (IsDummy) return false;
            if (IsProxy) return OriginalNode.CanBeQueuedRecursively(visited);

            // 이미 완료된 연구는 큐에 추가할 필요 없음 (하지만 체인에서는 유효)
            if (Def.IsFinished) return true;

            // 선행 연구 외의 조건이 충족되지 않으면 큐에 추가 불가
            if (!CanResearchIgnoringPrerequisites()) return false;

            if (visited == null) visited = new HashSet<ResearchProjectDef>();
            if (visited.Contains(Def)) return false;
            visited.Add(Def);

            // 모든 선행 연구가 완료되었거나 재귀적으로 큐에 추가 가능한지 확인
            var graph = ResearchGraph.Instance;
            if (graph == null || !graph.Initialized) return false;

            if (Def.prerequisites != null)
            {
                foreach (var prereqDef in Def.prerequisites)
                {
                    if (prereqDef.IsFinished) continue;

                    if (!graph.AllNodes.TryGetValue(prereqDef, out var prereqNode))
                        return false;
                    if (!prereqNode.CanBeQueuedRecursively(visited))
                        return false;
                }
            }

            if (Def.hiddenPrerequisites != null)
            {
                foreach (var prereqDef in Def.hiddenPrerequisites)
                {
                    if (prereqDef.IsFinished) continue;

                    if (!graph.AllNodes.TryGetValue(prereqDef, out var prereqNode))
                        return false;
                    if (!prereqNode.CanBeQueuedRecursively(visited))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 선행 연구를 제외한 다른 조건들이 충족되었는지 확인합니다.
        /// (기술 레벨, 필요 시설, TechPrint 등)
        /// </summary>
        private bool CanResearchIgnoringPrerequisites()
        {
            if (IsDummy) return false;
            if (IsProxy) return OriginalNode.CanResearchIgnoringPrerequisites();
            if (Def.IsFinished) return false;

            // 이 연구의 모든 선행 연구가 끝났다면, CanStartNow로 판별한다
            // 여러 모드에서 Postfix 등으로 사용하고 있음 (호환성 존중 가능)
            if (Def.PrerequisitesCompleted) return Def.CanStartNow;

            // TechPrint, 중력 구동기 점검, 메카나이터, 아노말리 지식, 특수 연구 작업대 체크
            return Def.TechprintRequirementMet
                && (Def.requiredResearchBuilding == null || Def.PlayerHasAnyAppropriateResearchBench)
                && Def.PlayerMechanitorRequirementMet
                && Def.AnalyzedThingsRequirementsMet
                && !Def.IsHidden
                && Def.InspectionRequirementsMet;
        }

        /// <summary>
        /// 이 노드가 Locked인 이유들을 outReasons에 채운다.
        /// 판정은 바닐라 MainTabWindow_Research.DrawStartButton의 lockedReasons와 동일
        /// </summary>
        public void GetLockedReasons(List<string> outReasons)
        {
            outReasons.Clear();
            if (IsDummy) return;
            if (IsProxy) { OriginalNode.GetLockedReasons(outReasons); return; }
            if (Def.IsFinished) return;

            if (!Def.TechprintRequirementMet)
            {
                outReasons.Add("InsufficientTechprintsApplied".Translate(Def.TechprintsApplied, Def.TechprintCount));
            }
            if (!Def.InspectionRequirementsMet)
            {
                outReasons.Add("MissingGravEngineInspection".Translate());
            }
            if (!Def.PlayerMechanitorRequirementMet)
            {
                outReasons.Add("MissingRequiredMechanitor".Translate());
            }
            if (!Def.AnalyzedThingsRequirementsMet && Def.requiredAnalyzed != null)
            {
                foreach (var thing in Def.requiredAnalyzed)
                {
                    outReasons.Add("NotStudied".Translate(thing.LabelCap));
                }
            }
            if (Def.requiredResearchBuilding != null && !Def.PlayerHasAnyAppropriateResearchBench)
            {
                // CanBeResearchedAt이 작업대 + 연결된 활성 시설(복합분석기 등)까지 검사하므로 둘 다 명시
                string detail = Def.requiredResearchBuilding.LabelCap;
                if (!Def.requiredResearchFacilities.NullOrEmpty())
                {
                    detail += " + " + string.Join(", ", Def.requiredResearchFacilities.Select(f => f.label));
                }
                outReasons.Add("MissingRequiredResearchFacilities".Translate() + " (" + detail + ")");
            }

            // 자체 조건은 충족 — 선행 체인 중 잠긴 연구 안내
            if (outReasons.Count == 0 && State == ResearchNodeState.Locked)
            {
                foreach (var ancestor in Ancestors)
                {
                    if (ancestor.IsDummy || ancestor.Def == null || ancestor.Def.IsFinished) continue;
                    if (!ancestor.CanResearchIgnoringPrerequisites())
                    {
                        outReasons.Add($"Prerequisite locked: {ancestor.Label}");
                    }
                }
            }
        }

        public Vector2 InputAnchor => new Vector2(Position.x, Position.y + Constraints.NodeSize.y / 2f);
        public Vector2 OutputAnchor => new Vector2(Position.x + Constraints.NodeSize.x, Position.y + Constraints.NodeSize.y / 2f);

        // 엣지 포트 분산
        private static int globalPortVersion;
        private int portVersion = -1;
        private Dictionary<ResearchNode, (float y, float ratio)> outputPorts;
        private Dictionary<ResearchNode, (float y, float ratio)> inputPorts;

        public static void InvalidateAllPorts()
        {
            globalPortVersion++;
        }

        /// <summary>
        /// to로 나가는 엣지의 출발점
        /// </summary>
        public Vector2 GetOutputAnchor(ResearchNode to)
        {
            if (IsDummy || to == null) return OutputAnchor;
            EnsurePorts();
            return outputPorts.TryGetValue(to, out var port)
                ? new Vector2(Position.x + Constraints.NodeSize.x, port.y)
                : OutputAnchor;
        }

        /// <summary>
        /// from에서 들어오는 엣지의 도착점
        /// </summary>
        public Vector2 GetInputAnchor(ResearchNode from)
        {
            if (IsDummy || from == null) return InputAnchor;
            EnsurePorts();
            return inputPorts.TryGetValue(from, out var port)
                ? new Vector2(Position.x, port.y)
                : InputAnchor;
        }

        /// <summary>
        /// to로 나가는 포트의 팬 내 정규화 순번 (0=최상단 ~ 1=최하단)
        /// </summary>
        public float GetOutputPortRatio(ResearchNode to)
        {
            if (IsDummy || to == null) return 0.5f;
            EnsurePorts();
            return outputPorts.TryGetValue(to, out var port) ? port.ratio : 0.5f;
        }

        /// <summary>
        /// from에서 들어오는 포트의 팬 내 정규화 순번 (0=최상단 ~ 1=최하단)
        /// </summary>
        public float GetInputPortRatio(ResearchNode from)
        {
            if (IsDummy || from == null) return 0.5f;
            EnsurePorts();
            return inputPorts.TryGetValue(from, out var port) ? port.ratio : 0.5f;
        }

        private void EnsurePorts()
        {
            if (portVersion == globalPortVersion && outputPorts != null) return;
            portVersion = globalPortVersion;
            outputPorts = BuildPorts(Children, c => c.InputAnchor.y);
            inputPorts = BuildPorts(Prerequisites, p => p.OutputAnchor.y);
        }

        private Dictionary<ResearchNode, (float y, float ratio)> BuildPorts(List<ResearchNode> counterparts, Func<ResearchNode, float> sortKey)
        {
            var ports = new Dictionary<ResearchNode, (float y, float ratio)>(counterparts.Count);
            int n = counterparts.Count;
            float centerY = Position.y + Constraints.NodeSize.y / 2f;
            switch (n)
            {
                case 0: return ports;
                case 1:
                    ports[counterparts[0]] = (centerY, 0.5f);
                    return ports;
            }

            // 상대 Y 오름차순으로 포트를 배치해 같은 노드에서 나가는 엣지끼리는 교차가 생기지 않게 함
            var sorted = counterparts.OrderBy(sortKey).ThenBy(c => c.Position.x).ToList();
            float span = Constraints.NodeSize.y - 2f * Constraints.EdgePortPadding;
            float spacing = Mathf.Min(Constraints.EdgePortMaxSpacing, span / (n - 1));
            float firstY = centerY - spacing * (n - 1) / 2f;
            for (int i = 0; i < sorted.Count; i++)
            {
                ports[sorted[i]] = (firstY + spacing * i, i / (float)(n - 1));
            }
            return ports;
        }

        public override string ToString()
        {
            return $"ResearchNode[{Id}, Graph={Key}, Rank={Rank}]";
        }

        public static ResearchNode Create(ResearchProjectDef def)
        {
            return new ResearchNode(def, GraphKey.For(def));
        }

        public static ResearchNode CreateProxy(ResearchNode originalNode, GraphKey targetKey)
        {
            return new ResearchNode(originalNode, targetKey);
        }

        public static ResearchNode CreateDummy(GraphKey key)
        {
            return new ResearchNode(key, true);
        }

        public static void CollectConnectedNodes(ResearchNode node, HashSet<ResearchNode> collected, bool upstream, Predicate<ResearchNode> filter = null, Predicate<ResearchNode> recurseCondition = null)
        {
            var nextNodes = upstream ? node.Prerequisites : node.Children;
            foreach (var nextNode in nextNodes)
            {
                // 1. Filter: Determine if the node should be collected (and potentially recursed)
                if (filter != null && !filter(nextNode)) continue;

                // 2. Collect: Add to the set
                if (collected.Add(nextNode))
                {
                    // 3. Recurse: Check if we should continue traversal from this node
                    // If recurseCondition is null, default is to always recurse.
                    if (recurseCondition == null || recurseCondition(nextNode))
                    {
                        CollectConnectedNodes(nextNode, collected, upstream, filter, recurseCondition);
                    }
                }
            }
        }

        public static ResearchChannel DetermineChannel(ResearchProjectDef def)
        {
            return ChannelRegistry.Of(def);
        }
    }
}
