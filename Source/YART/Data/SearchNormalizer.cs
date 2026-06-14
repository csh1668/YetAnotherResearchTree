using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Verse;

namespace YART.Data
{
    /// <summary>
    /// 검색용 문자열 정규화. 코퍼스 색인과 쿼리에 "동일하게" 적용해야 suffix-array 부분문자열 매칭이
    /// 다국어를 지원한다 (docs/SEARCH_I18N_PLAN.md). 스크립트별 전략:
    ///   · ASCII/라틴 : 소문자 + 결합표시(악센트) 제거 (프랑스어·베트남어 등). ß→ss, đ→d 등 특수.
    ///   · 한글       : 음절 → 호환 자모 분해 ("남"→ㄴㅏㅁ 이 "나무"→ㄴㅏㅁㅜ 의 접두가 됨).
    ///   · 가나       : 히라가나/가타카나 → 헵번 로마자 ("デンキ"→"denki").
    ///   · 한자(CJK)  : 활성 언어가 중국어면 병음, 일본어면 음독(로마자). 둘 다 라틴으로 수렴.
    /// 한자 테이블은 Unihan 베이크(Resources/cjk_readings.txt.gz)를 언어로 게이트해 지연 로드.
    /// 출력에 '|'(필드 구분자)·'\0'(노드 경계)를 절대 생성하지 않는다.
    /// </summary>
    public static class SearchNormalizer
    {
        private static bool _inited;
        private static Dictionary<int, string> _cjk; // codepoint → 로마자(병음 또는 음독). null이면 한자 무변환.
        private static readonly object _lock = new object();

        /// <summary>그래프 재빌드 시 호출 — 언어 변경 가능성에 대비해 다음 Normalize에서 재초기화.</summary>
        public static void Reset()
        {
            lock (_lock) { _inited = false; _cjk = null; }
        }

        private static void EnsureInit()
        {
            if (_inited) return;
            lock (_lock)
            {
                if (_inited) return;
                _cjk = null;
                var lang = LanguageDatabase.activeLanguage;
                string folder = (lang?.folderName ?? "").ToLowerInvariant();
                bool zh = folder.Contains("chinese");
                bool ja = folder.Contains("japanese");
                if (zh || ja) _cjk = LoadCjkTable(zh ? 1 : 2); // 1=pinyin, 2=on
                _inited = true;
            }
        }

        private static Dictionary<int, string> LoadCjkTable(int col)
        {
            var dict = new Dictionary<int, string>(1 << 15);
            try
            {
                var asm = typeof(SearchNormalizer).Assembly;
                string resName = null;
                foreach (var n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("cjk_readings.txt.gz", StringComparison.Ordinal)) { resName = n; break; }
                }
                if (resName == null) { Log.Warning("[YART] CJK readings resource not found — pinyin/on search disabled."); return dict; }

                using (var raw = asm.GetManifestResourceStream(resName))
                using (var gz = new GZipStream(raw, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        int t1 = line.IndexOf('\t');
                        if (t1 < 0) continue;
                        int t2 = line.IndexOf('\t', t1 + 1);
                        if (t2 < 0) continue;
                        string val = col == 1 ? line.Substring(t1 + 1, t2 - t1 - 1) : line.Substring(t2 + 1);
                        if (val.Length == 0 || val == "-") continue;
                        dict[Convert.ToInt32(line.Substring(0, t1), 16)] = val;
                    }
                }
                if (Prefs.DevMode) Log.Message($"[YART] CJK readings loaded: {dict.Count} ({(col == 1 ? "pinyin" : "on")}).");
            }
            catch (Exception e) { Log.Warning("[YART] CJK table load failed: " + e); }
            return dict;
        }

        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            EnsureInit();

            var sb = new StringBuilder(raw.Length + 8);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                if (c < 0x80) { sb.Append(char.ToLowerInvariant(c)); continue; } // ASCII 빠른 경로

                if (c >= 0xAC00 && c <= 0xD7A3) { AppendHangulJamo(sb, c); continue; } // 한글 음절 → 자모

