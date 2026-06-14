using UnityEngine;
using Verse;
using YART.Data;

namespace YART.Utils
{
    public static class GUIDrawingUtilities
    {
        public static void DrawBorderLines(Rect rect, Color color, float width)
        {
            // Top
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, width), color);
            // Bottom
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            // Left
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, width, rect.height), color);
            // Right
            Widgets.DrawBoxSolid(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
        }

        public static void DrawTopAccentBar(Rect rect, Color color, float height = 3f)
        {
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, height), color);
        }

        /// <summary>
        /// 흰색+알파 아이콘 텍스처를 지정 색으로 틴트해서 그린다.
        /// </summary>
        public static void DrawIcon(Rect rect, Texture2D icon, Color color)
        {
            if (icon == null) return;
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit);
            GUI.color = prev;
        }

        public static void DrawIconPlaceholder(Rect rect, Color color)
        {
            Widgets.DrawBoxSolid(rect, color);
            Rect innerRect = rect.ContractedBy(2f);
            Widgets.DrawBoxSolid(innerRect, new Color(color.r * 0.7f, color.g * 0.7f, color.b * 0.7f, color.a));
        }
    }
}
