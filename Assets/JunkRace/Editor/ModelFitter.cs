using UnityEditor;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Настоящие модели вместо примитивов. Модели лежат в Assets/Prefab/Car/&lt;Папка&gt;/
    /// (.prefab или .fbx), ищутся по имени файла. Физика остаётся на простых невидимых коробках —
    /// так машина ведёт себя предсказуемо, а модель только «одевает» её и подгоняется по размеру.
    /// </summary>
    public static class ModelFitter
    {
        public const string CarFolder = "Assets/Prefab/Car";

        /// <summary>
        /// Достаёт встроенные в FBX текстуры (Tripo и Blender кладут их внутрь файла, а Unity сам не находит —
        /// в файле прописан путь с чужого компьютера). Кладёт в папку «имя_textures» рядом с моделью.
        /// Возвращает эту папку (или null, если текстур нет).
        /// </summary>
        public static string ExtractTextures(GameObject model)
        {
            string path = AssetDatabase.GetAssetPath(model);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is ModelImporter importer)) return null;
            string dir = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string folder = dir + "/" + name + "_textures";
            if (AssetDatabase.IsValidFolder(folder)) return folder;
            AssetDatabase.CreateFolder(dir, name + "_textures");
            bool ok = false;
            try { ok = importer.ExtractTextures(folder); }
            catch (System.Exception e) { Debug.LogWarning($"JunkRace: не удалось достать текстуры из {path}: {e.Message}"); }
            AssetDatabase.Refresh();
            if (!ok || AssetDatabase.FindAssets("t:Texture2D", new[] { folder }).Length == 0)
            {
                AssetDatabase.DeleteAsset(folder);
                return null;
            }
            return folder;
        }

        /// <summary>Ищет модель по имени в папке (например "Body", "bathtub"). .prefab важнее .fbx.</summary>
        public static GameObject Find(string subFolder, string fileName)
        {
            string folder = CarFolder + "/" + subFolder;
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            GameObject fallback = null;
            var found = new System.Collections.Generic.List<string>(AssetDatabase.FindAssets(fileName + " t:Prefab", new[] { folder }));
            found.AddRange(AssetDatabase.FindAssets(fileName + " t:Model", new[] { folder }));
            foreach (var guid in found)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), fileName, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (path.EndsWith(".prefab")) return go;
                if (fallback == null) fallback = go;
            }
            return fallback;
        }

        /// <summary>Первая модель в папке (.prefab важнее .fbx), например Assets/Prefab/junky/Trash.</summary>
        public static GameObject FindFirst(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            GameObject fallback = null;
            // t:Model — FBX/OBJ, t:Prefab — префабы (так надёжнее, чем t:GameObject).
            var list = new System.Collections.Generic.List<string>(AssetDatabase.FindAssets("t:Prefab", new[] { folder }));
            list.AddRange(AssetDatabase.FindAssets("t:Model", new[] { folder }));
            var guids = list.ToArray();
            System.Array.Sort(guids, (a, b) => string.CompareOrdinal(AssetDatabase.GUIDToAssetPath(a), AssetDatabase.GUIDToAssetPath(b)));
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                if (path.EndsWith(".prefab")) return go;
                if (fallback == null) fallback = go;
            }
            return fallback;
        }

        /// <summary>
        /// Если у модели потерялась текстура (FBX выгружен без встроенных картинок) — подставляет
        /// найденную в папке картинку с «basecolor»/«albedo»/«diffuse» в имени.
        /// </summary>
        public static void FixMissingTexture(GameObject model, string folder)
        {
            Texture2D tex = FindBaseColor(folder);
            if (tex == null) return;

            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    // Не только пустую: у моделей Tripo текстуры называются одинаково (texture_pbr_20250901),
                    // и Unity при импорте цепляет первую попавшуюся с таким именем — чужую. Ставим свою.
                    if (m == null || !m.HasProperty("_BaseMap") || m.GetTexture("_BaseMap") == tex) continue;
                    var own = Shader.Find(LookBuilder.ToonShaderName);
                    var fixedMat = own != null ? new Material(own) : new Material(m);
                    fixedMat.name = m.name + " (текстура)";
                    fixedMat.color = Color.white;
                    fixedMat.SetTexture("_BaseMap", tex);
                    if (fixedMat.HasProperty("_NoiseStrength")) fixedMat.SetFloat("_NoiseStrength", 0f);
                    mats[i] = fixedMat;
                }
                r.sharedMaterials = mats;
            }
        }

        /// <summary>
        /// Основная текстура в папке: с «basecolor/albedo/diffuse» в имени, а если таких нет —
        /// любая, кроме карт металла, нормалей, шероховатости (у Tripo основная — без суффикса).
        /// </summary>
        static Texture2D FindBaseColor(string folder)
        {
            string[] good = { "basecolor", "albedo", "diffuse" };
            string[] skip = { "metal", "normal", "rough", "_rm", "height", "_ao", "occlusion", "emiss" };
            Texture2D fallback = null;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                foreach (var g in good) if (file.Contains(g)) return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                bool bad = false;
                foreach (var k in skip) if (file.Contains(k)) { bad = true; break; }
                if (!bad && fallback == null) fallback = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            return fallback;
        }

        /// <summary>Выпуклые коллайдеры по мешам модели — дёшево для физики даже у тяжёлой модели.</summary>
        public static void AddConvexColliders(GameObject model)
        {
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = true;
            }
        }

        /// <summary>
        /// Ставит модель дочерним объектом и вписывает её габариты в коробку center/size
        /// (в координатах owner). Длинную сторону разворачивает вдоль Z — вперёд по машине.
        /// Коллайдеры модели убираются, материалы переводятся на наш toon-шейдер.
        /// </summary>
        /// <param name="flat">Плоская деталь (дверь, доска, руль): модель в файле может стоять на ребре и быть
        /// повёрнута под углом — выравниваем её и кладём плашмя (самая тонкая сторона — вверх).</param>
        public static GameObject Attach(GameObject model, Transform owner, Vector3 center, Vector3 size, bool keepProportions = false,
                                        Quaternion? preRotation = null, bool flat = false)
        {
            Quaternion pre = preRotation ?? Quaternion.identity;
            // Контейнер: поворот и масштаб подгонки — на нём, а у самой модели остаётся
            // свой импортный поворот (у моделей из Blender ось Z вверх) и масштаб.
            var go = new GameObject("Модель");
            go.transform.SetParent(owner, false);
            // Выравнивание (поворот на произвольный угол) — на отдельном узле внутри: на контейнере
            // только повороты на 90° и масштаб, иначе неравномерный масштаб перекосил бы модель.
            var align = new GameObject("Выравнивание").transform;
            align.SetParent(go.transform, false);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model, align);
            inst.transform.localPosition = Vector3.zero;

            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            // Сначала заданный поворот (стол — вверх ногами, шкаф — на спину).
            go.transform.localRotation = pre;
            // Модели из Tripo часто повёрнуты в файле вокруг вертикали (диван на 54°, бензопила на 75°):
            // рамка раздувается, и модель сжимается при подгонке. Ищем поворот с самой узкой рамкой.
            // Берём его, только если рамка заметно ужалась — у ровных моделей ничего не меняется.
            bool squared = false;
            {
                // Вертикаль владельца в осях узла выравнивания (контейнер уже повёрнут на pre).
                Vector3 up = Quaternion.Inverse(pre) * Vector3.up;
                float area0 = 0f, bestArea = float.MaxValue;
                Quaternion best = Quaternion.identity;
                for (int a = 0; a < 90; a += 3)
                {
                    align.localRotation = Quaternion.AngleAxis(a, up);
                    var bb = VertexBounds(go, owner);
                    float area = bb.size.x * bb.size.z;
                    if (a == 0) area0 = area;
                    if (area < bestArea - 1e-6f) { bestArea = area; best = align.localRotation; }
                }
                squared = bestArea < area0 * 0.95f;
                align.localRotation = squared ? best : Quaternion.identity;
            }
            if (flat)
            {
                // Самую тонкую сторону — вверх (кладём плашмя).
                var s0 = VertexBounds(go, owner).size;
                if (s0.z < s0.y && s0.z <= s0.x) pre = Quaternion.Euler(90f, 0f, 0f) * pre;
                else if (s0.x < s0.y && s0.x < s0.z) pre = Quaternion.Euler(0f, 0f, 90f) * pre;
                go.transform.localRotation = pre;
            }
            // У повёрнутой в файле модели рамка меша неточная — меряем по вершинам.
            System.Func<Bounds> measure = flat || squared ? () => VertexBounds(go, owner) : () => LocalBounds(go, owner);
            Bounds b = measure();
            // Длинная сторона модели — по X, а нужно по Z: разворачиваем на 90°.
            if ((b.size.x > b.size.z) != (size.x > size.z))
            {
                go.transform.localRotation = Quaternion.Euler(0f, 90f, 0f) * pre;
                b = measure();
            }

            Vector3 k = new Vector3(Safe(size.x, b.size.x), Safe(size.y, b.size.y), Safe(size.z, b.size.z));
            if (keepProportions) k = Vector3.one * Mathf.Min(k.x, k.y, k.z);

            // Масштаб задаём в осях owner: после поворота оси модели могли поменяться местами.
            Vector3 modelAxesScale = Quaternion.Inverse(go.transform.localRotation) * k;
            go.transform.localScale = new Vector3(Mathf.Abs(modelAxesScale.x), Mathf.Abs(modelAxesScale.y), Mathf.Abs(modelAxesScale.z));

            b = measure();
            go.transform.localPosition += center - b.center;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = LookBuilder.ConvertMaterial(mats[i]);
                r.sharedMaterials = mats;
            }
            var texFolder = ExtractTextures(model);
            if (texFolder != null) FixMissingTexture(go, texFolder);
            return go;
        }

        /// <summary>
        /// Колесо: модель внутрь вращающейся части. Самую тонкую ось модели кладём вдоль X (ось колеса),
        /// масштаб — по диаметру, пропорции сохраняются. Центр модели — в центре колеса.
        /// </summary>
        public static GameObject AttachWheel(GameObject model, Transform pivot, float diameter)
        {
            var go = new GameObject("Модель");
            go.transform.SetParent(pivot, false);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model, go.transform);
            inst.transform.localPosition = Vector3.zero;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            // Ось колеса — по вершинам: направление, вдоль которого колесо самое тонкое (наименьший разброс точек).
            // Работает и для колёс, повёрнутых в файле под любым углом (иллюминатор пришёл под ~50°).
            // Почти совпала с осью рамки — берём ровно её, чтобы не было лишнего перекоса на градус.
            Vector3 axis = ThinnestAxis(go);
            Vector3 a = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
            if (a.x > 0.985f) axis = new Vector3(Mathf.Sign(axis.x), 0f, 0f);
            else if (a.y > 0.985f) axis = new Vector3(0f, Mathf.Sign(axis.y), 0f);
            else if (a.z > 0.985f) axis = new Vector3(0f, 0f, Mathf.Sign(axis.z));
            go.transform.localRotation = Quaternion.FromToRotation(axis, Vector3.right);
            Bounds b = VertexBounds(go, pivot);
            float k = Safe(diameter, Mathf.Max(b.size.y, b.size.z));
            go.transform.localScale = Vector3.one * k;
            b = VertexBounds(go, pivot);
            go.transform.localPosition -= b.center;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = LookBuilder.ConvertMaterial(mats[i]);
                r.sharedMaterials = mats;
            }
            var texFolder = ExtractTextures(model);
            if (texFolder != null) FixMissingTexture(go, texFolder);
            return go;
        }

        /// <summary>
        /// Направление наименьшего разброса вершин (в координатах go): у диска или шины это ось.
        /// Ковариация точек, затем степенной метод для (след·I − C) — его главный вектор и есть самый «тонкий».
        /// </summary>
        static Vector3 ThinnestAxis(GameObject go)
        {
            var pts = new System.Collections.Generic.List<Vector3>();
            var verts = new System.Collections.Generic.List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = go.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                mf.sharedMesh.GetVertices(verts);
                foreach (var v in verts) pts.Add(m.MultiplyPoint3x4(v));
            }
            if (pts.Count < 3) return Vector3.right;
            Vector3 mean = Vector3.zero;
            foreach (var p in pts) mean += p;
            mean /= pts.Count;
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p0 in pts)
            {
                Vector3 p = p0 - mean;
                xx += p.x * p.x; xy += p.x * p.y; xz += p.x * p.z;
                yy += p.y * p.y; yz += p.y * p.z; zz += p.z * p.z;
            }
            float tr = xx + yy + zz;
            // M = tr·I − C: самый большой собственный вектор M = самый маленький у C.
            Vector3 d = new Vector3(0.577f, 0.577f, 0.577f);
            for (int i = 0; i < 100; i++)
            {
                Vector3 n = new Vector3((tr - xx) * d.x - xy * d.y - xz * d.z,
                                        -xy * d.x + (tr - yy) * d.y - yz * d.z,
                                        -xz * d.x - yz * d.y + (tr - zz) * d.z);
                if (n.sqrMagnitude < 1e-12f) break;
                d = n.normalized;
            }
            return d;
        }

        /// <summary>Все модели в папке (.prefab и .fbx), по алфавиту.</summary>
        public static System.Collections.Generic.List<GameObject> FindAll(string folder)
        {
            var result = new System.Collections.Generic.List<GameObject>();
            if (!AssetDatabase.IsValidFolder(folder)) return result;
            var guids = new System.Collections.Generic.List<string>(AssetDatabase.FindAssets("t:Prefab", new[] { folder }));
            guids.AddRange(AssetDatabase.FindAssets("t:Model", new[] { folder }));
            guids.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GUIDToAssetPath(a), AssetDatabase.GUIDToAssetPath(b)));
            foreach (var g in guids)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g));
                if (go != null && !result.Contains(go)) result.Add(go);
            }
            return result;
        }

        /// <summary>Убирает видимые примитивы самой детали (коллайдеры остаются — физика прежняя).</summary>
        public static void StripOwnRenderers(Part part)
        {
            foreach (var r in part.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.GetComponentInParent<Part>() != part) continue;
                var mf = r.GetComponent<MeshFilter>();
                Object.DestroyImmediate(r);
                if (mf != null) Object.DestroyImmediate(mf);
            }
        }

        static float Safe(float want, float have) => have > 1e-4f ? want / have : 1f;

        /// <summary>Габариты всех мешей объекта в координатах owner.</summary>
        /// <summary>
        /// Рамка по самим вершинам. Рамка меша (mesh.bounds) у модели, повёрнутой внутри файла под углом,
        /// раздувается: люк выходил мельче коллайдера и не доставал до борта.
        /// </summary>
        public static Bounds VertexBounds(GameObject go, Transform owner)
        {
            bool any = false;
            var result = new Bounds();
            var verts = new System.Collections.Generic.List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = owner.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                mf.sharedMesh.GetVertices(verts);
                foreach (var v in verts)
                {
                    Vector3 p = m.MultiplyPoint3x4(v);
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                    else result.Encapsulate(p);
                }
            }
            return any ? result : LocalBounds(go, owner);
        }

        static Bounds LocalBounds(GameObject go, Transform owner)
        {
            bool any = false;
            var result = new Bounds();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                Bounds mb;
                if (r is SkinnedMeshRenderer smr) mb = smr.localBounds;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    mb = mf.sharedMesh.bounds;
                }
                Vector3 min = mb.min, max = mb.max;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                    Vector3 p = owner.InverseTransformPoint(r.transform.TransformPoint(corner));
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                    else result.Encapsulate(p);
                }
            }
            return result;
        }

        /// <summary>Невидимая коробка-коллайдер (физика корпуса).</summary>
        public static void ColliderBox(string name, Transform owner, Vector3 localPos, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(owner, false);
            go.transform.localPosition = localPos;
            go.AddComponent<BoxCollider>().size = size;
        }
    }
}
