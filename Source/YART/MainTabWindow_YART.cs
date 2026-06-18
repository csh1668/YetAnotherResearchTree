using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;
using YART.Data;
using YART.Utils;

namespace YART
{
    public partial class MainTabWindow_YART : MainTabWindow
    {
        private readonly HistoryNavigator<ResearchNode> history = new HistoryNavigator<ResearchNode>();
        private readonly List<Rect> interactionExcludeRects = new List<Rect>();

        // Viewport State
        private Vector2 scrollPosition = new Vector2(500f, 500f);
        private float zoomLevel = Constraints.DefaultZoom;

        private Vector2 leftPanelScrollPosition = Vector2.zero;
        private Rect visibleRect;
        private Rect canvasRect;
        private float eraLabelTopOffset;
        private float lastKeyPanTime;
        private ResearchProjectDef pendingOpenDef; // 외부(InfoCard 등)에서 특정 연구로 열어달라는 요청
        private string pendingSwitchPresetId;      // 프리셋 생성/편집 후 비동기 빌드 완료 시 자동 전환 대상

        // Drag / Click State.
        private Vector2 leftMouseDownPos;
        private bool leftMouseDownOnCanvas;
        private bool isDraggingCanvas;
        private bool clickPending;
        private Vector2 rightMouseDownPos;
        private bool rightMouseDownOnCanvas;
        private bool rightClickPending;

        // Graph Selection State (그래프별 뷰포트 저장/복원)
        // selectedTab: 벤치 채널에서 마지막으로 보던 탭 (다른 채널로 가도 기억)
        private ResearchChannel selectedChannelField;
        private ResearchChannel SelectedChannel
        {
            get { return selectedChannelField ?? (selectedChannelField = ChannelRegistry.Bench); }
            set { selectedChannelField = value; }
        }
        private ResearchTabDef selectedTab; // null = Main (GraphKey 생성자가 보정)
        private readonly Dictionary<GraphKey, Vector2> graphScrollPositions = new Dictionary<GraphKey, Vector2>();
        private readonly Dictionary<GraphKey, float> graphZoomLevels = new Dictionary<GraphKey, float>();

        // DoWindowContents 진입 후 설정에 따른 통합 뷰 자동 전환을 한 번만 실행하는 단발 가드
        private bool unifiedViewApplied;

        // 현재 보고 있는 프리셋(탭 그룹 또는 내장 통합). null이면 일반 탭 뷰.
        private string currentPresetId;

        /// <summary>현재 캔버스에 표시 중인 서브그래프 키</summary>
        private GraphKey CurrentKey => currentPresetId != null
            ? GraphKey.ForPreset(currentPresetId)
            : new GraphKey(SelectedChannel, selectedTab);

        /// <summary>현재 통합 벤치 뷰(모든 탭 = 내장 프리셋)를 보고 있는가.</summary>
        private bool ViewingAllTabs => currentPresetId == GraphKey.AllTabsId;

        // Queue Bar State
        private Vector2 queueBarScrollPosition = Vector2.zero;
        private ResearchProjectDef queueBarHoveredDef;
        private ResearchProjectDef draggedQueueDef;
        private ResearchProjectDef queueCardMouseDownDef;
        private Vector2 queueCardMouseDownPos;

        // Focus Mode State
        private ResearchNode hoveredNode = null;
        private readonly HashSet<ResearchNode> focusedNodes = new HashSet<ResearchNode>();
        // 호버 포커스 페이드값 (0=비활성, 1=포커스).
        private float focusAmount = 0f;
        private float lastFocusFadeTime = 0f;

        // External Dependents Popup (외부 후행 배지 호버 목록)
        private ResearchNode externalListNode;        // 현재 팝업 대상 (null = 닫힘)
        private Rect externalListRect;                // 직전 프레임 팝업 영역 (유지 판정 + 캔버스 입력 차단)
        private Rect externalListAnchor;              // 대상 배지 rect
        private int externalListPage;
        private ResearchNode externalListCandidate;   // 이번 프레임 배지 호버 (배지 draw에서 기록)
        private Rect externalListCandidateAnchor;

        // Camera Focus Animation (FocusOnNode 부드러운 이동)
        private bool scrollAnimating;
        private Vector2 scrollAnimStart;
        private Vector2 scrollAnimTarget;
        private float scrollAnimStartTime;

        // 인게임 Rebuild 후 노드 참조 무효화 감지 — Generation이 변하면 스테일 참조 초기화
        private int lastSeenGeneration;

