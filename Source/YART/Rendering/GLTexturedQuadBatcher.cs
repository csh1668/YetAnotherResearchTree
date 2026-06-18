using System.Collections.Generic;
using UnityEngine;

namespace YART.Rendering
{
    public static class GLTexturedQuadBatcher
    {
        private class Batch
        {
            public readonly List<Vector3> Vertices = new List<Vector3>(2048);
            public readonly List<Color> Colors = new List<Color>(2048);
            public readonly List<Vector2> Uvs = new List<Vector2>(2048);
        }

        private static readonly Dictionary<Texture2D, Batch> batches = new Dictionary<Texture2D, Batch>();
        private static readonly Dictionary<Texture2D, Material> materials = new Dictionary<Texture2D, Material>();
        private static readonly List<Texture2D> drawOrder = new List<Texture2D>(8);

        // 유니티 최댓값은 65535, 안전을 위해 20000
        private const int MaxVerticesPerBatch = 20000;

        public static Rect ClipRect { get; set; }

        private static Material GetMaterial(Texture2D tex)
        {
            if (materials.TryGetValue(tex, out var mat)) return mat;

            Shader shader = Shader.Find("GUI/Text Shader") ?? Shader.Find("Hidden/Internal-Colored");

            mat = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = tex
            };
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            mat.SetInt("_ZWrite", 0);
            materials[tex] = mat;
            return mat;
        }

        public static void QueueQuad(Texture2D tex, Rect rect, Rect uv, Color color)
        {
            if (tex == null || color.a <= 0.003f) return;
            if (ClipRect.width > 0 && ClipRect.height > 0)
            {
                if (!rect.Overlaps(ClipRect)) return;

                float xMin = Mathf.Max(rect.xMin, ClipRect.xMin);
                float xMax = Mathf.Min(rect.xMax, ClipRect.xMax);
                float yMin = Mathf.Max(rect.yMin, ClipRect.yMin);
                float yMax = Mathf.Min(rect.yMax, ClipRect.yMax);
                if (xMax <= xMin || yMax <= yMin) return;

                if (rect.width > 0f && rect.height > 0f &&
                    (xMin > rect.xMin || xMax < rect.xMax || yMin > rect.yMin || yMax < rect.yMax))
                {
                    float u0 = (xMin - rect.xMin) / rect.width;
                    float u1 = (xMax - rect.xMin) / rect.width;
                    float v0 = (yMin - rect.yMin) / rect.height;
                    float v1 = (yMax - rect.yMin) / rect.height;
                    uv = Rect.MinMaxRect(
                        Mathf.Lerp(uv.xMin, uv.xMax, u0),
                        Mathf.Lerp(uv.yMax, uv.yMin, v1),
                        Mathf.Lerp(uv.xMin, uv.xMax, u1),
                        Mathf.Lerp(uv.yMax, uv.yMin, v0));
                    rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
                }
            }

            if (!batches.TryGetValue(tex, out var batch))
            {
                batch = new Batch();
                batches[tex] = batch;
            }

            bool aboutToExceedBatch = batch.Vertices.Count >= MaxVerticesPerBatch - 4;
            if (aboutToExceedBatch) Flush();
            if (batch.Vertices.Count == 0) drawOrder.Add(tex);

            Vector2 p1 = GUIScreenTransform.ToScreen(new Vector2(rect.x, rect.y));
            Vector2 p2 = GUIScreenTransform.ToScreen(new Vector2(rect.xMax, rect.yMax));

            // GUI 좌표는 Y가 아래로 증가하므로, 텍스처 V는 위아래를 뒤집는다
            batch.Vertices.Add(new Vector3(p1.x, p1.y, 0)); batch.Uvs.Add(new Vector2(uv.xMin, uv.yMax));
            batch.Vertices.Add(new Vector3(p2.x, p1.y, 0)); batch.Uvs.Add(new Vector2(uv.xMax, uv.yMax));
            batch.Vertices.Add(new Vector3(p2.x, p2.y, 0)); batch.Uvs.Add(new Vector2(uv.xMax, uv.yMin));
            batch.Vertices.Add(new Vector3(p1.x, p2.y, 0)); batch.Uvs.Add(new Vector2(uv.xMin, uv.yMin));

            for (int i = 0; i < 4; i++) batch.Colors.Add(color);
        }

        public static void QueueQuad(Texture2D tex, Rect rect, Color color)
        {
            QueueQuad(tex, rect, new Rect(0f, 0f, 1f, 1f), color);
        }

        public static void QueueFreeQuad(Texture2D tex,
            Vector2 a, Vector2 b, Vector2 c, Vector2 d,
            Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD,
            Color colA, Color colB, Color colC, Color colD)
        {
            if (tex == null) return;

            freePoly[0].p = a; freePoly[0].uv = uvA; freePoly[0].c = colA;
            freePoly[1].p = b; freePoly[1].uv = uvB; freePoly[1].c = colB;
            freePoly[2].p = c; freePoly[2].uv = uvC; freePoly[2].c = colC;
            freePoly[3].p = d; freePoly[3].uv = uvD; freePoly[3].c = colD;

            GLClip.V[] poly = freePoly;
            int n = 4;
            if (ClipRect.width > 0 && ClipRect.height > 0)
            {
                float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
                float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
                float minY = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y));
                float maxY = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
                if (maxX < ClipRect.xMin || minX > ClipRect.xMax ||
                    maxY < ClipRect.yMin || minY > ClipRect.yMax) return;