                if (ToHiragana(c) != '\0' || c == (char)0x30FC) { i = AppendKanaRun(sb, raw, i); continue; } // 가나 → 로마자

                if (_cjk != null) // 한자 → 병음/음독
                {
                    int cp = c;
                    bool surrogate = false;
                    if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                    { cp = char.ConvertToUtf32(c, raw[i + 1]); surrogate = true; }
                    if (IsCjkIdeograph(cp))
                    {
                        if (surrogate) i++;
                        if (_cjk.TryGetValue(cp, out var rom)) sb.Append(rom);
                        else sb.Append(char.ConvertFromUtf32(cp)); // 매핑 없으면 원문자 보존
                        continue;
                    }
                }

                if (TryAppendSpecial(c, sb)) continue; // ß→ss, đ→d 등

                // 라틴 결합표시 제거 + 소문자 (악센트 무시)
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                foreach (char d in c.ToString().Normalize(NormalizationForm.FormD))
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark) continue;
                    sb.Append(char.ToLowerInvariant(d));
                }
            }
            return sb.ToString();
        }

        // ── 한글 ──────────────────────────────────────────────────────────────
        // 호환 자모(U+31xx). 종성 ㅁ과 다음 글자 초성 ㅁ이 같은 문자가 되어야 "남"이 "나무"의 접두가 됨.
        private const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        private const string Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
        private const string Jong = "ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ"; // 인덱스 1..27

        private static void AppendHangulJamo(StringBuilder sb, char syllable)
        {
            int s = syllable - 0xAC00;
            sb.Append(Cho[s / 588]);
            sb.Append(Jung[(s % 588) / 28]);
            int jong = s % 28;
            if (jong > 0) sb.Append(Jong[jong - 1]);
        }

        // ── 가나 → 로마자 ─────────────────────────────────────────────────────
        private static readonly Dictionary<char, string> Kana = BuildKanaMap();

        private static char ToHiragana(char c)
        {
            if (c >= 0x3041 && c <= 0x3096) return c;             // 히라가나
            if (c >= 0x30A1 && c <= 0x30F6) return (char)(c - 0x60); // 가타카나 → 히라가나
            return '\0';
        }

        /// <summary>start의 가나 런을 로마자로 변환. 반환값 = 런의 마지막 인덱스(호출부 루프가 ++).</summary>
        private static int AppendKanaRun(StringBuilder sb, string raw, int start)
        {
            string pending = null; // 직전 음절 (요음/장음 결합 위해 보류)
            bool sokuon = false;   // っ — 다음 자음 중복
            int i = start;
            for (; i < raw.Length; i++)
            {
                char oc = raw[i];
                if (oc == (char)0x30FC) // ー 장음 → 직전 모음 반복
                {
                    if (pending != null) { sb.Append(pending); char v = LastVowel(pending); pending = null; if (v != '\0') sb.Append(v); }
                    else if (sb.Length > 0 && IsVowel(sb[sb.Length - 1])) sb.Append(sb[sb.Length - 1]);
                    continue;
                }
                char h = ToHiragana(oc);
                if (h == '\0') break; // 가나 아님 → 런 종료

                if (h == (char)0x3063) { if (pending != null) { sb.Append(pending); pending = null; } sokuon = true; continue; } // っ
                if (h == (char)0x3083 || h == (char)0x3085 || h == (char)0x3087) // ゃゅょ
                {
                    char vowel = h == (char)0x3083 ? 'a' : h == (char)0x3085 ? 'u' : 'o';
                    if (pending != null) pending = ApplyYoon(pending, vowel);
                    if (pending != null) { sb.Append(pending); pending = null; }
                    continue;
                }
                if (!Kana.TryGetValue(h, out string rom))
                {
                    if (pending != null) { sb.Append(pending); pending = null; }
                    sb.Append(oc); // 알 수 없는 가나 — 원문자 보존
                    continue;
                }
                if (pending != null) { sb.Append(pending); pending = null; }
                if (sokuon) { if (rom.Length > 0 && !IsVowel(rom[0]) && rom[0] != 'n') sb.Append(rom[0]); sokuon = false; }
                pending = rom;
            }
            if (pending != null) sb.Append(pending);
            return i - 1;
        }

        private static string ApplyYoon(string baseRom, char vowel)
        {
            if (baseRom.EndsWith("shi", StringComparison.Ordinal)) return "sh" + vowel;
            if (baseRom.EndsWith("chi", StringComparison.Ordinal)) return "ch" + vowel;
            if (baseRom.EndsWith("ji", StringComparison.Ordinal)) return "j" + vowel;
            if (baseRom.EndsWith("i", StringComparison.Ordinal)) return baseRom.Substring(0, baseRom.Length - 1) + "y" + vowel;
            return baseRom + vowel;
        }

        private static bool IsVowel(char c) => c == 'a' || c == 'i' || c == 'u' || c == 'e' || c == 'o';
        private static char LastVowel(string s)
        {
            for (int i = s.Length - 1; i >= 0; i--) if (IsVowel(s[i])) return s[i];
            return '\0';
        }

        private static Dictionary<char, string> BuildKanaMap()
        {
            var m = new Dictionary<char, string>(128);
            // 히라가나 키 → 헵번 로마자 (Unihan 음독 표기와 동일 계열: shi/chi/tsu/fu, 장음=모음 반복)
            string[] pairs =
            {
                "あ a","い i","う u","え e","お o",
                "か ka","き ki","く ku","け ke","こ ko",
                "が ga","ぎ gi","ぐ gu","げ ge","ご go",
                "さ sa","し shi","す su","せ se","そ so",
                "ざ za","じ ji","ず zu","ぜ ze","ぞ zo",
                "た ta","ち chi","つ tsu","て te","と to",
                "だ da","ぢ ji","づ zu","で de","ど do",
                "な na","に ni","ぬ nu","ね ne","の no",
                "は ha","ひ hi","ふ fu","へ he","ほ ho",
                "ば ba","び bi","ぶ bu","べ be","ぼ bo",
                "ぱ pa","ぴ pi","ぷ pu","ぺ pe","ぽ po",
                "ま ma","み mi","む mu","め me","も mo",
                "や ya","ゆ yu","よ yo",
                "ら ra","り ri","る ru","れ re","ろ ro",
                "わ wa","ゐ i","ゑ e","を o","ん n","ゔ vu",
                "ぁ a","ぃ i","ぅ u","ぇ e","ぉ o","ゕ ka","ゖ ke",
            };
            foreach (var p in pairs)
            {
                int sp = p.IndexOf(' ');
                m[p[0]] = p.Substring(sp + 1);
            }
            return m;
        }

        // ── 한자 / 특수 ───────────────────────────────────────────────────────
        private static bool IsCjkIdeograph(int cp)
        {
            return (cp >= 0x4E00 && cp <= 0x9FFF)   // CJK Unified
                || (cp >= 0x3400 && cp <= 0x4DBF)   // Ext A
                || (cp >= 0xF900 && cp <= 0xFAFF)   // Compatibility Ideographs
                || (cp >= 0x20000 && cp <= 0x2FA1F); // Ext B+ (surrogate)
        }

        private static bool TryAppendSpecial(char c, StringBuilder sb)
        {
            switch (c)
            {
                case 'ß': sb.Append("ss"); return true;
                case 'ẞ': sb.Append("ss"); return true;
                case 'đ': case 'Đ': sb.Append('d'); return true;
                case 'ø': case 'Ø': sb.Append('o'); return true;
                case 'ł': case 'Ł': sb.Append('l'); return true;
                case 'æ': case 'Æ': sb.Append("ae"); return true;
                case 'œ': case 'Œ': sb.Append("oe"); return true;
                case 'ı': sb.Append('i'); return true;   // 터키 점없는 i
                case 'İ': sb.Append('i'); return true;
                default: return false;
            }
        }
    }
}
