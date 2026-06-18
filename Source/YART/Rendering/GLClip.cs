using UnityEngine;

namespace YART.Rendering
{
    /// <summary>
    /// https://en.wikipedia.org/wiki/Sutherland%E2%80%93Hodgman_algorithm
    /// </summary>
    internal static class GLClip
    {
        public struct V
        {
            public Vector2 p;
            public Vector2 uv;
            public Color c;
        }

        private static readonly V[] bufA = new V[16];
        private static readonly V[] bufB = new V[16];

        private static V Lerp(in V a, in V b, float t) => new V
        {
            p = Vector2.LerpUnclamped(a.p, b.p, t),
            uv = Vector2.LerpUnclamped(a.uv, b.uv, t),
            c = Color.LerpUnclamped(a.c, b.c, t),
        };

        // edge: 0 = x>=xMin, 1 = x<=xMax, 2 = y>=yMin, 3 = y<=yMax
        private static bool Inside(in V v, int edge, Rect r)
        {
            switch (edge)
            {
                case 0: return v.p.x >= r.xMin;
                case 1: return v.p.x <= r.xMax;
                case 2: return v.p.y >= r.yMin;
                default: return v.p.y <= r.yMax;
            }
        }

        private static float IntersectT(in V a, in V b, int edge, Rect r)
        {
            switch (edge)
            {
                case 0: return (r.xMin - a.p.x) / (b.p.x - a.p.x);
                case 1: return (r.xMax - a.p.x) / (b.p.x - a.p.x);
                case 2: return (r.yMin - a.p.y) / (b.p.y - a.p.y);
                default: return (r.yMax - a.p.y) / (b.p.y - a.p.y);
            }
        }

        /// <summary>
        /// 볼록 다각형 poly[0..count)를 AA 사각형 clip으로 잘라낸다
        /// </summary>
        public static int ClipConvex(V[] poly, int count, Rect clip, out V[] result)
        {
            V[] src = bufA, dst = bufB;
            for (int i = 0; i < count; i++) src[i] = poly[i];
            int n = count;

            for (int edge = 0; edge < 4 && n > 0; edge++)
            {
                int m = 0;
                V prev = src[n - 1];
                bool prevIn = Inside(prev, edge, clip);
                for (int i = 0; i < n; i++)
                {
                    V cur = src[i];
                    bool curIn = Inside(cur, edge, clip);
                    if (curIn)
                    {
                        if (!prevIn) dst[m++] = Lerp(prev, cur, IntersectT(prev, cur, edge, clip));
                        dst[m++] = cur;
                    }
                    else if (prevIn)
                    {
                        dst[m++] = Lerp(prev, cur, IntersectT(prev, cur, edge, clip));
                    }
                    prev = cur;
                    prevIn = curIn;
                }
                n = m;
                (src, dst) = (dst, src);
            }

            result = src;
            return n;
        }
    }
}
