using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;
using YART.Rendering;

namespace YART
{
    // 캔버스/카메라/입력: 배경(그리드·시대 틴트), 서브그래프 순회 렌더링, 팬/줌, 포커스 이동
    public partial class MainTabWindow_YART
    {
        private void DoGraphCanvas(Rect rect)
        {
            // 포커스 카메라 애니메이션 진행 (입력/컬링 계산 전에 위치 갱신)
            UpdateCameraAnimation();

            // WASD 키보드 패닝 (마우스 위치 무관, 검색창 입력 중에는 무시)
            HandleKeyboardPan();

            // 이벤트 처리 (줌/팬) - 패널 영역 제외
            HandleInput(rect);

            // 스크롤(팬) 범위 제한 — 그래프가 화면에서 완전히 벗어나지 않도록 (느슨하게)
            ClampScroll(rect);

            // Culling 영역 계산
            // World = (Screen - scroll) / zoom
            Vector2 minWorld = (Vector2.zero - scrollPosition) / zoomLevel;
            Vector2 maxWorld = (new Vector2(rect.width, rect.height) - scrollPosition) / zoomLevel;

            // 화면 크기에 맞게 불필요한 렌더링을 최소화
            visibleRect = new Rect(minWorld.x - Constraints.CullingMargin.x, minWorld.y - Constraints.CullingMargin.y,
                (maxWorld.x - minWorld.x) + (Constraints.CullingMargin.x * 2),
                (maxWorld.y - minWorld.y) + (Constraints.CullingMargin.y * 2));

            Vector2 screenPos = GUIUtility.GUIToScreenPoint(rect.position);
            Rect clipRect = new Rect(screenPos.x, screenPos.y, rect.width, rect.height);

            GLLineBatcher.ClipRect = clipRect;
            GLSolidQuadBatcher.ClipRect = clipRect;
            GLTexturedQuadBatcher.ClipRect = clipRect;

            try
            {
                Vector2 viewOffset = rect.position + scrollPosition;
                bool repaint = Event.current.type == EventType.Repaint;

                if (repaint)
                {
                    GUIScreenTransform.Capture();
                    DrawCanvasBackground(rect, viewOffset, zoomLevel);
                }

                PerfBeginDraw();
                DrawGraphContent(viewOffset, zoomLevel);
                PerfEndDraw();

                // 비네팅 (콘텐츠 위, 패널 아래)
                if (repaint) GUI.DrawTexture(rect, Assets.Vignette, ScaleMode.StretchToFill);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[YART] Error drawing graph: {ex}", 938472);
            }
            finally
            {
                // Reset Clipping Limit
                GLLineBatcher.ClipRect = Rect.zero;
                GLSolidQuadBatcher.ClipRect = Rect.zero;
                GLTexturedQuadBatcher.ClipRect = Rect.zero;
            }
        }

        /// <summary>
        /// 다크 청사진 배경: 베이스 컬러 + 시대 구역 틴트 + 2단 그리드 + 노이즈 그레인.
        /// 그리드/틴트는 월드 좌표에 고정되어 팬/줌과 함께 움직인다.
        /// </summary>
        private void DrawCanvasBackground(Rect rect, Vector2 offset, float zoom)
        {
            // 1. 베이스
            Widgets.DrawBoxSolid(rect, Constraints.BackgroundColor);

            // 2. 시대 구역 틴트 (경계 x에서 시작해 오른쪽으로 페이드 아웃)
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph?.TechLevelBoundaries != null && graph.TechLevelBoundaries.Count > 0)
            {
                var boundaries = graph.TechLevelBoundaries.OrderBy(kvp => kvp.Value).ToList();
                for (int i = 0; i < boundaries.Count; i++)
                {
                    float zoneStartWorld = boundaries[i].Value;
                    // 마지막 구역은 스크롤과 무관하게 고정 폭으로 페이드 (팬 중 일렁임 방지)
                    float zoneEndWorld = i + 1 < boundaries.Count
                        ? boundaries[i + 1].Value
                        : zoneStartWorld + Constraints.GridMajorSpacing * 8f;
                    if (zoneEndWorld < visibleRect.xMin || zoneStartWorld > visibleRect.xMax) continue;

                    float x0 = (zoneStartWorld * zoom) + offset.x;
                    float x1 = (zoneEndWorld * zoom) + offset.x;
                    if (x1 <= x0) continue;

                    Color era = Constraints.GetEraColor(boundaries[i].Key);
                    GLSolidQuadBatcher.QueueGradientQuadH(
                        new Rect(x0, rect.y, x1 - x0, rect.height),
                        GenColor.WithAlpha(era, Constraints.EraZoneTintAlpha),
                        GenColor.WithAlpha(era, 0f));
                }
                GLSolidQuadBatcher.Flush();
            }