                bool fullyInside = minX >= ClipRect.xMin && maxX <= ClipRect.xMax &&
                                   minY >= ClipRect.yMin && maxY <= ClipRect.yMax;
                if (!fullyInside)
                {
                    n = GLClip.ClipConvex(freePoly, 4, ClipRect, out poly);
                    if (n < 3) return;
                }
            }

            if (!batches.TryGetValue(tex, out var batch))
            {
                batch = new Batch();
                batches[tex] = batch;
            }

            if (batch.Vertices.Count >= MaxVerticesPerBatch - 12) Flush();
            if (batch.Vertices.Count == 0) drawOrder.Add(tex);

            void AddTexQuad(Batch bt, in GLClip.V v0, in GLClip.V v1, in GLClip.V v2, in GLClip.V v3)
            {
                Vector2 s0 = GUIScreenTransform.ToScreen(v0.p);
                Vector2 s1 = GUIScreenTransform.ToScreen(v1.p);
                Vector2 s2 = GUIScreenTransform.ToScreen(v2.p);
                Vector2 s3 = GUIScreenTransform.ToScreen(v3.p);
                bt.Vertices.Add(new Vector3(s0.x, s0.y, 0)); bt.Uvs.Add(v0.uv); bt.Colors.Add(v0.c);
                bt.Vertices.Add(new Vector3(s1.x, s1.y, 0)); bt.Uvs.Add(v1.uv); bt.Colors.Add(v1.c);
                bt.Vertices.Add(new Vector3(s2.x, s2.y, 0)); bt.Uvs.Add(v2.uv); bt.Colors.Add(v2.c);
                bt.Vertices.Add(new Vector3(s3.x, s3.y, 0)); bt.Uvs.Add(v3.uv); bt.Colors.Add(v3.c);
            }

            int idx = 1;
            while (idx + 2 < n) { AddTexQuad(batch, poly[0], poly[idx], poly[idx + 1], poly[idx + 2]); idx += 2; }
            if (idx + 1 <= n - 1) AddTexQuad(batch, poly[0], poly[idx], poly[idx + 1], poly[idx + 1]);
        }

        private static readonly GLClip.V[] freePoly = new GLClip.V[4];

        /// <summary>
        /// 9-slice 쿼드. 코너는 고정 크기로, 변/중앙은 늘려서 그린다.
        /// </summary>
        /// <param name="cornerScreen">화면상 코너 크기 (px)</param>
        /// <param name="cornerUV">텍스처상 코너 비율 (예: 12px 코너 / 64px 텍스처 = 0.1875)</param>
        public static void QueueNineSlice(Texture2D tex, Rect rect, Color color, float cornerScreen, float cornerUV)
        {
            if (tex == null || color.a <= 0.003f) return;
            if (ClipRect.width > 0 && ClipRect.height > 0 && !rect.Overlaps(ClipRect)) return;

            float c = Mathf.Min(cornerScreen, rect.width / 2f, rect.height / 2f);
            if (c < 1f)
            {
                QueueQuad(tex, rect, color);
                return;
            }

            float u = cornerUV;
            float x0 = rect.x, x1 = rect.x + c, x2 = rect.xMax - c, x3 = rect.xMax;
            float y0 = rect.y, y1 = rect.y + c, y2 = rect.yMax - c, y3 = rect.yMax;

            // 행(상/중/하) × 열(좌/중/우)
            // 주의: QueueQuad가 GUI 좌표(Y 아래로 증가)에 맞춰 V를 뒤집으므로,
            // 화면 "상단" 행에는 텍스처의 윗부분인 V [1-u, 1]을 할당해야 한다.
            QueueQuad(tex, Rect.MinMaxRect(x0, y0, x1, y1), Rect.MinMaxRect(0, 1 - u, u, 1), color);
            QueueQuad(tex, Rect.MinMaxRect(x1, y0, x2, y1), Rect.MinMaxRect(u, 1 - u, 1 - u, 1), color);
            QueueQuad(tex, Rect.MinMaxRect(x2, y0, x3, y1), Rect.MinMaxRect(1 - u, 1 - u, 1, 1), color);

            QueueQuad(tex, Rect.MinMaxRect(x0, y1, x1, y2), Rect.MinMaxRect(0, u, u, 1 - u), color);
            QueueQuad(tex, Rect.MinMaxRect(x1, y1, x2, y2), Rect.MinMaxRect(u, u, 1 - u, 1 - u), color);
            QueueQuad(tex, Rect.MinMaxRect(x2, y1, x3, y2), Rect.MinMaxRect(1 - u, u, 1, 1 - u), color);

            QueueQuad(tex, Rect.MinMaxRect(x0, y2, x1, y3), Rect.MinMaxRect(0, 0, u, u), color);
            QueueQuad(tex, Rect.MinMaxRect(x1, y2, x2, y3), Rect.MinMaxRect(u, 0, 1 - u, u), color);
            QueueQuad(tex, Rect.MinMaxRect(x2, y2, x3, y3), Rect.MinMaxRect(1 - u, 0, 1, u), color);
        }

        public static void Flush()
        {
            if (drawOrder.Count == 0) return;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);

            foreach (var tex in drawOrder)
            {
                var batch = batches[tex];
                if (batch.Vertices.Count == 0) continue;

                GetMaterial(tex).SetPass(0);
                GL.Begin(GL.QUADS);
                for (int i = 0; i < batch.Vertices.Count; i++)
                {
                    GL.Color(batch.Colors[i]);
                    GL.TexCoord2(batch.Uvs[i].x, batch.Uvs[i].y);
                    GL.Vertex(batch.Vertices[i]);
                }
                GL.End();
            }

            GL.PopMatrix();
            Clear();
        }

        public static void Clear()
        {
            foreach (var batch in batches.Values)
            {
                batch.Vertices.Clear();
                batch.Colors.Clear();
                batch.Uvs.Clear();
            }
            drawOrder.Clear();
        }
    }
}
