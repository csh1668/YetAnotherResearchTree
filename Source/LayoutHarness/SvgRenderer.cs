using System;
using System.Globalization;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using YART.Data;

namespace LayoutHarness
{
    internal static class SvgRenderer
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string N(float v) => v.ToString("0.#", Inv);

        public static string Render(ResearchSubGraph g)
        {
            float nw = Constraints.NodeSize.x, nh = Constraints.NodeSize.y;
            float halfReal = nh / 2f;

            // bbox
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var n in g.Nodes)
            {
                float h = n.IsDummy ? Constraints.LayoutDummyNodeHeight : nh;
                minX = Math.Min(minX, n.Position.x);
                maxX = Math.Max(maxX, n.Position.x + nw);
                minY = Math.Min(minY, n.Position.y - h / 2f);
                maxY = Math.Max(maxY, n.Position.y + h / 2f);
            }
            if (g.Nodes.Count == 0) { minX = minY = 0; maxX = maxY = 100; }
            float pad = 80f;
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;
            float w = maxX - minX, h2 = maxY - minY;

            var sb = new StringBuilder(256 * 1024);
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{N(minX)} {N(minY)} {N(w)} {N(h2)}\" ")
              .Append($"width=\"{N(Math.Min(w, 4000))}\" height=\"{N(Math.Min(h2, 4000))}\" font-family=\"sans-serif\">\n");
            sb.Append($"<rect x=\"{N(minX)}\" y=\"{N(minY)}\" width=\"{N(w)}\" height=\"{N(h2)}\" fill=\"#0b1c33\"/>\n");

            foreach (var kv in g.TechLevelBoundaries.OrderBy(k => k.Value))
            {
                sb.Append($"<line x1=\"{N(kv.Value)}\" y1=\"{N(minY)}\" x2=\"{N(kv.Value)}\" y2=\"{N(maxY)}\" stroke=\"#ffffff\" stroke-opacity=\"0.10\" stroke-width=\"2\"/>\n");
                sb.Append($"<text x=\"{N(kv.Value + 4)}\" y=\"{N(minY + 24)}\" fill=\"#ffffff\" fill-opacity=\"0.35\" font-size=\"20\">{Esc(kv.Key.ToString())}</text>\n");
            }

            sb.Append("<g stroke=\"#5a6b88\" stroke-opacity=\"0.55\" stroke-width=\"1.5\" fill=\"none\">\n");
            foreach (var e in g.Edges)
            {
                float x1 = e.From.Position.x + nw, y1 = e.From.Position.y;
                float x2 = e.To.Position.x, y2 = e.To.Position.y;
                float hx = (x2 - x1) * 0.45f;
                sb.Append($"<path d=\"M {N(x1)} {N(y1)} C {N(x1 + hx)} {N(y1)} {N(x2 - hx)} {N(y2)} {N(x2)} {N(y2)}\"/>\n");
            }
            foreach (var n in g.Nodes)
                if (n.IsDummy)
                    sb.Append($"<path d=\"M {N(n.Position.x)} {N(n.Position.y)} L {N(n.Position.x + nw)} {N(n.Position.y)}\"/>\n");
            sb.Append("</g>\n");
            
            sb.Append("<g fill=\"#ffd24d\" fill-opacity=\"0.5\">\n");
            foreach (var n in g.Nodes.Where(n => n.IsDummy))
                sb.Append($"<circle cx=\"{N(n.Position.x + nw / 2f)}\" cy=\"{N(n.Position.y)}\" r=\"2.5\"/>\n");
            sb.Append("</g>\n");

            foreach (var n in g.Nodes.Where(n => !n.IsDummy))
            {
                float x = n.Position.x, y = n.Position.y - halfReal;
                // 그룹키가 있으면 그룹별 색(정렬 가시화), 없으면 시대색 폴백
                var c = !string.IsNullOrEmpty(n.GroupKey) ? GroupColor(n.GroupKey) : EraColor(n.TechLevel);
                string hex = Hex(c);
                if (n.IsProxy)
                {
                    sb.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(nw)}\" height=\"{N(nh)}\" rx=\"6\" ")
                      .Append($"fill=\"{hex}\" fill-opacity=\"0.10\" stroke=\"{hex}\" stroke-opacity=\"0.8\" stroke-width=\"2\" stroke-dasharray=\"6 4\"/>\n");
                }
                else
                {
                    sb.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(nw)}\" height=\"{N(nh)}\" rx=\"6\" ")
                      .Append($"fill=\"{hex}\" fill-opacity=\"0.22\" stroke=\"{hex}\" stroke-opacity=\"0.95\" stroke-width=\"2\"/>\n");
                }
                sb.Append($"<text x=\"{N(x + 8)}\" y=\"{N(n.Position.y + 5)}\" fill=\"#eaf2ff\" font-size=\"15\">{Esc(Trunc(n.Label, 26))}</text>\n");
            }

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static Color EraColor(TechLevel tl)
        {
            switch (tl)
            {
                case TechLevel.Animal:
                case TechLevel.Neolithic: return new Color(0.85f, 0.36f, 0.30f);
                case TechLevel.Medieval:  return new Color(0.784f, 0.608f, 0.314f);
                case TechLevel.Industrial:return new Color(0.3f, 0.7f, 0.4f);
                case TechLevel.Spacer:    return new Color(0.0f, 0.898f, 0.898f);
                case TechLevel.Ultra:     return new Color(0.70f, 0.44f, 1.0f);
                case TechLevel.Archotech: return new Color(1.0f, 0.843f, 0.0f);
                default: return Color.gray;
            }
        }

        // 그룹키 → 안정적 고채도 색 (해시 기반 hue, 정렬 가시화용)
        private static Color GroupColor(string group)
        {
            int hash = 17;
            foreach (char ch in group) hash = hash * 31 + ch;
            float hue = ((hash & 0x7fffffff) % 360) / 360f;
            return HsvToRgb(hue, 0.65f, 0.95f);
        }

        private static Color HsvToRgb(float h, float s, float v)
        {
            float r = 0, g = 0, b = 0;
            int i = (int)MathF.Floor(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s), q = v * (1f - f * s), t = v * (1f - (1f - f) * s);
            switch (((i % 6) + 6) % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                case 5: r = v; g = p; b = q; break;
            }
            return new Color(r, g, b);
        }

        private static string Hex(Color c)
        {
            int r = Clamp255(c.r), g = Clamp255(c.g), b = Clamp255(c.b);
            return $"#{r:x2}{g:x2}{b:x2}";
        }

        private static int Clamp255(float v)
        {
            int i = (int)MathF.Round(v * 255f);
            return i < 0 ? 0 : (i > 255 ? 255 : i);
        }

        private static string Trunc(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