            // 3. 그리드 (보조선은 저줌에서 페이드 아웃)
            float minorAlpha = Mathf.InverseLerp(Constraints.GridMinorFadeOutZoom - 0.2f, Constraints.GridMinorFadeOutZoom, zoom);
            if (minorAlpha > 0f)
            {
                DrawGridLines(rect, offset, zoom, Constraints.GridMinorSpacing,
                    GenColor.WithAlpha(Constraints.GridMinorColor, Constraints.GridMinorColor.a * minorAlpha), skipMajorPositions: true);
            }
            DrawGridLines(rect, offset, zoom, Constraints.GridMajorSpacing, Constraints.GridMajorColor, skipMajorPositions: false);
            GLLineBatcher.Flush();

            // 주선 교차점 도트 (글로우 스프라이트, 고줌에서만)
            if (zoom >= 0.8f)
            {
                float dotAlpha = Mathf.InverseLerp(0.8f, 1.1f, zoom);
                Color dotColor = GenColor.WithAlpha(Constraints.GridDotColor, Constraints.GridDotColor.a * dotAlpha);
                float size = Constraints.GridDotSize * zoom;
                float spacing = Constraints.GridMajorSpacing;
                float startX = Mathf.Floor(visibleRect.xMin / spacing) * spacing;
                float startY = Mathf.Floor(visibleRect.yMin / spacing) * spacing;
                for (float wx = startX; wx <= visibleRect.xMax; wx += spacing)
                {
                    for (float wy = startY; wy <= visibleRect.yMax; wy += spacing)
                    {
                        float sx = (wx * zoom) + offset.x;
                        float sy = (wy * zoom) + offset.y;
                        GLTexturedQuadBatcher.QueueQuad(Assets.GlowRadial,
                            new Rect(sx - size / 2f, sy - size / 2f, size, size), dotColor);
                    }
                }
                GLTexturedQuadBatcher.Flush();
            }

            // 4. 노이즈 그레인 (스크린 공간, 절반 속도 패럴랙스)
            using (Temporary.Color(new Color(1f, 1f, 1f, Constraints.NoiseAlpha)))
            {
                float ts = Constraints.NoiseTileScreenSize;
                Rect texCoords = new Rect(-offset.x * 0.5f / ts, offset.y * 0.5f / ts, rect.width / ts, rect.height / ts);
                GUI.DrawTextureWithTexCoords(rect, Assets.NoiseTile, texCoords);
            }
        }

        private void DrawGridLines(Rect rect, Vector2 offset, float zoom, float spacingWorld, Color color, bool skipMajorPositions)
        {
            if (color.a <= 0.004f) return;

            float startX = Mathf.Floor(visibleRect.xMin / spacingWorld) * spacingWorld;
            for (float wx = startX; wx <= visibleRect.xMax; wx += spacingWorld)
            {
                // 보조선 패스에서 주선 위치는 건너뜀 (이중 렌더 방지)
                if (skipMajorPositions && Mathf.Repeat(wx, Constraints.GridMajorSpacing) < 0.01f) continue;
                float sx = (wx * zoom) + offset.x;
                GLLineBatcher.QueueLine(new Vector2(sx, rect.y), new Vector2(sx, rect.yMax), color);
            }

            float startY = Mathf.Floor(visibleRect.yMin / spacingWorld) * spacingWorld;
            for (float wy = startY; wy <= visibleRect.yMax; wy += spacingWorld)
            {
                if (skipMajorPositions && Mathf.Repeat(wy, Constraints.GridMajorSpacing) < 0.01f) continue;
                float sy = (wy * zoom) + offset.y;
                GLLineBatcher.QueueLine(new Vector2(rect.x, sy), new Vector2(rect.xMax, sy), color);
            }
        }

