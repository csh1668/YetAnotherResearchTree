using System;
using System.Collections.Generic;
using System.Text;
using Verse;
using YART.Utils;

namespace YART.Data
{
    /// <summary>
    /// 연구 노드 전문 검색 (suffix array 기반).
    /// 코퍼스: 노드별 "label|defName|description|" (본 연구 + 해금 def들) + "모드명|패키지ID|" + '\0'.
    /// 위치→노드/필드 lookup 배열로 occurrence당 O(1) 판별, 필드 가중치로 랭킹.
    /// Build는 백그라운드 스레드(GraphBuildPipeline), Search는 UI 스레드에서 호출됨.
    /// </summary>
    public class ResearchSearchEngine
    {
        // ── 필드 클래스 (랭킹 가중치 내림차순) ──
        public const byte FieldMainLabel = 0;
        public const byte FieldMainDefName = 1;
        public const byte FieldUnlockLabel = 2;
        public const byte FieldUnlockDefName = 3;
        public const byte FieldModName = 4;
        public const byte FieldMainDesc = 5;
        public const byte FieldUnlockDesc = 6;
        public const byte FieldNone = 255; // 구분자('|','\0') 위치

        // 인접 등급 간 차이(≥10) > MatchBonus 최대(9) — 필드 우선순위가 항상 보너스를 지배.
        private static readonly int[] FieldWeights = { 100, 80, 60, 50, 30, 20, 10 };

        public struct SearchResult
        {
            public ResearchNode Node;
            public int Score;
            public byte ReasonField;   // 가장 약한 토큰이 매치된 필드 (FieldNone = 이유 없음)
            public string ReasonLabel; // 해금 def 라벨 / 모드명 (본 연구 필드면 null)
        }

        private struct Segment
        {
            public int Start;
            public string Label; // null = 본 연구 def의 필드
        }

        private SuffixStructure _suffix;
        private string _text;
        private readonly List<ResearchNode> _nodes = new List<ResearchNode>();
        private ushort[] _posNode;
        private byte[] _posField;
        private readonly List<Segment[]> _segments = new List<Segment[]>();
        private readonly List<(string name, string packageId)> _knownMods = new List<(string, string)>();

        // 검색 스크래치 (노드 수 크기, stamp 세대로 재사용 — 검색마다 할당 없음)
        private int[] _candStamp;
        private int[] _tokenStamp;
        private int[] _accumScore;
        private int[] _tokenBestScore;
        private int[] _tokenBestPos;
        private int[] _weakScore;
        private int[] _weakPos;
        private int _generation;

        private volatile bool _isBuilt; // 백그라운드 빌드 완료 게이트
        public bool IsBuilt => _isBuilt;

        /// <summary>@ 자동완성용: 인덱스에 등장한 모든 모드. IsBuilt == true 이후에만 읽을 것 (빌드 중 변경됨).</summary>
        public IReadOnlyList<(string name, string packageId)> KnownMods => _knownMods;

        /// <summary>
        /// 비영어 언어용 원문 맵: DefInjected 번역이 def 필드를 덮어쓸 때 원문(영어)이
        /// DefInjection.replacedString에 보관됨 (디컴파일 확인: DefInjectionPackage.cs).
        /// 키 = 주입 경로("defName.label" 등). 활성 언어가 영어면 빈 맵.
        /// 서로 다른 def 타입 간 경로 충돌은 마지막 값이 이김 — 검색 인덱싱 용도로는 무해.
        /// </summary>
        public static Dictionary<string, string> BuildOriginalStringMap()
        {
            var map = new Dictionary<string, string>();
            var lang = LanguageDatabase.activeLanguage;
            if (lang == null || lang == LanguageDatabase.defaultLanguage) return map;
            foreach (var package in lang.defInjections)
            {
                foreach (var kvp in package.injections)
                {
                    var inj = kvp.Value;
                    if (!inj.injected || inj.IsFullListInjection) continue;
                    if (string.IsNullOrEmpty(inj.replacedString)) continue;
                    map[kvp.Key] = inj.replacedString;
                }
            }
            return map;
        }

