using System.Collections.Generic;
using UnityEngine;

namespace YART.Rendering
{
    public static class GLSolidQuadBatcher
    {
        private static readonly List<Vector3> vertices = new List<Vector3>(8192);
        private static readonly List<Color> colors = new List<Color>(8192);
        private static Material quadMaterial;

        // 유니티 최댓값은 65535, 안전을 위해 20000
        private const int MaxVerticesPerBatch = 20000;

        public static Rect ClipRect { get; set; }

        private static void EnsureMaterial()
        {
            if (quadMaterial == null)
            {
                Shader shader = Shader.Find("GUI/Text Shader") ?? Shader.Find("Hidden/Internal-Colored");

                quadMaterial = new Material(shader);
                quadMaterial.hideFlags = HideFlags.HideAndDontSave;
                quadMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                quadMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                quadMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                quadMaterial.SetInt("_ZWrite", 0);
            }
        }

        public static void QueueQuad(Rect rect, Color color)
        {
            if (ClipRect.width > 0 && ClipRect.height > 0)
            {
                // Fast Way
                if (!rect.Overlaps(ClipRect)) return;

                float xMin = Mathf.Max(rect.xMin, ClipRect.xMin);
                float xMax = Mathf.Min(rect.xMax, ClipRect.xMax);
                float yMin = Mathf.Max(rect.yMin, ClipRect.yMin);
                float yMax = Mathf.Min(rect.yMax, ClipRect.yMax);

                if (xMax <= xMin || yMax <= yMin) return;

                rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }

            bool aboutToExceedBatch = vertices.Count >= MaxVerticesPerBatch - 4;
            if (aboutToExceedBatch) Flush();

            Vector2 p1 = GUIScreenTransform.ToScreen(new Vector2(rect.x, rect.y)); // Top-Left
            Vector2 p2 = GUIScreenTransform.ToScreen(new Vector2(rect.xMax, rect.yMax)); // Bottom-Right

            float x1 = p1.x;
            float y1 = p1.y;
            float x2 = p2.x;
            float y2 = p2.y;

            vertices.Add(new Vector3(x1, y1, 0));
            vertices.Add(new Vector3(x2, y1, 0));
            vertices.Add(new Vector3(x2, y2, 0));
            vertices.Add(new Vector3(x1, y2, 0));

            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
        }


        public static void QueueGradientQuadH(Rect rect, Color leftColor, Color rightColor)
        {
            if (ClipRect.width > 0 && ClipRect.height > 0)
            {
                if (!rect.Overlaps(ClipRect)) return;

                float xMin = Mathf.Max(rect.xMin, ClipRect.xMin);
                float xMax = Mathf.Min(rect.xMax, ClipRect.xMax);
                float yMin = Mathf.Max(rect.yMin, ClipRect.yMin);
                float yMax = Mathf.Min(rect.yMax, ClipRect.yMax);
                if (xMax <= xMin || yMax <= yMin) return;

                float tL = rect.width > 0f ? (xMin - rect.xMin) / rect.width : 0f;
                float tR = rect.width > 0f ? (xMax - rect.xMin) / rect.width : 1f;
                Color clippedL = Color.Lerp(leftColor, rightColor, tL);
                Color clippedR = Color.Lerp(leftColor, rightColor, tR);
                rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
                leftColor = clippedL;
                rightColor = clippedR;
            }

            if (vertices.Count >= MaxVerticesPerBatch - 4) Flush();

            Vector2 p1 = GUIScreenTransform.ToScreen(new Vector2(rect.x, rect.y));
            Vector2 p2 = GUIScreenTransform.ToScreen(new Vector2(rect.xMax, rect.yMax));

            vertices.Add(new Vector3(p1.x, p1.y, 0));
            vertices.Add(new Vector3(p2.x, p1.y, 0));
            vertices.Add(new Vector3(p2.x, p2.y, 0));
            vertices.Add(new Vector3(p1.x, p2.y, 0));

            colors.Add(leftColor);
            colors.Add(rightColor);
            colors.Add(rightColor);
            colors.Add(leftColor);
        }

        /// <summary>
        /// 임의 사각형을 큐에 넣는다. 정점 순서는 a->b->c->d
        /// </summary>
        public static void QueueFreeQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            freePoly[0].p = a; freePoly[0].c = color;
            freePoly[1].p = b; freePoly[1].c = color;
            freePoly[2].p = c; freePoly[2].c = color;
            freePoly[3].p = d; freePoly[3].c = color;

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

            // 최대 8정점 → 최대 3쿼드(12정점) 방출
            if (vertices.Count >= MaxVerticesPerBatch - 12) Flush();

            void AddQuad(in GLClip.V v0, in GLClip.V v1, in GLClip.V v2, in GLClip.V v3)
            {
                Vector2 s0 = GUIScreenTransform.ToScreen(v0.p);
                Vector2 s1 = GUIScreenTransform.ToScreen(v1.p);
                Vector2 s2 = GUIScreenTransform.ToScreen(v2.p);
                Vector2 s3 = GUIScreenTransform.ToScreen(v3.p);
                vertices.Add(new Vector3(s0.x, s0.y, 0)); colors.Add(v0.c);
                vertices.Add(new Vector3(s1.x, s1.y, 0)); colors.Add(v1.c);
                vertices.Add(new Vector3(s2.x, s2.y, 0)); colors.Add(v2.c);
                vertices.Add(new Vector3(s3.x, s3.y, 0)); colors.Add(v3.c);
            }

            int idx = 1;
            while (idx + 2 < n) { AddQuad(poly[0], poly[idx], poly[idx + 1], poly[idx + 2]); idx += 2; }
            if (idx + 1 <= n - 1) AddQuad(poly[0], poly[idx], poly[idx + 1], poly[idx + 1]);
        }

        private static readonly GLClip.V[] freePoly = new GLClip.V[4];

        public static void Flush()
        {
            if (vertices.Count == 0) return;

            EnsureMaterial();

            GL.PushMatrix();
            quadMaterial.SetPass(0);

            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);

            GL.Begin(GL.QUADS);
            for (int i = 0; i < vertices.Count; i++)
            {
                GL.Color(colors[i]);
                GL.Vertex(vertices[i]);
            }
            GL.End();

            GL.PopMatrix();
            Clear();
        }

        public static void Clear()
        {
            vertices.Clear();
            colors.Clear();
        }
    }
}