        public override void PreOpen()
        {
            base.PreOpen();

            preventCameraMotion = true;

            // PreOpen에서도 설정하지만 SetInitialSizeAndPosition에서도 설정하여 확실하게 함
            this.windowRect.x = 0;
            this.windowRect.y = 0;
            this.windowRect.width = UI.screenWidth;
            this.windowRect.height = UI.screenHeight - Constraints.BottomTabHeight;

            // 창이 열릴 때 현재 제너레이션을 기준으로 삼는다.
            lastSeenGeneration = GraphBuildPipeline.Generation;

            // 통합 뷰 상태 초기화 — DoWindowContents 첫 진입 시 설정 기반 자동 전환을 수행한다.
            currentPresetId = null;
            unifiedViewApplied = false;

            // World Tech Level 등으로 가시성 필터 레벨이 바뀌었으면(주로 게임 진입 1회) 그래프를
            // 백그라운드로 다시 빌드한다. 필터는 정적이라 게임당 거의 1회만 트리거된다.
            if (WorldTechLevelCompat.NeedsRebuild)
                GraphBuildPipeline.RebuildNonBlocking();

            // 백그라운드 빌드 미완이어도 블로킹하지 않는다 — DoWindowContents가 준비될 때까지
            // 안내 라벨을 그리고, 창은 평소처럼 닫을 수 있다. 실패/미시작 시에만 동기 폴백.
            GraphBuildPipeline.EnsureBuiltNonBlocking();
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            this.windowRect = new Rect(0, 0, UI.screenWidth, UI.screenHeight - Constraints.BottomTabHeight);
        }

