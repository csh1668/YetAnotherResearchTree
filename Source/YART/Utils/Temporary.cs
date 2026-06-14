using System;
using UnityEngine;
using Verse;

namespace YART.Utils
{
    /// <summary>
    /// Unity GUI 관련 설정을 임시로 변경하고, 사용 후 자동으로 복구한다
    /// </summary>
    public static class Temporary
    {
        public readonly struct TextAnchorScope : IDisposable
        {
            private readonly TextAnchor _original;
            public TextAnchorScope(TextAnchor anchor)
            {
                _original = Text.Anchor;
                Text.Anchor = anchor;
            }
            public void Dispose() => Text.Anchor = _original;
        }

        public readonly struct GuiColorScope : IDisposable
        {
            private readonly Color _original;
            public GuiColorScope(Color color)
            {
                _original = GUI.color;
                GUI.color = color;
            }
            public void Dispose() => GUI.color = _original;
        }

        public readonly struct TextFontScope : IDisposable
        {
            private readonly GameFont _original;
            public TextFontScope(GameFont font)
            {
                _original = Text.Font;
                Text.Font = font;
            }
            public void Dispose() => Text.Font = _original;
        }

        public readonly struct AlphaScope : IDisposable
        {
            private readonly Color _original;
            public AlphaScope(float alpha)
            {
                _original = GUI.color;
                GUI.color = new Color(_original.r, _original.g, _original.b, _original.a * alpha);
            }
            public void Dispose() => GUI.color = _original;
        }

        public readonly struct MatrixScope : IDisposable
        {
            private readonly Matrix4x4 _original;
            public MatrixScope(Matrix4x4 matrix)
            {
                _original = GUI.matrix;
                GUI.matrix = matrix;
            }
            public void Dispose() => GUI.matrix = _original;
        }

        // 편의를 위한 팩토리 메서드
        public static TextAnchorScope Anchor(TextAnchor anchor) => new TextAnchorScope(anchor);
        public static GuiColorScope Color(Color color) => new GuiColorScope(color);
        public static TextFontScope Font(GameFont font) => new TextFontScope(font);
        public static AlphaScope Alpha(float alpha) => new AlphaScope(alpha);
        public static MatrixScope Matrix(Matrix4x4 matrix) => new MatrixScope(matrix);
    }
}
