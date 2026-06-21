using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class Dialog_YARTColorPicker : Window
    {
        private readonly Color defaultColor;
        private readonly Action<Color> onSave;

        private Color color;
        private readonly Color oldColor;

        // HSV는 피커 컨트롤의 1차 상태
        private float hue, sat, val;

        private readonly string[] rgbBuf = new string[3];
        private string hexBuf;
        private Color lastSync = new Color(-1f, -1f, -1f);

        private bool svDragging, hueDragging;

        // SV 사각형 텍스처, Hue 바 텍스처
        private Texture2D svTex;
        private Color[] svPx;
        private float svTexHue = -1f;
        private static Texture2D hueTex;

        private static readonly List<Color> pickable = BuildPickable();
        private static readonly Regex HexRegex = new Regex("^[0-9a-fA-F]*$");
        private static readonly Regex IntRegex = new Regex("^[0-9]*$");

        private const int SvRes = 128;

        public override Vector2 InitialSize => new Vector2(580f, 470f);

        public Dialog_YARTColorPicker(Color current, Color def, Action<Color> onSave)
        {
            current.a = 1f;
            this.color = current;
            this.oldColor = current;
            this.defaultColor = new Color(def.r, def.g, def.b, 1f);
            this.onSave = onSave;
            Color.RGBToHSV(color, out hue, out sat, out val);

            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            draggable = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (svTex != null) { UnityEngine.Object.Destroy(svTex); svTex = null; }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (color != lastSync) { SyncBuffers(); lastSync = color; }

            // 제목
            using (Temporary.Font(GameFont.Medium))
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f),
                    "ChooseAColor".Translate().CapitalizeFirst());
            float top = inRect.y + 38f;

            // SV 사각형 + Hue 바
            float svW = 240f, svH = 200f, hueW = 22f;
            Rect svRect = new Rect(inRect.x, top, svW, svH);
            Rect hueRect = new Rect(svRect.xMax + 10f, top, hueW, svH);
            DrawSvSquare(svRect);
            DrawHueBar(hueRect);

            // 우측 컬럼
            Rect right = new Rect(hueRect.xMax + 14f, top, inRect.xMax - (hueRect.xMax + 14f), svH);
            DrawRightColumn(right);

            // SV 아래
            float y = svRect.yMax + 12f;
            float ctlW = hueRect.xMax - inRect.x;
            const float rowH = 28f, rowGap = 4f;
            DrawChannel(new Rect(inRect.x, y, ctlW, rowH), "R", 0); y += rowH + rowGap;
            DrawChannel(new Rect(inRect.x, y, ctlW, rowH), "G", 1); y += rowH + rowGap;
            DrawChannel(new Rect(inRect.x, y, ctlW, rowH), "B", 2); y += rowH + rowGap;
            DrawHexRow(new Rect(inRect.x, y, ctlW, rowH));

            // 하단 버튼
            float butH = 38f, butW = 150f;
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - butH, butW, butH), "Cancel".Translate()))
                Close();
            if (Widgets.ButtonText(new Rect(inRect.xMax - butW, inRect.yMax - butH, butW, butH), "Accept".Translate()))
            {
                onSave?.Invoke(color);
                Close();
            }
        }

        private void DrawSvSquare(Rect r)
        {
            if (svTex == null || !Mathf.Approximately(svTexHue, hue)) RebuildSvTex();
            GUI.DrawTexture(r, svTex);
            Widgets.DrawBox(r, 1);

            // 핸들 (상=명도1, 좌=채도0)
            float hx = r.x + sat * r.width;
            float hy = r.y + (1f - val) * r.height;
            Rect handle = new Rect(hx - 5f, hy - 5f, 10f, 10f);
            using (Temporary.Color(Color.black)) Widgets.DrawBox(handle, 2);
            using (Temporary.Color(Color.white)) Widgets.DrawBox(handle.ContractedBy(1f), 1);

            HandleDrag(r, ref svDragging, mp =>
            {
                sat = Mathf.Clamp01((mp.x - r.x) / r.width);
                val = Mathf.Clamp01(1f - (mp.y - r.y) / r.height);
                ApplyHsv();
            });
        }

        private void RebuildSvTex()
        {
            if (svTex == null)
            {
                svTex = new Texture2D(SvRes, SvRes, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                svPx = new Color[SvRes * SvRes];
            }
            for (int yy = 0; yy < SvRes; yy++)
            {
                float v = (float)yy / (SvRes - 1); // y=0(아래)=명도0
                for (int xx = 0; xx < SvRes; xx++)
                    svPx[yy * SvRes + xx] = Color.HSVToRGB(hue, (float)xx / (SvRes - 1), v);
            }
            svTex.SetPixels(svPx);
            svTex.Apply();
            svTexHue = hue;
        }

        private void DrawHueBar(Rect r)
        {
            GUI.DrawTexture(r, HueTexture());
            Widgets.DrawBox(r, 1);

            float hy = r.y + hue * r.height; // 상=hue0
            Rect marker = new Rect(r.x - 2f, hy - 2f, r.width + 4f, 4f);
            using (Temporary.Color(Color.white)) Widgets.DrawBox(marker, 1);

            HandleDrag(r, ref hueDragging, mp =>
            {
                hue = Mathf.Clamp01((mp.y - r.y) / r.height);
                ApplyHsv();
            });
        }

        private static Texture2D HueTexture()
        {
            if (hueTex == null)
            {
                const int H = 128;
                hueTex = new Texture2D(1, H, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var px = new Color[H];
                for (int y = 0; y < H; y++)
                    px[y] = Color.HSVToRGB(1f - (float)y / (H - 1), 1f, 1f); // 아래=hue1, 위=hue0
                hueTex.SetPixels(px);
                hueTex.Apply();
            }
            return hueTex;
        }

        private void DrawRightColumn(Rect r)
        {
            // 미리보기
            Rect cur = new Rect(r.x, r.y, r.width, 30f);
            Widgets.DrawBoxSolid(cur, color);
            Widgets.DrawBox(cur, 1);
            Rect old = new Rect(r.x, cur.yMax, r.width, 18f);
            Widgets.DrawBoxSolid(old, oldColor);
            Widgets.DrawBox(old, 1);

            float gy = old.yMax + 10f;
            const float sw = 22f, sgap = 4f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((r.width + sgap) / (sw + sgap)));
            for (int i = 0; i < pickable.Count; i++)
            {
                int row = i / cols, col = i % cols;
                Rect s = new Rect(r.x + col * (sw + sgap), gy + row * (sw + sgap), sw, sw);
                if (s.yMax > r.yMax - 28f) break;
                Widgets.DrawBoxSolid(s, pickable[i]);
                if (Mouse.IsOver(s)) using (Temporary.Color(Color.white)) Widgets.DrawBox(s, 2);
                else Widgets.DrawBox(s, 1);
                if (Widgets.ButtonInvisible(s)) SetColor(pickable[i]);
            }

            Rect def = new Rect(r.x, r.yMax - 24f, r.width, 24f);
            if (Widgets.ButtonText(def, "Default".Translate().CapitalizeFirst()))
                SetColor(defaultColor);
        }

        private void DrawChannel(Rect row, string label, int idx)
        {
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
                Widgets.Label(new Rect(row.x, row.y, 16f, row.height), label);

            Rect fieldRect = new Rect(row.xMax - 44f, row.y + 2f, 44f, row.height - 4f);
            Rect sliderRect = new Rect(row.x + 20f, row.y, fieldRect.x - (row.x + 20f) - 8f, row.height);

            float cur = Mathf.Round(color[idx] * 255f);
            float nv = Widgets.HorizontalSlider(sliderRect, cur, 0f, 255f, roundTo: 1f);
            if (Mathf.RoundToInt(nv) != Mathf.RoundToInt(cur)) SetChannel(idx, nv / 255f);

            string nb = Widgets.TextField(fieldRect, rgbBuf[idx], 3, IntRegex);
            if (nb != rgbBuf[idx])
            {
                rgbBuf[idx] = nb;
                if (int.TryParse(nb, out int iv))
                {
                    SetChannel(idx, Mathf.Clamp01(iv / 255f));
                    lastSync = color; // 타이핑 중 버퍼 클로버 방지
                }
            }
        }

        private void DrawHexRow(Rect row)
        {
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
                Widgets.Label(new Rect(row.x, row.y, 20f, row.height), "#");
            Rect fieldRect = new Rect(row.x + 22f, row.y + 2f, 100f, row.height - 4f);
            string nb = Widgets.TextField(fieldRect, hexBuf, 6, HexRegex);
            if (nb != hexBuf)
            {
                hexBuf = nb;
                if (nb.Length == 6 && ColorUtility.TryParseHtmlString("#" + nb, out var parsed))
                {
                    SetColor(new Color(parsed.r, parsed.g, parsed.b, 1f));
                    lastSync = color;
                }
            }
        }

        private void ApplyHsv()
        {
            color = Color.HSVToRGB(hue, sat, val);
            color.a = 1f;
        }

        private void SetChannel(int idx, float v)
        {
            color[idx] = Mathf.Clamp01(v);
            color.a = 1f;
            ResyncHsvFromColor();
        }

        private void SetColor(Color c)
        {
            color = new Color(c.r, c.g, c.b, 1f);
            ResyncHsvFromColor();
        }

        private void ResyncHsvFromColor()
        {
            Color.RGBToHSV(color, out float h, out float s, out float v);
            if (s > 0.0001f) hue = h; // 회색이면 기존 hue 유지(휠 위치 보존)
            sat = s;
            val = v;
        }

        private void SyncBuffers()
        {
            rgbBuf[0] = Mathf.RoundToInt(color.r * 255f).ToString();
            rgbBuf[1] = Mathf.RoundToInt(color.g * 255f).ToString();
            rgbBuf[2] = Mathf.RoundToInt(color.b * 255f).ToString();
            hexBuf = ColorUtility.ToHtmlStringRGB(color);
        }

        private static void HandleDrag(Rect r, ref bool dragging, Action<Vector2> onPos)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                dragging = true; onPos(e.mousePosition); e.Use();
            }
            else if (dragging && e.type == EventType.MouseDrag)
            {
                onPos(e.mousePosition); e.Use();
            }
            if (e.rawType == EventType.MouseUp) dragging = false;
        }

        private static List<Color> BuildPickable()
        {
            var list = new List<Color>();
            foreach (var id in ColorPalettes.SelectableIds)
            {
                var p = ColorPalettes.Get(id);
                foreach (var c in p.era) AddUnique(list, c);
                AddUnique(list, p.prereqMet);
                AddUnique(list, p.prereqUnmet);
            }
            return list;
        }

        private static void AddUnique(List<Color> list, Color c)
        {
            foreach (var e in list)
                if (Mathf.Abs(e.r - c.r) < 0.004f && Mathf.Abs(e.g - c.g) < 0.004f && Mathf.Abs(e.b - c.b) < 0.004f)
                    return;
            list.Add(new Color(c.r, c.g, c.b, 1f));
        }
    }
}