        /// <summary>그래프가 빌드되지 않는 동안 표시</summary>
        private static void DrawGraphBuildingNotice(Rect inRect)
        {
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                using (Temporary.Font(GameFont.Medium))
                {
                    int dots = 1 + (int)(Time.realtimeSinceStartup * 2f) % 3;
                    GUI.color = new Color(1f, 1f, 1f, 0.75f);
                    Widgets.Label(inRect, "YART_Building".Translate() + new string('.', dots));
                }
                using (Temporary.Font(GameFont.Small))
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.45f);
                    var hintRect = new Rect(inRect.x, inRect.y + 36f, inRect.width, inRect.height);
                    Widgets.Label(hintRect, "YART_BuildingHint".Translate());
                }
                GUI.color = Color.white;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (Temporary.Anchor(TextAnchor.UpperLeft))
            using (Temporary.Font(GameFont.Small))
            {
                if (!GraphBuildPipeline.GraphReady)
                {
                    DrawGraphBuildingNotice(inRect);
                    return;
                }

                if (GraphBuildPipeline.Generation != lastSeenGeneration)
                {
                    history.Clear();
                    focusedNodes.Clear();
                    hoveredNode = null;
                    externalListNode = null;
                    externalListCandidate = null;
                    lastSeenGeneration = GraphBuildPipeline.Generation;
                }

                if (!unifiedViewApplied)
                {
                    if (YARTMod.Settings.unifiedBenchView
                        && SelectedChannel.IsBench
                        && GetVisibleStandardTabs().Count >= 2)
                    {
                        GraphBuildPipeline.EnsureBuilt();
                        ResearchGraph.Instance.GetOrBuildUnifiedBench();
                        if (ResearchGraph.Instance.GetSubGraph(GraphKey.UnifiedBench) != null)
                        {
                            SwitchGraph(GraphKey.UnifiedBench, playSound: false);
                            unifiedViewApplied = true;
                        }
                    }
                    else
                    {
                        unifiedViewApplied = true;
                    }
                }

                // 프리셋 생성/편집 후 비동기 빌드가 끝나면 그 프리셋 뷰로 자동 전환
                if (pendingSwitchPresetId != null)
                {
                    var presetKey = GraphKey.ForPreset(pendingSwitchPresetId);
                    if (ResearchGraph.Instance.GetSubGraph(presetKey) != null)
                    {
                        SwitchGraph(presetKey, playSound: false);
                        pendingSwitchPresetId = null;
                    }
                    else if (TabPresetManager.ById(pendingSwitchPresetId) == null)
                    {
                        pendingSwitchPresetId = null; // 삭제됐거나 사라진 프리셋 — 포기
                    }
                }

                // 외부에서 특정 연구로 열어달라는 요청
                if (pendingOpenDef != null)
                {
                    var node = GetNodeOnCurrentGraph(pendingOpenDef);
                    if (node == null) ResearchGraph.Instance.AllNodes.TryGetValue(pendingOpenDef, out node);
                    if (node != null) JumpToNode(node);
                    pendingOpenDef = null;
                }

                // 마우스 뒤로/앞으로 버튼
                if (Event.current.type == EventType.MouseDown
                    && (Event.current.button == 3 || Event.current.button == 4))
                {
                    if (Event.current.button == 3 && history.CanUndo)
                    {
                        history.Undo();
                        leftPanelScrollPosition = Vector2.zero;
                        NavigateToHistoryNode(history.Current);
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    }
                    else if (Event.current.button == 4 && history.CanRedo)
                    {
                        history.Redo();
                        leftPanelScrollPosition = Vector2.zero;
                        NavigateToHistoryNode(history.Current);
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    }
                    Event.current.Use();
                }

                // 키보드: Ctrl+F = 검색 포커스, Home = 전체 보기
                if (Event.current.type == EventType.KeyDown)
                {
                    if (Event.current.control && Event.current.keyCode == KeyCode.F)
                    {
                        GUI.FocusControl("YARTSearchField");
                        Event.current.Use();
                    }
                    else if (Event.current.keyCode == KeyCode.Home
                             && GUI.GetNameOfFocusedControl() != "YARTSearchField")
                    {
                        ZoomToFit();
                        SoundDefOf.Click.PlayOneShotOnCamera();
                        Event.current.Use();
                    }
                }

                // 전체 화면을 캔버스로 사용
                canvasRect = inRect;

                // 상단 큐 바
                var queueMgr = ResearchQueueManager.Instance;
                Rect? queueBarRect = null;
                if (queueMgr != null)
                {
                    queueBarRect = new Rect(0f, 0f, inRect.width, Constraints.QueueBarHeight);
                }

                // 좌상단
                Rect topControlsRect = ComputeTopLeftControlsRect();

                // 시대 라벨
                eraLabelTopOffset = queueBarRect != null ? Constraints.QueueBarHeight : 52f;

                // 좌측 상세 패널 영역
                var leftRightWidth = Mathf.Max(300f, inRect.width * 0.25f);
                Rect? leftRect = null;
                bool shouldDrawLeftPanel = history.HasCurrent;
                if (shouldDrawLeftPanel)
                {
                    leftRect = new Rect(0f, Constraints.QueueBarHeight, leftRightWidth,
                        inRect.height - Constraints.QueueBarHeight);
                }

                // 우하단 트랙 칩 영역
                Rect chipsRect = ComputeTrackChipsRect(inRect);

                // 우상단 즐겨찾기 퀵리스트 (상단탭 바로 아래)
                Rect favoritesRect = ComputeFavoritesRect(inRect);
                Rect zoomRect = new Rect((leftRect?.xMax ?? 0f) + 16f, inRect.yMax - 40f, 80f, 24f);
                // 바닐라 연구창 전환 아이콘 버튼 (줌 라벨 오른쪽)
                Rect vanillaSwitchRect = new Rect(zoomRect.xMax + 6f, zoomRect.y, 24f, 24f);
                // 설정 아이콘 버튼 (전환 버튼 오른쪽)
                Rect settingsButtonRect = new Rect(vanillaSwitchRect.xMax + 6f, zoomRect.y, 24f, 24f);

                // 1. 메인 캔버스 그리기 (배경)
                interactionExcludeRects.Clear();
                interactionExcludeRects.Add(topControlsRect);
                if (queueBarRect != null) interactionExcludeRects.Add(queueBarRect.Value);
                // 검색 결과 드롭다운 (직전 프레임 위치 — IMGUI 특성상 1프레임 지연은 무해)
                if (searchResultsRect.height > 0f) interactionExcludeRects.Add(searchResultsRect);
                // 외부 후행 호버 팝업 (동일하게 직전 프레임 위치)
                if (externalListRect.height > 0f) interactionExcludeRects.Add(externalListRect);
                if (shouldDrawLeftPanel) interactionExcludeRects.Add(leftRect.Value);
                if (chipsRect.height > 0f) interactionExcludeRects.Add(chipsRect);
                if (favoritesRect.height > 0f) interactionExcludeRects.Add(favoritesRect);
                interactionExcludeRects.Add(zoomRect);
                interactionExcludeRects.Add(vanillaSwitchRect);
                interactionExcludeRects.Add(settingsButtonRect);

                DoGraphCanvas(canvasRect);

                // 2. 상단 큐 바 그리기
                if (queueBarRect != null)
                {
                    DoQueueBar(queueBarRect.Value, topControlsRect.width);
                }
                else
                {
                    queueBarHoveredDef = null;
                }

                HandleSearchDropdownScroll();

                // 3. 좌측 패널 그리기
                if (shouldDrawLeftPanel)
                {
                    DoLeftRect(leftRect.Value);
                }

                // 4. 플로팅 HUD
                DoTopLeftControls();
                DoTrackChipsColumn(chipsRect);
                DoFavoritesList(favoritesRect);
                DoZoomIndicator(zoomRect);
                DoVanillaSwitchButton(vanillaSwitchRect);
                DoSettingsButton(settingsButtonRect);

                // 5. 외부 후행 호버 팝업
                DrawExternalDependentsPopup();

                // 6. 성능 측정 오버레이
                DrawPerfOverlay(inRect);

                if ((Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp)
                    && Event.current.button >= 2)
                {
                    Event.current.Use();
                }
            }
        }

        private void SwitchGraph(GraphKey key, bool playSound = true)
        {
            if (key.Equals(CurrentKey)) return;

            graphScrollPositions[CurrentKey] = scrollAnimating ? scrollAnimTarget : scrollPosition;
            graphZoomLevels[CurrentKey] = zoomLevel;
            scrollAnimating = false;

            currentPresetId = key.IsPreset ? key.PresetId : null;
            SelectedChannel = key.Channel;
            if (!key.IsPreset && key.Channel.IsBench) selectedTab = key.Tab;

            scrollPosition = graphScrollPositions.TryGetValue(key, out var s) ? s : new Vector2(500f, 500f);
            zoomLevel = graphZoomLevels.TryGetValue(key, out var z) ? z : Constraints.DefaultZoom;

            hoveredNode = null;
            focusedNodes.Clear();
            focusAmount = 0f;
            queueBarHoveredDef = null;
            draggedQueueDef = null;
            if (playSound) SoundDefOf.Click.PlayOneShotOnCamera();
        }

        /// <summary>
        /// 외부(바닐라 InfoCard 하이퍼링크 등)에서 특정 연구로 YART를 열어달라는 요청.
        /// </summary>
        public void RequestOpenAt(ResearchProjectDef def)
        {
            pendingOpenDef = def;
        }

        // ── 프리셋 에디터(Dialog_PresetEditor)용 공개 API ───────────────────────────
        /// <summary>현재 표시 중인 그래프 키 (에디터가 프리뷰 전 복원 지점을 기억하는 용도).</summary>
        public GraphKey CurrentGraphKey => CurrentKey;

        /// <summary>에디터의 눈(프리뷰) 또는 복원에서 캔버스를 특정 키로 즉시 전환 (그래프 준비 시에만).</summary>
        public void PreviewGraphKey(GraphKey key)
        {
            if (GraphBuildPipeline.GraphReady) SwitchGraph(key, playSound: false);
        }

        /// <summary>프리셋 생성/편집 후 비동기 빌드가 끝나면 그 프리셋 뷰로 자동 전환하도록 예약.</summary>
        public void RequestPresetSwitch(string presetId)
        {
            pendingSwitchPresetId = presetId;
        }

        /// <summary>삭제 등으로 현재 프리셋 뷰가 무효가 되면 첫 일반 탭으로 폴백.</summary>
        public void FallbackFromPresetIfViewing(string presetId)
        {
            if (currentPresetId != presetId) return;
            var tabs = GetVisibleStandardTabs();
            SwitchGraph(tabs.Count > 0
                ? new GraphKey(ChannelRegistry.Bench, tabs[0])
                : new GraphKey(ChannelRegistry.Bench), playSound: false);
        }

        /// <summary>
        /// def를 현재 선택된 캔버스에서 표현하는 노드(원본 또는 프록시)를 반환합니다. 없으면 null.
        /// </summary>
        private ResearchNode GetNodeOnCurrentGraph(ResearchProjectDef def)
        {
            var graph = ResearchGraph.Instance;
            if (def == null || !graph.Initialized) return null;
            if (graph.AllNodes.TryGetValue(def, out var node) && node.Key.Equals(CurrentKey)) return node;
            return graph.GetNodeForGraph(def, CurrentKey);
        }

        private bool IsMouseOverBlockingElement()
        {
            Vector2 mousePos = Event.current.mousePosition;
            for (int i = 0; i < interactionExcludeRects.Count; i++)
            {
                if (interactionExcludeRects[i].Contains(mousePos)) return true;
            }
            return false;
        }
    }
}
