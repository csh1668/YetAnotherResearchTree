using System;
using System.Collections.Generic;

namespace YART.Utils
{
    /// <summary>
    /// Suffix array 기반 부분 문자열 검색.
    /// 빌드: prefix doubling + counting sort = O(n log n).
    /// Search: 패턴의 SA 구간 이진 탐색 O(m log n) 후 모든 occurrence 시작 위치 반환.
    /// </summary>
    public class SuffixStructure
    {
        private readonly string _text;
        private readonly int[] _sa;

        public SuffixStructure(string text)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _sa = BuildSuffixArray(text);
        }

        /// <summary>패턴 occurrence 수를 반환하고 SA 시작 인덱스를 start로 출력. 0이면 매치 없음.
        /// 핫패스용 — Search의 yield 이터레이터 오버헤드 회피 (PositionAt으로 위치 조회).</summary>
        public int FindOccurrences(string pattern, out int start)
        {
            start = 0;
            if (string.IsNullOrEmpty(pattern)) return 0;
            var (lo, hi) = FindRange(pattern);
            if (lo >= hi || !IsPrefixMatch(pattern, _sa[lo])) return 0;
            start = lo;
            return hi - lo;
        }

        /// <summary>SA 인덱스 → 텍스트 위치 (FindOccurrences의 [start, start+count) 범위로 호출).</summary>
        public int PositionAt(int saIndex) => _sa[saIndex];

        public IEnumerable<int> Search(string pattern)
        {
            int count = FindOccurrences(pattern, out int start);
            for (int i = 0; i < count; ++i)
            {
                yield return _sa[start + i];
            }
        }

        private bool IsPrefixMatch(string pattern, int saIndex)
        {
            int n = _text.Length;
            int m = pattern.Length;
            if (saIndex + m > n) return false;
            return string.Compare(_text, saIndex, pattern, 0, m, StringComparison.Ordinal) == 0;
        }

        private (int start, int end) FindRange(string pattern)
        {
            int n = _text.Length, m = pattern.Length;

            int loInc = 0, hiExc = n;
            while (loInc < hiExc)
            {
                int mid = (loInc + hiExc) >> 1;
                int s = _sa[mid];
                int cmp = string.Compare(_text, s, pattern, 0, Math.Min(n - s, m), StringComparison.Ordinal);
                if (cmp == 0 && n - s < m) cmp = -1; // 패턴보다 짧은 suffix는 접두 일치라도 패턴보다 작다 (ordinal)
                if (cmp < 0)
                    loInc = mid + 1;
                else
                    hiExc = mid;
            }

            int start = loInc;

            loInc = start;
            hiExc = n;
            while (loInc < hiExc)
            {
                int mid = (loInc + hiExc) >> 1;
                int s = _sa[mid];
                int cmp = string.Compare(_text, s, pattern, 0, Math.Min(n - s, m), StringComparison.Ordinal);
                if (cmp == 0 && n - s < m) cmp = -1; // 패턴보다 짧은 suffix는 접두 일치라도 패턴보다 작다 (ordinal)
                if (cmp <= 0)
                {
                    loInc = mid + 1;
                }
                else
                {
                    hiExc = mid;
                }
            }

            int end = loInc;

            return (start, end);
        }

        private static int[] BuildSuffixArray(string text)
        {
            int n = text.Length;
            var sa = new int[n];
            if (n == 0) return sa;

            var rank = new int[n];
            var tmp = new int[n];
            var sa2 = new int[n];
            // cnt 크기 n+1: rank 최대값 n-1 → 버킷 인덱스 rank+1이 최대 n. (+1 오프셋 안정 정렬 패턴)
            var cnt = new int[n + 1];

            // 초기: 단일 문자 기준 정렬. 문자값이 유니코드 전역이라 counting 불가 → 첫 패스만 비교 정렬.
            var firstKeys = new char[n];
            for (int i = 0; i < n; ++i) { sa[i] = i; firstKeys[i] = text[i]; }
            Array.Sort(firstKeys, sa);
            rank[sa[0]] = 0;
            for (int i = 1; i < n; ++i)
                rank[sa[i]] = rank[sa[i - 1]] + (text[sa[i]] != text[sa[i - 1]] ? 1 : 0);

            for (int k = 1; k < n; k <<= 1)
            {
                // 2차 키(rank[i+k], 범위 밖 = -1 = 최솟값) 오름차순으로 sa2 구성:
                // 2차 키가 비는 suffix(i+k >= n)가 가장 앞, 나머지는 직전 sa 순서 유지(안정성).
                int p = 0;
                for (int i = n - k; i < n; ++i) sa2[p++] = i;
                for (int i = 0; i < n; ++i)
                {
                    if (sa[i] >= k) sa2[p++] = sa[i] - k;
                }

                // 1차 키(rank) 기준 안정 counting sort
                Array.Clear(cnt, 0, cnt.Length);
                for (int i = 0; i < n; ++i) cnt[rank[i] + 1]++;
                for (int i = 1; i <= n; ++i) cnt[i] += cnt[i - 1];
                for (int i = 0; i < n; ++i) sa[cnt[rank[sa2[i]]]++] = sa2[i];

                tmp[sa[0]] = 0;
                for (int i = 1; i < n; ++i)
                {
                    int a = sa[i - 1], b = sa[i];
                    bool same = rank[a] == rank[b]
                        && (a + k < n ? rank[a + k] : -1) == (b + k < n ? rank[b + k] : -1);
                    tmp[b] = tmp[a] + (same ? 0 : 1);
                }
                Array.Copy(tmp, rank, n);
                if (rank[sa[n - 1]] == n - 1) break;
            }
            return sa;
        }
    }
}
