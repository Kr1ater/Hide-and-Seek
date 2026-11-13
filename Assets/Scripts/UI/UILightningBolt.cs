using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Рисует ломаную линию молнии прямо в Canvas, без единой картинки.
//
// Наследуемся от Graphic - это базовый класс всей UI-графики Unity. Он даёт
// нам OnPopulateMesh: Unity зовёт его, когда нужно построить меш, а мы
// вручную выкладываем прямоугольники вдоль ломаной.
//
// Reveal позволяет "прочертить" молнию сверху вниз: 0 - ничего не видно,
// 1 - линия нарисована целиком.
public class UILightningBolt : Graphic
{
    private readonly List<Vector2> _points = new List<Vector2>();

    private float _thickness = 6f;
    private float _reveal = 1f;

    public void SetPoints(List<Vector2> points, float thickness)
    {
        _points.Clear();
        if (points != null) _points.AddRange(points);

        _thickness = thickness;
        SetVerticesDirty();
    }

    public float Reveal
    {
        get => _reveal;
        set
        {
            _reveal = Mathf.Clamp01(value);
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        if (_points.Count < 2 || _reveal <= 0f) return;

        float revealLength = TotalLength() * _reveal;
        float drawnLength = 0f;

        for (int i = 0; i < _points.Count - 1; i++)
        {
            Vector2 from = _points[i];
            Vector2 to = _points[i + 1];

            float segmentLength = Vector2.Distance(from, to);
            if (segmentLength <= 0.0001f) continue;

            // Дошли до границы видимой части - рисуем сегмент не целиком и выходим
            if (drawnLength + segmentLength > revealLength)
            {
                float t = (revealLength - drawnLength) / segmentLength;
                AddSegment(vh, from, Vector2.Lerp(from, to, Mathf.Clamp01(t)));
                return;
            }

            AddSegment(vh, from, to);
            drawnLength += segmentLength;
        }
    }

    private float TotalLength()
    {
        float length = 0f;

        for (int i = 0; i < _points.Count - 1; i++)
        {
            length += Vector2.Distance(_points[i], _points[i + 1]);
        }

        return length;
    }

    // Один отрезок молнии - это прямоугольник из двух треугольников,
    // развёрнутый перпендикулярно направлению отрезка
    private void AddSegment(VertexHelper vh, Vector2 from, Vector2 to)
    {
        Vector2 direction = (to - from).normalized;
        Vector2 offset = new Vector2(-direction.y, direction.x) * (_thickness * 0.5f);

        int startIndex = vh.currentVertCount;

        UIVertex vertex = UIVertex.simpleVert;
        vertex.color = color;

        vertex.position = from - offset;
        vh.AddVert(vertex);
        vertex.position = from + offset;
        vh.AddVert(vertex);
        vertex.position = to + offset;
        vh.AddVert(vertex);
        vertex.position = to - offset;
        vh.AddVert(vertex);

        vh.AddTriangle(startIndex, startIndex + 1, startIndex + 2);
        vh.AddTriangle(startIndex + 2, startIndex + 3, startIndex);
    }
}
