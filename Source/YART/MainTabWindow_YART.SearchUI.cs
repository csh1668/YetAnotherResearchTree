using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART
{
    public partial class MainTabWindow_YART
    {
        private enum SearchDropdownMode { None, Help, ModComplete, Results }

        private static readonly (string syntaxKey, string descKey)[] SearchSyntaxHints =
        {
            ("YART_SearchHint_WordsSyntax", "YART_SearchHint_WordsDesc"),
            ("YART_SearchHint_ModSyntax", "YART_SearchHint_ModDesc"),
        };

        private string modSuggestPartial; // RefreshModSuggestions 캐시 키
        private readonly List<(string name, string packageId)> modSuggestions = new List<(string, string)>();

        private string searchQuery = ""; // 검색창에 커밋된 텍스트 (TextField 바인딩)
        private string searchEffective = ""; // 실제 검색에 쓰는 쿼리 = 커밋분 + IME 조합분
        // 매치 판정은 Def 기준 — 통합(All tabs) 뷰의 복사본 노드도 같은 Def로 매치되게 (인스턴스 비교는 통합서 실패)
        private readonly HashSet<ResearchProjectDef> matchedDefs = new HashSet<ResearchProjectDef>();
        private readonly List<ResearchSearchEngine.SearchResult> searchResults = new List<ResearchSearchEngine.SearchResult>(); // 랭킹된 검색 결과
        private Rect searchResultsRect; // 직전 프레임의 드롭다운 영역 (캔버스 입력 차단용)
        private bool searchDropdownClosed; // 빈 곳 클릭으로 드롭다운만 닫은 상태 (쿼리 변경 시 재오픈)
        // searchSelIndex/searchScrollPos는 Results·ModComplete 두 모드가 공유 — RunSearch/RefreshModSuggestions가
        // 각자 목록 변경 시점에 리셋한다는 전제. 목록을 다른 경로로 바꾸면 리셋도 같이 옮길 것.
        private int searchSelIndex; // 키보드 선택 행
        private Vector2 searchScrollPos;
        private int searchDropdownRowCount; // 직전 프레임 드롭다운 행 수 (휠 pre-pass의 스크롤 클램프용)

        private const float SearchRowHeight = 26f;
        private const int SearchMaxVisibleRows = 8;

        /// <summary>검색창 아래 가상화 드롭다운 리스트. 보이는 행만 drawRow 호출. searchResultsRect 갱신.</summary>
        private void DrawDropdownList(Rect searchRect, int count, System.Action<Rect, int> drawRow)
        {
            int visRows = Mathf.Min(count, SearchMaxVisibleRows);
            float outH = visRows * SearchRowHeight + 8f;
            searchResultsRect = new Rect(searchRect.x, searchRect.yMax + 4f, searchRect.width, outH);
            searchDropdownRowCount = count;

            Widgets.DrawBoxSolid(searchResultsRect, Constraints.PanelBg);
            GUIDrawingUtilities.DrawBorderLines(searchResultsRect, Constraints.PanelBorder, 1f);

            Rect outRect = new Rect(searchResultsRect.x + 2f, searchResultsRect.y + 4f, searchResultsRect.width - 4f, outH - 8f);
            float rowW = outRect.width - (count > SearchMaxVisibleRows ? 16f : 0f);
            Rect viewRect = new Rect(0f, 0f, rowW, count * SearchRowHeight);

            Widgets.BeginScrollView(outRect, ref searchScrollPos, viewRect);
            int first = Mathf.Max(0, Mathf.FloorToInt(searchScrollPos.y / SearchRowHeight));
            int last = Mathf.Min(count - 1, Mathf.FloorToInt((searchScrollPos.y + outRect.height) / SearchRowHeight));
            for (int i = first; i <= last; i++)
            {
                drawRow(new Rect(0f, i * SearchRowHeight, rowW, SearchRowHeight), i);
            }
            Widgets.EndScrollView();
        }

        private void HandleSearchDropdownScroll()
        {
            if (Event.current.type != EventType.ScrollWheel) return;
            if (searchResultsRect.height <= 0f || !searchResultsRect.Contains(Event.current.mousePosition)) return;

            float maxY = Mathf.Max(0f, (searchDropdownRowCount - SearchMaxVisibleRows) * SearchRowHeight);
            searchScrollPos.y = Mathf.Clamp(searchScrollPos.y + Event.current.delta.y * SearchRowHeight, 0f, maxY);
            Event.current.Use();
        }

        /// <summary>쿼리로 검색을 재실행하고 표시 상태를 리셋한다. 빈 쿼리면 결과만 비움.</summary>
        private void RunSearch()
        {
            var results = GraphBuildPipeline.SearchEngine.Search(searchEffective);
            matchedDefs.Clear();
            searchResults.Clear();
            searchSelIndex = 0;
            searchScrollPos = Vector2.zero;
            searchDropdownClosed = false;
            foreach (var res in results)
            {
                if (res.Node.IsHidden) continue; // 미발견 연구 제외 (바닐라 동일)
                matchedDefs.Add(res.Node.Def);
                searchResults.Add(res); // 엔진이 이미 점수순 정렬
            }
        }

        private void DoSearchBox(Rect searchRect)
        {
            Widgets.DrawBoxSolid(searchRect, new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(searchRect, Constraints.PanelBorder, 1f);

            Rect searchIconRect = new Rect(searchRect.x + 4, searchRect.y + 4, 20, 20);
            GUI.DrawTexture(searchIconRect, TexButton.Search);

            HandleSearchKeys(CurrentDropdownMode());

            Rect textRect = new Rect(searchRect.x + 28, searchRect.y + 2, searchRect.width - 50, 24);
            GUI.SetNextControlName("YARTSearchField");
            searchQuery = Widgets.TextField(textRect, searchQuery);

            // IME 조합 중인 글자(아직 커밋 안 됨)도 즉시 검색에 반영 — 한글 "펨" 등 조합형 입력 대응
            string composing = GUI.GetNameOfFocusedControl() == "YARTSearchField" ? Input.compositionString : "";
            string effective = string.IsNullOrEmpty(composing) ? searchQuery : searchQuery + composing;
            if (effective != searchEffective)
            {
                searchEffective = effective;
                RunSearch();
            }

            if (!string.IsNullOrEmpty(searchQuery))
            {
                Rect clearRect = new Rect(searchRect.xMax - 24, searchRect.y + 2, 24, 24);
                if (Widgets.ButtonInvisible(clearRect))
                {
                    searchQuery = "";
                    searchEffective = "";
                    SyncSearchFieldEditor();
                    RunSearch();
                }
                using (Temporary.Font(GameFont.Small))
                using (Temporary.Anchor(TextAnchor.MiddleCenter))
                using (Temporary.Color(Color.gray))
                {
                    Widgets.Label(clearRect, "x");
                }
            }
            else if (GUI.GetNameOfFocusedControl() != "YARTSearchField")
            {
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                using (Temporary.Color(Color.gray))
                {
                    Widgets.Label(textRect, "YART_SearchPlaceholder".Translate());
                }
            }

            searchResultsRect = Rect.zero;
            searchDropdownRowCount = 0;
            switch (CurrentDropdownMode())
            {
                case SearchDropdownMode.Help: DrawSearchHelp(searchRect); break;
                case SearchDropdownMode.ModComplete: DrawModSuggestions(searchRect); break;
                case SearchDropdownMode.Results: DrawSearchResultsDropdown(searchRect); break;
            }
        }

        private void DrawSearchResultsDropdown(Rect searchRect)
        {
            if (searchDropdownClosed || string.IsNullOrEmpty(searchEffective) || searchResults.Count == 0) return;
            DrawDropdownList(searchRect, searchResults.Count, DrawSearchResultRow);
        }

        private void DrawSearchResultRow(Rect rowRect, int i)
        {
            var result = searchResults[i];
            var resNode = result.Node;

            if (i == searchSelIndex) Widgets.DrawHighlightSelected(rowRect);
            else if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

            // 시대 색 점
            Rect dotRect = new Rect(rowRect.x + 6f, rowRect.y + rowRect.height / 2f - 3f, 6f, 6f);
            Widgets.DrawBoxSolid(dotRect, resNode.EraAccentColor);

            // 우측 보조 텍스트: 소속 그래프 (교차 그래프 결과만 — 클릭 시 그래프 전환됨을 암시)
            string side = null;
            if (!resNode.Key.Equals(CurrentKey))
            {
                // IsUnified 키는 Tab=null이므로 반드시 먼저 확인
                side = resNode.Key.IsUnified ? (string)"YART_AllTabs".Translate()
                    : resNode.Key.Channel.IsBench
                        ? (string)resNode.Key.Tab.LabelCap
                        : resNode.Key.Channel.Label;
            }

            float sideWidth = side == null ? 0f : 96f;
            Rect labelRect = new Rect(rowRect.x + 18f, rowRect.y, rowRect.width - 22f - sideWidth, rowRect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(new Color(1f, 1f, 1f, SearchMatchAlpha(result.ReasonField))))
            {
                Widgets.Label(labelRect, resNode.Label.Truncate(labelRect.width));
            }
            if (side != null)
            {
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleRight))
                using (Temporary.Color(new Color(0.5f, 0.55f, 0.65f)))
                {
                    Widgets.Label(new Rect(rowRect.x, rowRect.y, rowRect.width - 6f, rowRect.height), side.Truncate(sideWidth));
                }
            }

            if (Widgets.ButtonInvisible(rowRect))
            {
                searchSelIndex = i;
                // 통합 뷰 중이면 통합 복사본 우선, 없으면 원래 노드(JumpToNode가 그래프 전환 처리)
                var jumpNode = GetNodeOnCurrentGraph(resNode.Def) ?? resNode;
                JumpToNode(jumpNode);
            }
        }

        private static float SearchMatchAlpha(byte reasonField)
        {
            switch (reasonField)
            {
                case ResearchSearchEngine.FieldMainLabel:
                case ResearchSearchEngine.FieldMainDefName:
                    return 1f;
                case ResearchSearchEngine.FieldUnlockLabel:
                case ResearchSearchEngine.FieldUnlockDefName:
                    return 0.8f;
                case ResearchSearchEngine.FieldModName:
                    return 0.65f;
                case ResearchSearchEngine.FieldMainDesc:
                case ResearchSearchEngine.FieldUnlockDesc:
                    return 0.5f;
                default:
                    return 1f; // FieldNone (@ 전용 쿼리 등)
            }
        }

        private SearchDropdownMode CurrentDropdownMode()
        {
            bool focused = GUI.GetNameOfFocusedControl() == "YARTSearchField";
            if (focused)
            {
                string atToken = CurrentAtToken();
                if (atToken != null)
                {
                    RefreshModSuggestions(atToken);
                    if (modSuggestions.Count > 0) return SearchDropdownMode.ModComplete;
                }
                else if (string.IsNullOrEmpty(searchEffective))
                {
                    return SearchDropdownMode.Help;
                }
            }
            if (!searchDropdownClosed && !string.IsNullOrEmpty(searchEffective) && searchResults.Count > 0)
                return SearchDropdownMode.Results;
            return SearchDropdownMode.None;
        }

        /// <summary>입력 중인 마지막 토큰이 "@…"이면 @ 뒤 부분(소문자), 아니면 null.</summary>
        private string CurrentAtToken()
        {
            if (string.IsNullOrEmpty(searchQuery) || searchQuery.EndsWith(" ")) return null;
            int lastSpace = searchQuery.LastIndexOf(' ');
            string last = searchQuery.Substring(lastSpace + 1);
            return last.Length >= 1 && last[0] == '@' ? last.Substring(1).ToLowerInvariant() : null;
        }

        private void RefreshModSuggestions(string partial)
        {
            if (partial == modSuggestPartial) return;
            modSuggestPartial = partial;
            modSuggestions.Clear();
            foreach (var entry in GraphBuildPipeline.SearchEngine.KnownMods)
            {
                if (partial.Length == 0
                    || entry.name.ToLowerInvariant().Contains(partial)
                    || entry.packageId.ToLowerInvariant().Contains(partial))
                {
                    modSuggestions.Add(entry);
                }
            }
            modSuggestions.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            searchSelIndex = 0;
            searchScrollPos = Vector2.zero;
        }

        /// <summary>마지막 @토큰을 "@packageId "로 치환. 모드명은 공백 포함이라 packageId를 삽입.</summary>
        private void ApplyModCompletion(string packageId)
        {
            int lastSpace = searchQuery.LastIndexOf(' ');
            string head = lastSpace < 0 ? "" : searchQuery.Substring(0, lastSpace + 1);
            searchQuery = head + "@" + packageId.ToLowerInvariant() + " ";
            searchEffective = searchQuery;
            GUI.FocusControl("YARTSearchField"); // 자동완성 선택 후에도 계속 입력 가능하게 포커스 유지
            SyncSearchFieldEditor();
            RunSearch();
        }

        private void DrawSearchHelp(Rect searchRect)
        {
            const float rowH = 22f;
            float h = SearchSyntaxHints.Length * rowH + 8f;
            searchResultsRect = new Rect(searchRect.x, searchRect.yMax + 4f, searchRect.width, h);
            Widgets.DrawBoxSolid(searchResultsRect, Constraints.PanelBg);
            GUIDrawingUtilities.DrawBorderLines(searchResultsRect, Constraints.PanelBorder, 1f);

            float y = searchResultsRect.y + 4f;
            foreach (var hint in SearchSyntaxHints)
            {
                using (Temporary.Font(GameFont.Tiny))
                {
                    using (Temporary.Color(new Color(0.8f, 0.85f, 0.95f)))
                    {
                        Widgets.Label(new Rect(searchResultsRect.x + 8f, y, 80f, rowH), hint.syntaxKey.Translate());
                    }
                    using (Temporary.Color(new Color(0.5f, 0.55f, 0.65f)))
                    {
                        Widgets.Label(new Rect(searchResultsRect.x + 92f, y, searchResultsRect.width - 100f, rowH), hint.descKey.Translate());
                    }
                }
                y += rowH;
            }
        }

        private void DrawModSuggestions(Rect searchRect)
        {
            DrawDropdownList(searchRect, modSuggestions.Count, DrawModSuggestionRow);
        }

        private void DrawModSuggestionRow(Rect rowRect, int i)
        {
            var entry = modSuggestions[i];
            if (i == searchSelIndex) Widgets.DrawHighlightSelected(rowRect);
            else if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            {
                Widgets.Label(new Rect(rowRect.x + 8f, rowRect.y, rowRect.width - 128f, rowRect.height),
                    entry.name.Truncate(rowRect.width - 128f));
            }
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleRight))
            using (Temporary.Color(new Color(0.5f, 0.55f, 0.65f)))
            {
                Widgets.Label(new Rect(rowRect.x, rowRect.y, rowRect.width - 6f, rowRect.height),
                    entry.packageId.Truncate(120f));
            }
            if (Widgets.ButtonInvisible(rowRect))
            {
                searchSelIndex = i;
                ApplyModCompletion(entry.packageId);
            }
        }

        private void HandleSearchKeys(SearchDropdownMode mode)
        {
            if (Event.current.type != EventType.KeyDown) return;
            if (GUI.GetNameOfFocusedControl() != "YARTSearchField") return;

            var key = Event.current.keyCode;
            int count = mode == SearchDropdownMode.Results ? searchResults.Count
                      : mode == SearchDropdownMode.ModComplete ? modSuggestions.Count : 0;
            if (count == 0) return;

            if (key == KeyCode.DownArrow || key == KeyCode.UpArrow)
            {
                searchSelIndex = Mathf.Clamp(searchSelIndex + (key == KeyCode.DownArrow ? 1 : -1), 0, count - 1);
                EnsureSearchRowVisible(searchSelIndex, count);
                Event.current.Use();
            }
            else if (key == KeyCode.Return || key == KeyCode.KeypadEnter)
            {
                int sel = Mathf.Clamp(searchSelIndex, 0, count - 1);
                if (mode == SearchDropdownMode.Results)
                {
                    var selNode = searchResults[sel].Node;
                    var jumpNode = GetNodeOnCurrentGraph(selNode.Def) ?? selNode;
                    JumpToNode(jumpNode);
                }
                else ApplyModCompletion(modSuggestions[sel].packageId);
                Event.current.Use();
            }
        }

        private void EnsureSearchRowVisible(int row, int count)
        {
            if (count <= SearchMaxVisibleRows) { searchScrollPos.y = 0f; return; }
            float viewH = SearchMaxVisibleRows * SearchRowHeight;
            float top = row * SearchRowHeight;
            if (top < searchScrollPos.y) searchScrollPos.y = top;
            else if (top + SearchRowHeight > searchScrollPos.y + viewH) searchScrollPos.y = top + SearchRowHeight - viewH;
        }

        private void SyncSearchFieldEditor()
        {
            if (GUI.GetNameOfFocusedControl() != "YARTSearchField") return;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = searchQuery;
            editor.MoveTextEnd();
        }

        public override void OnCancelKeyPressed()
        {
            if (GUI.GetNameOfFocusedControl() == "YARTSearchField")
            {
                if (!string.IsNullOrEmpty(searchQuery))
                {
                    searchQuery = "";
                    searchEffective = "";
                    SyncSearchFieldEditor();
                    RunSearch();
                }
                else
                {
                    GUI.FocusControl(null);
                }
                Event.current.Use();
                return;
            }
            base.OnCancelKeyPressed();
        }

        public override void OnAcceptKeyPressed()
        {
            if (GUI.GetNameOfFocusedControl() == "YARTSearchField")
            {
                var mode = CurrentDropdownMode();
                if (mode == SearchDropdownMode.Results && searchResults.Count > 0)
                {
                    int sel = Mathf.Clamp(searchSelIndex, 0, searchResults.Count - 1);
                    var selNode = searchResults[sel].Node;
                    JumpToNode(GetNodeOnCurrentGraph(selNode.Def) ?? selNode);
                }
                else if (mode == SearchDropdownMode.ModComplete && modSuggestions.Count > 0)
                {
                    int sel = Mathf.Clamp(searchSelIndex, 0, modSuggestions.Count - 1);
                    ApplyModCompletion(modSuggestions[sel].packageId);
                }
                Event.current.Use(); // 드롭다운이 없어도 검색창 포커스 중 Enter로 창이 닫히지 않게 소비
                return;
            }
            base.OnAcceptKeyPressed();
        }
    }
}