        private void DrawGraphContent(Vector2 offset, float zoom)
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null) return;

            // 성능 측정 스트레스 배수 — 소규모 모드팩에서도 1000/1000 부하를 모사 (DevMode)
            int reps = perfStress > 1 ? perfStress : 1;
            for (int r = 0; r < reps; r++)
            {
                DrawSubGraph(graph, offset, zoom);
            }
        }

        private void DrawSubGraph(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // 입력 처리는 이벤트 패스에서 (clickPending/rightClickPending는 MouseUp에 세팅됨 — 렌더 게이트 앞)
            HandleNodeInteraction(graph, offset, zoom);
            HandleNodeContextMenu(graph, offset, zoom);

            // ── 이하 렌더링은 Repaint 패스에서만 ──
            // IMGUI는 프레임당 OnGUI를 Layout+Repaint 2회 호출하는데, GL/배처 드로우는 Repaint에서만
            // 실제 효력이 있다. Layout 패스의 렌더 큐잉은 전부 버려지는 순수 낭비 → 프레임타임 ~절반 절감.
            if (Event.current.type != EventType.Repaint) return;

            // 호버 포커스(디밍) 갱신 — 렌더 직전
            UpdateFocusMode(graph, offset, zoom);

            // 포커스 페이드값
            float nowFade = Time.realtimeSinceStartup;
            float fadeDt = lastFocusFadeTime > 0f ? Mathf.Min(nowFade - lastFocusFadeTime, 0.1f) : 0f;
            lastFocusFadeTime = nowFade;
            float focusTarget = hoveredNode != null ? 1f : 0f;
            focusAmount = Mathf.MoveTowards(focusAmount, focusTarget, fadeDt / Constraints.FocusFadeDuration);

            // TechLevel 구분선 그리기
            DrawTechLevelLines(graph, offset, zoom);

            // 1. 엣지 먼저 그리기 (노드 뒤에 가려지도록)
            // graph.Edges 리스트를 직접 순회하여 O(E)로 최적화
            PerfSec(0); // 엣지 큐 (CPU: 앵커/색/하이라이트/리본 지오메트리)
            foreach (var edge in graph.Edges)
            {
                DrawConnection(edge.From, edge.To, offset, zoom);
            }
            PerfSecEnd();

            PerfSec(1); // 엣지 플러시 (GL 정점 제출)
            GLLineBatcher.Flush();
            GLTexturedQuadBatcher.Flush();
            PerfSecEnd();

            // 2. 노드 배경 및 그래픽 요소 그리기 (Batching)
            PerfSec(2); // 배경 큐 (CPU: 상태/색/9-slice 쿼드 생성)
            GLSolidQuadBatcher.Clear();
            foreach (var node in graph.Nodes)
            {
                DrawNodeBackground(node, offset, zoom);
            }
            PerfSecEnd();

            PerfSec(3); // 배경 플러시 (GL 정점 제출)
            GLTexturedQuadBatcher.Flush();
            GLSolidQuadBatcher.Flush();
            PerfSecEnd();

            // 2.5. 활성 경로 체인 펄스
            DrawActivePulses(graph, offset, zoom);
            GLTexturedQuadBatcher.Flush();

            // 3. 노드 텍스트 (Foreground)
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = zoom < 0.7f ? GameFont.Tiny : GameFont.Small;

            try
            {
                foreach (var node in graph.Nodes)
                {
                    DrawNodeForeground(node, offset, zoom);
                }
            }
            finally
            {
                // Reset Text State
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }

            // Flush any remaining batched lines (e.g., dummy node connectors queued in DrawNodeBackground)
            GLLineBatcher.Flush();
            PerfSecEnd();
        }

        private void DrawTechLevelLines(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            if (graph.TechLevelBoundaries == null || graph.TechLevelBoundaries.Count == 0) return;

            var boundaries = graph.TechLevelBoundaries.OrderBy(kvp => kvp.Value).ToList();

            // 1. 경계선: X는 월드 좌표에 고정, 세로로는 줌/스크롤과 무관하게 항상 캔버스 전체를 채움
            foreach (var kvp in boundaries)
            {
                float xWorld = kvp.Value;
                if (xWorld < visibleRect.xMin || xWorld > visibleRect.xMax) continue;

                float xScreen = (xWorld * zoom) + offset.x;
                GLLineBatcher.QueueLine(
                    new Vector2(xScreen, canvasRect.yMin),
                    new Vector2(xScreen, canvasRect.yMax),
                    Constraints.TechLevelLineColor);
            }

            // 2. 시대 라벨: 스크린 고정 높이(상단 큐 바/컨트롤 바로 아래)에 sticky로 항상 표시.
            //    구역 시작선 오른쪽에 붙고, 시작선이 화면 왼쪽을 벗어나면 가장자리에 고정되며,
            //    다음 시대 경계가 다가오면 밀려난다.
            float labelY = canvasRect.y + eraLabelTopOffset + Constraints.EraLabelTopMargin;

            using (Temporary.Anchor(TextAnchor.UpperLeft))
            using (Temporary.Color(Constraints.TechLevelTextColor))
            {
                for (int i = 0; i < boundaries.Count; i++)
                {
                    float zoneStart = (boundaries[i].Value * zoom) + offset.x;
                    float zoneEnd = i + 1 < boundaries.Count
                        ? (boundaries[i + 1].Value * zoom) + offset.x
                        : float.PositiveInfinity;

                    // 구역 전체가 화면 밖이면 스킵
                    if (zoneEnd < canvasRect.xMin || zoneStart > canvasRect.xMax) continue;

                    string label = TechLevelLabel(boundaries[i].Key);
                    Vector2 size = Text.CalcSize(label);

                    float x = Mathf.Max(zoneStart + Constraints.EraLabelSidePadding,
                                        canvasRect.x + Constraints.EraLabelSidePadding);
                    x = Mathf.Min(x, zoneEnd - size.x - Constraints.EraLabelSidePadding);
                    if (x + size.x < canvasRect.xMin || x > canvasRect.xMax) continue;

                    Widgets.Label(new Rect(x, labelY, size.x + 4f, size.y), label);
                }
            }
        }

        /// <summary>
        /// 노드의 화면 렉트 (픽셀 스냅). 줌인 시 리치 카드 확장(상하 대칭)이 반영되므로
        /// 렌더링과 히트테스트 모두 이 메서드를 사용해야 한다.
        /// </summary>
        private static Rect GetNodeScreenRect(ResearchNode node, Vector2 offset, float zoom)
        {
            Rect world = node.Rect;
            float extra = Constraints.RichCardExtraHeight * GetRichCardLerp(zoom);
            if (extra > 0f)
            {
                world.y -= extra / 2f;
                world.height += extra;
            }

            return new Rect(
                Mathf.Round((world.x * zoom) + offset.x),
                Mathf.Round((world.y * zoom) + offset.y),
                Mathf.Round(world.width * zoom),
                Mathf.Round(world.height * zoom));
        }

        /// <summary>
        /// WASD로 캔버스를 패닝한다. KeyDown 이벤트가 아닌 Input.GetKey를 매 프레임 폴링해
        /// 부드러운 연속 이동을 구현. 일시정지 중에도 동작하도록 Time.realtimeSinceStartup 기준 dt 사용.
        /// Repaint 패스에서만 적용해 프레임당 1회 이동(같은 프레임 다중 이벤트 중복 가산 방지).
        /// </summary>
        private void HandleKeyboardPan()
        {
            if (Event.current.type != EventType.Repaint) return;

            float now = Time.realtimeSinceStartup;
            float dt = now - lastKeyPanTime;
            lastKeyPanTime = now;

            // 검색창 입력 중에는 WASD를 타이핑으로 취급 (패닝 안 함)
            string focused = GUI.GetNameOfFocusedControl();
            if (focused == "YARTSearchField" || focused == "YARTPresetName") return; // 텍스트 입력 중엔 WASD 팬 금지
            // 첫 프레임/탭 비활성 후 복귀 등 큰 갭은 무시 (순간이동 방지)
            if (dt <= 0f || dt > 0.2f) return;

            Vector2 dir = Vector2.zero;
            if (Input.GetKey(KeyCode.W)) dir.y += 1f; // 카메라 위로 = 위쪽 콘텐츠 노출
            if (Input.GetKey(KeyCode.S)) dir.y -= 1f;
            if (Input.GetKey(KeyCode.A)) dir.x += 1f;
            if (Input.GetKey(KeyCode.D)) dir.x -= 1f;
            if (dir == Vector2.zero) return;

            scrollAnimating = false; // 유저 입력 = 포커스 애니메이션 취소
            scrollPosition += dir * (Constraints.KeyboardPanSpeed * dt);
        }

        private void HandleInput(Rect rect)
        {
            if (!Mouse.IsOver(rect)) return;

            // 마우스가 패널 위에 있으면 입력 무시
            if (IsMouseOverBlockingElement()) return;

            Event e = Event.current;

            // 줌 (휠)
            if (e.type == EventType.ScrollWheel)
            {
                float zoomDelta = -e.delta.y * 0.05f;
                float oldZoom = zoomLevel;
                float newZoom = Mathf.Clamp(oldZoom + zoomDelta, Constraints.MinZoom, Constraints.MaxZoom);

                if (Mathf.Abs(newZoom - oldZoom) > 0.001f)
                {
                    scrollAnimating = false; // 유저 줌 = 포커스 애니메이션 취소

                    Vector2 mousePos = e.mousePosition;
                    Vector2 mousePosRelative = mousePos - rect.position;

                    // 현재 마우스 위치의 월드 좌표 계산
                    Vector2 mouseWorldPos = (mousePosRelative - scrollPosition) / oldZoom;

                    zoomLevel = newZoom;

                    // 마우스 커서 중심 줌을 위한 스크롤 보정
                    scrollPosition = mousePosRelative - (mouseWorldPos * newZoom);
                }

                e.Use();
            }

            // 팬 / 클릭 판별
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                    leftMouseDownPos = e.mousePosition;
                    leftMouseDownOnCanvas = true;
                    isDraggingCanvas = false;
                    break;

                // 휠 클릭 드래그는 항상 팬
                case EventType.MouseDrag when e.button == 2:
                    scrollAnimating = false; // 유저 팬 = 포커스 애니메이션 취소
                    scrollPosition += e.delta;
                    e.Use();
                    break;

                // 좌클릭 드래그는 임계값을 넘어야 팬으로 전환 (노드 클릭과 구분)
                case EventType.MouseDrag when e.button == 0 && leftMouseDownOnCanvas:
                    if (!isDraggingCanvas &&
                        (e.mousePosition - leftMouseDownPos).sqrMagnitude >
                        Constraints.ClickDragThreshold * Constraints.ClickDragThreshold)
                    {
                        isDraggingCanvas = true;
                    }

                    if (isDraggingCanvas)
                    {
                        scrollAnimating = false; // 유저 팬 = 포커스 애니메이션 취소
                        scrollPosition += e.delta;
                        e.Use();
                    }
                    break;

                case EventType.MouseUp when e.button == 0:
                    // 드래그 없이 떼었으면 클릭 -> 같은 OnGUI 패스의 HandleNodeInteraction에서 소비
                    if (leftMouseDownOnCanvas && !isDraggingCanvas)
                    {
                        clickPending = true;
                    }
                    leftMouseDownOnCanvas = false;
                    isDraggingCanvas = false;
                    break;

                case EventType.MouseDown when e.button == 1:
                    rightMouseDownPos = e.mousePosition;
                    rightMouseDownOnCanvas = true;
                    break;

                case EventType.MouseUp when e.button == 1:
                    // 드래그 없이 떼었으면 우클릭 → 같은 패스의 HandleNodeContextMenu에서 소비
                    if (rightMouseDownOnCanvas &&
                        (e.mousePosition - rightMouseDownPos).sqrMagnitude <=
                        Constraints.ClickDragThreshold * Constraints.ClickDragThreshold)
                    {
                        rightClickPending = true;
                    }
                    rightMouseDownOnCanvas = false;
                    break;
            }
        }

        /// <summary>
        /// 지정된 노드를 화면 중앙에 위치시킵니다.
        /// </summary>
        /// <param name="node">포커스할 노드</param>
        private void FocusOnNode(ResearchNode node)
        {
            if (node == null || node.IsDummy) return;

            // 노드의 월드 좌표 중심
            Vector2 nodeCenter = node.Center;

            // 화면 중앙 좌표 (캔버스 기준)
            Vector2 screenCenter = new Vector2(canvasRect.width / 2f, canvasRect.height / 2f);

            // scrollPosition 계산: 화면중앙 = (노드월드좌표 * zoom) + scrollPosition
            // => scrollPosition = 화면중앙 - (노드월드좌표 * zoom)
            // 순간이동 대신 ease-out 애니메이션 (진행은 UpdateCameraAnimation)
            scrollAnimStart = scrollPosition;
            scrollAnimTarget = screenCenter - (nodeCenter * zoomLevel);
            scrollAnimStartTime = Time.realtimeSinceStartup;
            scrollAnimating = true;
        }

        /// <summary>
        /// FocusOnNode의 부드러운 카메라 이동 진행 (ease-out cubic).
        /// 유저가 직접 팬/줌하면 즉시 취소된다.
        /// </summary>
        private void UpdateCameraAnimation()
        {
            if (!scrollAnimating) return;

            float t = (Time.realtimeSinceStartup - scrollAnimStartTime) / Constraints.CameraFocusAnimDuration;
            if (t >= 1f)
            {
                scrollPosition = scrollAnimTarget;
                scrollAnimating = false;
                return;
            }

            float eased = 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);
            scrollPosition = Vector2.Lerp(scrollAnimStart, scrollAnimTarget, eased);
        }

        /// <summary>
        /// 팬 범위 제한: 그래프 바운딩 박스(줌 적용)가 화면에서 완전히 사라지지 않도록 스크롤을 클램프.
        /// 최소 Constraints.PanKeepVisible 만큼은 화면에 남긴다 (느슨한 제한). 그래프가 뷰포트보다
        /// 작아 범위가 뒤집히면 중앙으로 고정. 카메라 애니메이션 목표도 이 안으로 수렴.
        /// </summary>
        private void ClampScroll(Rect rect)
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null) return;
            Rect bb = graph.BoundingBox;
            if (bb.width <= 0f || bb.height <= 0f) return;

            float vw = rect.width, vh = rect.height;
            float keepX = Mathf.Min(Constraints.PanKeepVisible, vw * 0.4f);
            float keepY = Mathf.Min(Constraints.PanKeepVisible, vh * 0.4f);

            float gx = bb.xMin * zoomLevel, gw = bb.width * zoomLevel;
            float gy = bb.yMin * zoomLevel, gh = bb.height * zoomLevel;

            // screenX(worldX) = worldX*zoom + scroll.x. 그래프 우변이 keepX 이상 보이고, 좌변이 vw-keepX 이하.
            float minX = keepX - gx - gw;
            float maxX = vw - keepX - gx;
            if (minX > maxX) { float m = (minX + maxX) * 0.5f; minX = maxX = m; }

            float minY = keepY - gy - gh;
            float maxY = vh - keepY - gy;
            if (minY > maxY) { float m = (minY + maxY) * 0.5f; minY = maxY = m; }

            scrollPosition.x = Mathf.Clamp(scrollPosition.x, minX, maxX);
            scrollPosition.y = Mathf.Clamp(scrollPosition.y, minY, maxY);
        }

        /// <summary>현재 그래프 전체가 화면에 들어오도록 줌을 맞추고 중앙으로 이동한다 (Home 키).</summary>
        private void ZoomToFit()
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null || graph.BoundingBox.width <= 0f || graph.BoundingBox.height <= 0f) return;

            Rect bb = graph.BoundingBox;
            float fit = Mathf.Min(canvasRect.width / bb.width, canvasRect.height / bb.height);
            zoomLevel = Mathf.Clamp(fit, Constraints.MinZoom, Constraints.MaxZoom);

            scrollAnimStart = scrollPosition;
            scrollAnimTarget = new Vector2(canvasRect.width / 2f, canvasRect.height / 2f) - (bb.center * zoomLevel);
            scrollAnimStartTime = Time.realtimeSinceStartup;
            scrollAnimating = true;
        }

        /// <summary>
        /// 노드의 홈 그래프로 전환 + 포커스 + 좌측 패널 선택. (검색 결과/큐 카드 점프 공용)
        /// </summary>
        private void JumpToNode(ResearchNode node)
        {
            if (node == null || node.IsDummy) return;
            var realNode = node.IsProxy ? node.OriginalNode : node;
            if (realNode.IsHidden) return; // 미발견 연구로는 점프 불가

            if (!realNode.Key.Equals(CurrentKey))
            {
                SwitchGraph(realNode.Key, playSound: false); // 점프 자체 Click으로 통일 (이중 사운드 방지)
            }
            FocusOnNode(realNode);
            SoundDefOf.Click.PlayOneShotOnCamera();
            if (!history.HasCurrent || history.Current != realNode)
            {
                history.Push(realNode);
                leftPanelScrollPosition = Vector2.zero;
            }
        }
    }
}
