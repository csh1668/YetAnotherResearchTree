using System.Collections.Generic;
using UnityEngine;

namespace YART.Rendering
{
    public static class GLLineBatcher
    {
        private static readonly List<Vector3> vertices = new List<Vector3>(8192);
        private static readonly List<Color> colors = new List<Color>(8192);
        private static Material lineMaterial;

        // 유니티 최댓값은 65535, 안전을 위해 20000
        private const int MaxVerticesPerBatch = 20000;

        public static Rect ClipRect { get; set; }

        private static void EnsureMaterial()
        {
            if (lineMaterial == null)
            {
                Shader shader = Shader.Find("GUI/Text Shader") ?? Shader.Find("Hidden/Internal-Colored");

                lineMaterial = new Material(shader);
                lineMaterial.hideFlags = HideFlags.HideAndDontSave;
                lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                lineMaterial.SetInt("_ZWrite", 0);
            }
        }

        public static void QueueLine(Vector2 start, Vector2 end, Color startColor, Color endColor)
        {
            if (ClipRect.width > 0 && ClipRect.height > 0)
            {
                if (!LiangBarskyClip(ref start, ref end, ClipRect))
                {
                    // 선분이 클립 영역과 완전히 겹치지 않음
                    return;
                }
            }

            bool aboutToExceedBatch = vertices.Count >= MaxVerticesPerBatch - 2;
            if (aboutToExceedBatch)
            {
                Flush();
            }

            Vector2 screenStart = GUIScreenTransform.ToScreen(start);
            Vector2 screenEnd = GUIScreenTransform.ToScreen(end);

            vertices.Add(new Vector3(screenStart.x, screenStart.y, 0f));
            vertices.Add(new Vector3(screenEnd.x, screenEnd.y, 0f));

            colors.Add(startColor);
            colors.Add(endColor);
        }

        public static void QueueLine(Vector2 start, Vector2 end, Color color)
        {
            QueueLine(start, end, color, color);
        }

        /// <summary>
        /// 선분 p1-p2가 clipRect와 교차하는 부분을 계산한다. (GUI 좌표)
        /// p1-p2가 완전히 clipRect 밖에 있으면 false 반환
        /// </summary>
        private static bool LiangBarskyClip(ref Vector2 p1, ref Vector2 p2, Rect clipRect)
        {
            float t0 = 0.0f, t1 = 1.0f, dx = p2.x - p1.x, dy = p2.y - p1.y;

            bool ClipTest(float p, float q, ref float x0, ref float x1)
            {
                float u = q / p;
                if (p < 0.0f)
                {
                    if (u > x1) return false;
                    if (u > x0) x0 = u;
                }
                else if (p > 0.0f)
                {
                    if (u < x0) return false;
                    if (u < x1) x1 = u;
                }
                else if (q < 0.0f)
                {
                    return false;
                }
                return true;
            }

            if (ClipTest(-dx, p1.x - clipRect.xMin, ref t0, ref t1)) // Left
            {
                if (ClipTest(dx, clipRect.xMax - p1.x, ref t0, ref t1)) // Right
                {
                    if (ClipTest(-dy, p1.y - clipRect.yMin, ref t0, ref t1)) // Bottom (Top in GUI)
                    {
                        if (ClipTest(dy, clipRect.yMax - p1.y, ref t0, ref t1)) // Top (Bottom in GUI)
                        {
                            if (t1 < 1.0f)
                            {
                                p2.x = p1.x + t1 * dx;
                                p2.y = p1.y + t1 * dy;
                            }
                            if (t0 > 0.0f)
                            {
                                p1.x = p1.x + t0 * dx;
                                p1.y = p1.y + t0 * dy;
                            }
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public static void Flush()
        {
            if (vertices.Count == 0) return;

            EnsureMaterial();

            GL.PushMatrix();
            lineMaterial.SetPass(0);

            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);

            GL.Begin(GL.LINES);
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