        /// <summary>
        /// 인덱스를 미빌드 상태로 되돌린다 — 그래프 재빌드(RebuildNow) 후 노드 인스턴스가
        /// 새로 만들어지면 다음 Build가 강제로 새 인덱스를 짓게 한다 (Build는 _isBuilt면 no-op).
        /// </summary>
        public void Invalidate()
        {
            _isBuilt = false;
            SearchNormalizer.Reset(); // 언어 변경 가능성 — 다음 빌드에서 정규화 모드 재감지
        }

        public void Build(IEnumerable<ResearchNode> researchNodes)
        {
            if (_isBuilt) return;

            var sb = new StringBuilder(1 << 18);
            var spans = new List<(int start, int len, byte field)>();
            var nodeEnds = new List<int>();
            var segs = new List<Segment>();
            var seenModIds = new HashSet<string>();

            _nodes.Clear();
            _segments.Clear();
            _knownMods.Clear();

            void AppendField(string raw, byte field)
            {
                if (!string.IsNullOrEmpty(raw))
                {
                    string s = SearchNormalizer.Normalize(raw); // 다국어 정규화 (쿼리와 동일하게 적용)
                    if (!string.IsNullOrEmpty(s))
                    {
                        spans.Add((sb.Length, s.Length, field));
                        sb.Append(s);
                    }
                }
                sb.Append('|');
            }

            var originals = BuildOriginalStringMap();

            void AddDef(Def def, bool isMain, HashSet<ModContentPack> mods)
            {
                segs.Add(new Segment { Start = sb.Length, Label = isMain ? null : def.label });
                AppendField(def.label, isMain ? FieldMainLabel : FieldUnlockLabel);
                AppendField(def.defName, isMain ? FieldMainDefName : FieldUnlockDefName);
                AppendField(def.description, isMain ? FieldMainDesc : FieldUnlockDesc);

                // 비영어 언어: 번역 전 원문(영어)도 같은 필드 클래스로 인덱싱 — 영어 검색 지원
                if (originals.Count > 0)
                {
                    if (originals.TryGetValue(def.defName + ".label", out string origLabel) && origLabel != def.label)
                    {
                        AppendField(origLabel, isMain ? FieldMainLabel : FieldUnlockLabel);
                    }
                    if (originals.TryGetValue(def.defName + ".description", out string origDesc) && origDesc != def.description)
                    {
                        AppendField(origDesc, isMain ? FieldMainDesc : FieldUnlockDesc);
                    }
                }
                if (def.modContentPack != null) mods.Add(def.modContentPack);
            }

            foreach (var node in researchNodes)
            {
                if (node.IsDummy || node.IsProxy) continue;
                if (node.Def == null) continue;
                if (_nodes.Count >= ushort.MaxValue)
                {
                    Log.Warning("[YART] Search index: node count exceeds 65535, remainder skipped.");
                    break;
                }

                _nodes.Add(node);
                segs.Clear();

                var mods = new HashSet<ModContentPack>();
                AddDef(node.Def, isMain: true, mods);
                // YART 소유 스냅샷(node.UnlockedDefs)을 읽는다. node.Def.UnlockedDefs(패치 가능한 vanilla
                // getter)를 백그라운드에서 직접 읽으면 VFE Tribals 등이 그 안에서 designator 텍스처를 워커
                // 스레드에서 로드해 위반이 난다. 캐시는 GraphBuildPipeline.WarmUnlockedDefsCache()가 메인
                // 스레드에서 미리 채워두므로, 여기서의 접근은 안전한 cache-hit이다.
                foreach (var unlocked in node.UnlockedDefs)
                {
                    if (unlocked == null) continue;
                    AddDef(unlocked, isMain: false, mods);
                }

                foreach (var mod in mods)
                {
                    segs.Add(new Segment { Start = sb.Length, Label = mod.Name });
                    AppendField(mod.Name, FieldModName);
                    AppendField(mod.PackageId, FieldModName);
                }

                // @ 필터는 SourceMod(연구 def의 모드)만 검사하므로, 자동완성 후보도 그 기준으로만 수집
                // (해금 def의 모드는 코퍼스에는 들어가지만 @로는 매치 불가 → 후보에 넣으면 죽은 제안이 됨)
                var mainMod = node.Def.modContentPack;
                if (mainMod != null && seenModIds.Add(mainMod.PackageId))
                {
                    _knownMods.Add((mainMod.Name, mainMod.PackageId));
                }

                sb.Append('\0'); // 노드 경계 — 쿼리에 못 들어가는 문자라 노드 간 매치 차단
                nodeEnds.Add(sb.Length);
                _segments.Add(segs.ToArray());
            }

            _text = sb.ToString();

            _posNode = new ushort[_text.Length];
            _posField = new byte[_text.Length];
            for (int i = 0; i < _posField.Length; i++) _posField[i] = FieldNone;

            int nodeIdx = 0;
            for (int i = 0; i < _text.Length; i++)
            {
                _posNode[i] = (ushort)nodeIdx;
                if (i + 1 == nodeEnds[nodeIdx]) nodeIdx++;
            }
            foreach (var (start, len, field) in spans)
            {
                for (int i = start; i < start + len; i++) _posField[i] = field;
            }

            _suffix = new SuffixStructure(_text);

            int nc = _nodes.Count;
            _candStamp = new int[nc];
            _tokenStamp = new int[nc];
            _accumScore = new int[nc];
            _tokenBestScore = new int[nc];
            _tokenBestPos = new int[nc];
            _weakScore = new int[nc];
            _weakPos = new int[nc];
            _generation = 0;

            _isBuilt = true;
            if (Prefs.DevMode) Log.Message($"[YART] Search index built: {nc} nodes, {_text.Length} chars.");
        }

