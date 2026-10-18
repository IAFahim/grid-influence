using UnityEngine;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiSparkline : VisualElement
    {
        private GiHistory _history;

        public GiSparkline()
        {
            style.height = 28;
            style.minWidth = 120;
            generateVisualContent += Draw;
        }

        public void Show(GiHistory history)
        {
            _history = history;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            if (_history == null || _history.Count < 2) return;

            _history.Range(out var low, out var high);
            var span = Mathf.Max(1, high - low);
            var painter = context.painter2D;
            var width = contentRect.width;
            var height = contentRect.height;
            painter.lineWidth = 1.5f;
            painter.strokeColor = new Color(0.90f, 0.45f, 0.05f);
            painter.BeginPath();
            for (var i = 0; i < _history.Count; i++)
            {
                var x = width * i / (_history.Count - 1);
                var y = height - height * (_history[i] - low) / span;
                if (i == 0) painter.MoveTo(new Vector2(x, y));
                else painter.LineTo(new Vector2(x, y));
            }

            painter.Stroke();
        }
    }
}
