using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Связывает детали сцены с ассетами-описаниями в Assets/JunkRace/Data/Parts/&lt;Вид&gt;/&lt;Название&gt;.asset.
    /// Нет ассета — создаём из значений детали (то, что задано в сборщике). Есть — ассет главный:
    /// его значения переписываются в деталь. Так твой баланс в ассетах переживает пересборку сцены.
    /// </summary>
    public static class PartDefinitions
    {
        const string Root = "Assets/JunkRace/Data/Parts";

        [MenuItem("JunkRace/Детали: привязать описания в открытой сцене")]
        public static void BindOpenScene()
        {
            var (created, bound) = BindAll();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"JunkRace: деталей привязано {bound}, новых описаний создано {created}. Сохрани сцену.");
        }

        public static (int created, int bound) BindAll()
        {
            int created = 0, bound = 0;
            foreach (var part in Object.FindObjectsByType<Part>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (Bind(part, out bool isNew)) bound++;
                if (isNew) created++;
            }
            AssetDatabase.SaveAssets();
            return (created, bound);
        }

        static bool Bind(Part part, out bool isNew)
        {
            isNew = false;
            var partSo = new SerializedObject(part);
            string name = partSo.FindProperty("displayName").stringValue;
            if (string.IsNullOrWhiteSpace(name)) return false;

            Type type = DefinitionType(part);
            string folder = EnsureFolder(FolderName(part));
            string path = $"{folder}/{FileName(name)}.asset";

            var def = AssetDatabase.LoadAssetAtPath<PartDefinition>(path);
            if (def != null && def.GetType() != type)
            {
                Debug.LogWarning($"JunkRace: «{name}» — описание {path} другого вида ({def.GetType().Name}, нужен {type.Name}). Пропускаю.", part);
                return false;
            }

            if (def == null)
            {
                def = (PartDefinition)ScriptableObject.CreateInstance(type);
                CopyByName(partSo, new SerializedObject(def));
                AssetDatabase.CreateAsset(def, path);
                isNew = true;
            }
            else
            {
                CopyByName(new SerializedObject(def), partSo);
                partSo.Update();
            }

            partSo.FindProperty("definition").objectReferenceValue = def;
            partSo.ApplyModifiedPropertiesWithoutUndo();
            if (part.TryGetComponent(out Rigidbody rb)) rb.mass = part.Mass;
            return true;
        }

        /// <summary>Копирует все поля с совпадающими именами (mass, radius, power…).</summary>
        static void CopyByName(SerializedObject src, SerializedObject dst)
        {
            var it = src.GetIterator();
            if (!it.NextVisible(true)) return;
            do
            {
                if (it.name == "m_Script") continue;
                var d = dst.FindProperty(it.name);
                if (d != null && d.propertyType == it.propertyType) dst.CopyFromSerializedProperty(it);
            } while (it.NextVisible(false));
            dst.ApplyModifiedPropertiesWithoutUndo();
        }

        static Type DefinitionType(Part p) => p switch
        {
            WheelPart => typeof(WheelDefinition),
            EnginePart => typeof(EngineDefinition),
            SteeringPart => typeof(SteeringDefinition),
            ThrusterPart => typeof(ThrusterDefinition),
            UmbrellaPart => typeof(UmbrellaDefinition),
            _ => typeof(PartDefinition),
        };

        static string FolderName(Part p) => p switch
        {
            WheelPart => "Колёса",
            EnginePart => "Моторы",
            ThrusterPart => "Толкатели",
            SteeringPart => "Рули",
            SeatPart => "Сиденья",
            _ when p.Category == PartCategory.Hull => "Корпуса",
            _ => "Разное",
        };

        static string FileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(':', '_').Replace('*', '_').Replace('?', '_').Replace('"', '_').Trim();
        }

        static string EnsureFolder(string sub)
        {
            string cur = "Assets";
            foreach (var piece in (Root.Substring("Assets/".Length) + "/" + sub).Split('/'))
            {
                string next = cur + "/" + piece;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, piece);
                cur = next;
            }
            return cur;
        }
    }
}