        /// <summary>점수 내림차순으로 정렬된 결과. 빈/미빌드 시 빈 리스트.</summary>
        public List<SearchResult> Search(string query)
        {
            var results = new List<SearchResult>();
            if (!_isBuilt || string.IsNullOrWhiteSpace(query)) return results;

            var parts = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var textTokens = new List<string>();
            var modTokens = new List<string>();
            foreach (var part in parts)
            {
                if (part[0] == '@')
                {
                    if (part.Length > 1)
                    {
                        string m = SearchNormalizer.Normalize(part.Substring(1));
                        if (!string.IsNullOrEmpty(m)) modTokens.Add(m);
                    }
                    // 단독 "@"는 무시 (자동완성 입력 중 상태)
                }
                else
                {
                    string norm = SearchNormalizer.Normalize(part); // 코퍼스와 동일 정규화
                    if (string.IsNullOrEmpty(norm)) continue;
                    // '|'는 코퍼스 필드 구분자 — 포함 토큰은 어떤 단일 필드와도 매치 불가 (교차 매치 방지)
                    if (norm.IndexOf('|') >= 0) return results;
                    textTokens.Add(norm);
                }
            }

            var candidates = new List<int>();
            int gen = ++_generation;

            if (textTokens.Count == 0)
            {
                if (modTokens.Count == 0) return results;
                for (int v = 0; v < _nodes.Count; v++)
                {
                    candidates.Add(v);
                    _accumScore[v] = 0;
                    _weakPos[v] = -1;
                }
            }
            else
            {
                for (int t = 0; t < textTokens.Count; t++)
                {
                    string token = textTokens[t];
                    int tgen = ++_generation;

                    int occCount = _suffix.FindOccurrences(token, out int occStart);
                    for (int oi = 0; oi < occCount; oi++)
                    {
                        int pos = _suffix.PositionAt(occStart + oi);
                        byte field = _posField[pos];
                        if (field == FieldNone) continue;
                        int v = _posNode[pos];
                        if (t > 0 && _candStamp[v] != gen) continue; // 이전 토큰에서 탈락

                        int score = FieldWeights[field] + MatchBonus(pos, token.Length);
                        if (_tokenStamp[v] != tgen)
                        {
                            _tokenStamp[v] = tgen;
                            _tokenBestScore[v] = score;
                            _tokenBestPos[v] = pos;
                            if (t == 0)
                            {
                                _candStamp[v] = gen;
                                candidates.Add(v);
                                _accumScore[v] = 0;
                                _weakScore[v] = int.MaxValue;
                                _weakPos[v] = -1;
                            }
                        }
                        else if (score > _tokenBestScore[v])
                        {
                            _tokenBestScore[v] = score;
                            _tokenBestPos[v] = pos;
                        }
                    }

                    // 이번 토큰 AND 적용 + 누적/최약 토큰 갱신
                    int w = 0;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int v = candidates[i];
                        if (_tokenStamp[v] != tgen)
                        {
                            _candStamp[v] = 0; // 탈락 — 이후 토큰의 occurrence 스킵용
                            continue;
                        }
                        _accumScore[v] += _tokenBestScore[v];
                        if (_tokenBestScore[v] < _weakScore[v])
                        {
                            _weakScore[v] = _tokenBestScore[v];
                            _weakPos[v] = _tokenBestPos[v];
                        }
                        candidates[w++] = v;
                    }
                    candidates.RemoveRange(w, candidates.Count - w);
                    if (candidates.Count == 0) return results;
                }
            }

