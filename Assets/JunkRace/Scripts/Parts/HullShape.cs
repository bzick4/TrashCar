using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Форма корпуса по вершинам его модели (а не по коробкам-коллайдерам): детали и колёса прижимаются
    /// вплотную к тому, что видно. Меш модели должен быть доступен для чтения (ставит сборщик сцены).
    /// </summary>
    public static class HullShape
    {
        const int MaxPoints = 30000;
        static readonly List<Vector3> verts = new List<Vector3>();
        static readonly List<Vector3> pts = new List<Vector3>();

        /// <summary>Вершины видимой модели корпуса в координатах frame. Пусто — модели нет или меш не читается.</summary>
        public static List<Vector3> Points(Part hull, Transform frame)
        {
            pts.Clear();
            if (hull == null) return pts;
            foreach (var mf in hull.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                if (mf.GetComponentInParent<Part>() != hull) continue;            // прикрученные детали — не корпус
                if (!mf.TryGetComponent(out MeshRenderer r) || !r.enabled) continue;
                mesh.GetVertices(verts);
                int step = Mathf.Max(1, verts.Count / MaxPoints);
                Matrix4x4 m = frame.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Count; i += step) pts.Add(m.MultiplyPoint3x4(verts[i]));
            }
            return pts;
        }

        /// <summary>
        /// Зазор от грани коробки box (в координатах тех же точек) до корпуса по направлению axis (±X/±Y/±Z).
        /// Учитываются точки впереди центра коробки и внутри её сечения (чуть ужатого, чтобы край
        /// не цеплялся за подлокотник). Отрицательный — деталь утонула в корпусе.
        /// </summary>
        public static bool Gap(List<Vector3> points, Bounds box, Vector3 axis, out float gap, float shrink = 0.7f)
        {
            gap = 0f;
            Vector3 a = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
            Vector3 across = (Vector3.one - a);
            Vector3 half = Vector3.Scale(box.extents, across) * shrink;
            float center = Vector3.Dot(box.center, axis);
            float face = center + Vector3.Dot(box.extents, a);
            float best = float.MaxValue;
            foreach (var p in points)
            {
                float along = Vector3.Dot(p, axis);
                if (along <= center || along >= best) continue;
                Vector3 d = Vector3.Scale(p - box.center, across);
                if (Mathf.Abs(d.x) > half.x || Mathf.Abs(d.y) > half.y || Mathf.Abs(d.z) > half.z) continue;
                best = along;
            }
            if (best == float.MaxValue) return false;
            gap = best - face;
            return true;
        }
    }
}
