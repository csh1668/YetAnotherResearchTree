using UnityEngine;

namespace YART.Rendering
{
    /// <summary>
    /// GUI->스크린 변환을 프레임당 1회 캡처한다
    /// </summary>
    public static class GUIScreenTransform
    {
        private static float sx = 1f, sy = 1f, tx, ty;

        public static void Capture()
        {
            Vector2 o = GUIUtility.GUIToScreenPoint(Vector2.zero);
            Vector2 x = GUIUtility.GUIToScreenPoint(new Vector2(1f, 0f));
            Vector2 y = GUIUtility.GUIToScreenPoint(new Vector2(0f, 1f));
            sx = x.x - o.x; sy = y.y - o.y;
            if (sx == 0f) sx = 1f;
            if (sy == 0f) sy = 1f;
            // 평행 이동도 물리로 맞춰야 함
            tx = o.x * sx; ty = o.y * sy;
        }

        public static Vector2 ToScreen(Vector2 p) => new Vector2(p.x * sx + tx, p.y * sy + ty);
    }
}