            // @ 모드 필터 — SourceMod의 name/packageId에 모든 필터 contains (AND, 기존 의미 유지)
            if (modTokens.Count > 0)
            {
                int w = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int v = candidates[i];
                    var mod = _nodes[v].SourceMod;
                    bool keep = mod != null;
                    if (keep)
                    {
                        string name = SearchNormalizer.Normalize(mod.Name);
                        string id = SearchNormalizer.Normalize(mod.PackageId);
                        foreach (var m in modTokens)
                        {
                            if (!name.Contains(m) && !id.Contains(m)) { keep = false; break; }
                        }
                    }
                    if (keep) candidates[w++] = v;
                }
                candidates.RemoveRange(w, candidates.Count - w);
            }

            foreach (int v in candidates)
            {
                var r = new SearchResult { Node = _nodes[v], Score = _accumScore[v], ReasonField = FieldNone };
                if (_weakPos[v] >= 0)
                {
                    r.ReasonField = _posField[_weakPos[v]];
                    r.ReasonLabel = SegmentLabelAt(v, _weakPos[v]);
                }
                results.Add(r);
            }

            results.Sort((a, b) =>
            {
                if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
                if (a.Score == 0) return string.CompareOrdinal(a.Node.Label, b.Node.Label); // @ 전용 쿼리 — 알파벳순
                int la = a.Node.Label != null ? a.Node.Label.Length : 0;
                int lb = b.Node.Label != null ? b.Node.Label.Length : 0;
                if (la != lb) return la.CompareTo(lb);
                return string.CompareOrdinal(a.Node.Label, b.Node.Label);
            });
            return results;
        }

        private int MatchBonus(int pos, int len)
        {
            char prev = pos > 0 ? _text[pos - 1] : '\0';
            bool fieldStart = prev == '|' || prev == '\0';
            char next = pos + len < _text.Length ? _text[pos + len] : '\0';
            bool fieldEnd = next == '|' || next == '\0';
            if (fieldStart && fieldEnd) return 9; // 필드 전체 일치
            if (fieldStart) return 6;             // 필드 접두 일치
            if (prev == ' ') return 3;            // 단어 시작 일치
            return 0;
        }

        /// <summary>pos가 속한 세그먼트의 라벨 (해금 def 라벨/모드명, 본 연구 필드면 null). 최종 결과에만 호출.</summary>
        private string SegmentLabelAt(int nodeIdx, int pos)
        {
            var segs = _segments[nodeIdx];
            string label = null;
            for (int i = 0; i < segs.Length; i++)
            {
                if (segs[i].Start > pos) break;
                label = segs[i].Label;
            }
            return label;
        }
    }
}
