using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Модель игрока из Assets/Prefab/Player:
    ///   • персонаж — FBX со скином (например, с Mixamo: «FBX for Unity», With Skin);
    ///   • анимации — FBX без скина, по имени файла: idle, walk, run, jump, sit, drive, carry.
    /// Собирает Animator и префаб Resources/JunkRace/PlayerModel — его ставят на себя и на чужих (CharacterVisual).
    /// </summary>
    public static class PlayerModelBuilder
    {
        public const string Folder = "Assets/Prefab/Player";
        const string OutFolder = "Assets/JunkRace/Resources/JunkRace";
        const string PrefabPath = OutFolder + "/PlayerModel.prefab";
        const string ControllerPath = OutFolder + "/PlayerAnimator.controller";
        const float Height = 1.75f;

        static readonly string[] Keys = { "idle", "walk", "run", "jump", "sit", "driv", "carry" };

        [MenuItem("JunkRace/Игрок: подключить модель из Assets/Prefab/Player")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets/Prefab", "Player");
                Debug.LogWarning($"JunkRace: создал {Folder} — положи туда персонажа (FBX со скином) и анимации, потом запусти ещё раз.");
                return;
            }

            var paths = AssetDatabase.FindAssets("t:Model", new[] { Folder })
                .Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p).ToList();
            string characterPath = paths.FirstOrDefault(p => HasMesh(p) && Key(p) == null) ?? paths.FirstOrDefault(HasMesh);
            if (characterPath == null)
            {
                Debug.LogWarning($"JunkRace: в {Folder} нет модели персонажа (FBX с мешем).");
                return;
            }

            // 1. Персонаж: гуманоид, если есть скелет.
            var charImporter = (ModelImporter)AssetImporter.GetAtPath(characterPath);
            bool rigged = AssetDatabase.LoadAllAssetsAtPath(characterPath).OfType<SkinnedMeshRenderer>().Any();
            if (rigged && charImporter.animationType != ModelImporterAnimationType.Human)
            {
                charImporter.animationType = ModelImporterAnimationType.Human;
                charImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                charImporter.SaveAndReimport();
            }
            var avatar = AssetDatabase.LoadAllAssetsAtPath(characterPath).OfType<Avatar>().FirstOrDefault();

            // 2. Анимации: тот же скелет (аватар персонажа), петли, без смещения корня.
            var clips = new Dictionary<string, AnimationClip>();
            if (rigged && avatar != null && avatar.isHuman)
            {
                foreach (var p in paths)
                {
                    string key = Key(p);
                    if (key == null) continue;
                    if (p != characterPath) PrepareAnimation(p, avatar, key);
                    var clip = AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>()
                        .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                    if (clip != null && !clips.ContainsKey(key)) clips[key] = clip;
                }
            }

            // 3. Animator и 4. префаб.
            if (!AssetDatabase.IsValidFolder("Assets/JunkRace/Resources")) AssetDatabase.CreateFolder("Assets/JunkRace", "Resources");
            if (!AssetDatabase.IsValidFolder(OutFolder)) AssetDatabase.CreateFolder("Assets/JunkRace/Resources", "JunkRace");
            var controller = clips.Count > 0 ? BuildController(clips) : null;
            BuildPrefab(characterPath, controller);

            string found = clips.Count > 0 ? string.Join(", ", clips.Keys) : "нет (будет покачиваться)";
            Debug.Log($"JunkRace: модель игрока — {System.IO.Path.GetFileName(characterPath)}, анимации: {found}. Префаб: {PrefabPath}");
        }

        static bool HasMesh(string path) =>
            AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Any();

        static string Key(string path)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (n.Contains("sit")) return "sit";   // «Sitting Idle» — это сидя, а не стоя
            foreach (var k in Keys) if (n.Contains(k)) return k;
            return null;
        }

        static void PrepareAnimation(string path, Avatar avatar, string key)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            imp.sourceAvatar = avatar;
            imp.importAnimation = true;
            var src = imp.clipAnimations.Length > 0 ? imp.clipAnimations : imp.defaultClipAnimations;
            foreach (var c in src)
            {
                c.loopTime = key != "jump";
                c.lockRootRotation = true;
                c.lockRootHeightY = true;
                c.lockRootPositionXZ = true;
                c.keepOriginalOrientation = true;
                c.keepOriginalPositionY = true;
                c.keepOriginalPositionXZ = true;
            }
            imp.clipAnimations = src;
            imp.SaveAndReimport();
        }

        static AnimatorController BuildController(Dictionary<string, AnimationClip> clips)
        {
            AssetDatabase.DeleteAsset(ControllerPath);
            var ac = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Driving", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Sitting", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Carrying", AnimatorControllerParameterType.Bool);
            var sm = ac.layers[0].stateMachine;

            AnimationClip Get(params string[] keys)
            {
                foreach (var k in keys) if (clips.TryGetValue(k, out var c)) return c;
                return null;
            }

            // Ходьба: стоит → идёт → бежит (скорость игрока ~5 м/с).
            var move = ac.CreateBlendTreeInController("Ходьба", out var tree, 0);
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            var idle = Get("idle", "walk", "run");
            if (idle != null) tree.AddChild(idle, 0f);
            if (clips.TryGetValue("walk", out var walk)) tree.AddChild(walk, 1.8f);
            if (clips.TryGetValue("run", out var run)) tree.AddChild(run, 5f);
            sm.defaultState = move;

            AnimatorState air = null, sit = null, drive = null;
            if (clips.TryGetValue("jump", out var jump)) { air = sm.AddState("Прыжок"); air.motion = jump; }
            var sitClip = Get("sit", "driv");
            if (sitClip != null) { sit = sm.AddState("Сидит"); sit.motion = sitClip; }
            var driveClip = Get("driv", "sit");
            if (driveClip != null) { drive = sm.AddState("За рулём"); drive.motion = driveClip; }

            void FromAny(AnimatorState to, string param, bool value)
            {
                var t = sm.AddAnyStateTransition(to);
                t.canTransitionToSelf = false;
                t.duration = 0.15f;
                t.hasExitTime = false;
                t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, param);
            }
            void Back(AnimatorState from, string param, bool value)
            {
                var t = from.AddTransition(move);
                t.duration = 0.2f;
                t.hasExitTime = false;
                t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, param);
            }
            if (drive != null) { FromAny(drive, "Driving", true); Back(drive, "Driving", false); }
            if (sit != null) { FromAny(sit, "Sitting", true); Back(sit, "Sitting", false); }
            if (air != null)
            {
                var t = move.AddTransition(air);
                t.duration = 0.1f;
                t.hasExitTime = false;
                t.AddCondition(AnimatorConditionMode.IfNot, 0f, "Grounded");
                Back(air, "Grounded", true);
            }

            // Несёт деталь: руки вперёд поверх ходьбы (верх тела). Вес слоя ставит CharacterVisual.
            if (clips.TryGetValue("carry", out var carry))
            {
                var mask = new AvatarMask { name = "Верх тела" };
                for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
                {
                    var part = (AvatarMaskBodyPart)i;
                    bool upper = part == AvatarMaskBodyPart.Body || part == AvatarMaskBodyPart.Head
                        || part == AvatarMaskBodyPart.LeftArm || part == AvatarMaskBodyPart.RightArm
                        || part == AvatarMaskBodyPart.LeftFingers || part == AvatarMaskBodyPart.RightFingers;
                    mask.SetHumanoidBodyPartActive(part, upper);
                }
                AssetDatabase.AddObjectToAsset(mask, ac);
                ac.AddLayer("Carry");
                var layers = ac.layers;
                var layer = layers[layers.Length - 1];
                layer.avatarMask = mask;
                layer.defaultWeight = 0f;
                var st = layer.stateMachine.AddState("Несёт");
                st.motion = carry;
                layers[layers.Length - 1] = layer;
                ac.layers = layers;
            }
            AssetDatabase.SaveAssets();
            return ac;
        }

        static void BuildPrefab(string characterPath, AnimatorController controller)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(characterPath);
            var root = new GameObject("PlayerModel");
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            // Рост — 1.75 м, ноги на нуле, лицом вперёд (+Z, как у Mixamo).
            var renderers = inst.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                float k = b.size.y > 1e-4f ? Height / b.size.y : 1f;
                inst.transform.localScale *= k;
                b = renderers[0].bounds;
                foreach (var r in renderers) b.Encapsulate(r.bounds);
                inst.transform.position -= new Vector3(b.center.x, b.min.y, b.center.z);
            }

            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = LookBuilder.ConvertMaterial(mats[i]);
                r.sharedMaterials = mats;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = false;
            }
            var texFolder = ModelFitter.ExtractTextures(model);
            if (texFolder != null) ModelFitter.FixMissingTexture(inst, texFolder);

            var anim = inst.GetComponentInChildren<Animator>();
            if (controller != null)
            {
                if (anim == null) anim = inst.AddComponent<Animator>();
                anim.runtimeAnimatorController = controller;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }
            else if (anim != null) anim.runtimeAnimatorController = null;

            root.AddComponent<CharacterVisual>();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
        }

        [MenuItem("JunkRace/Игрок: убрать модель (вернуть капсулу)")]
        static void Remove()
        {
            AssetDatabase.DeleteAsset(PrefabPath);
            AssetDatabase.DeleteAsset(ControllerPath);
            Debug.Log("JunkRace: модель игрока убрана — снова капсула.");
        }
    }
}
