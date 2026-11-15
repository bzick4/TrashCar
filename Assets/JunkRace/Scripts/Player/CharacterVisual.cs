using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Вид персонажа вместо капсулы. Префаб собирает редактор (JunkRace → Игрок: подключить модель)
    /// в Resources/JunkRace/PlayerModel. Один и тот же — для себя и для чужих игроков.
    /// Анимации ведём сами по движению корня, так что работает и у чужих (их позы приходят по сети).
    /// Нет аниматора (модель без скелета) — просто покачиваем при ходьбе.
    /// </summary>
    public class CharacterVisual : MonoBehaviour
    {
        public const string ResourcePath = "JunkRace/PlayerModel";
        /// <summary>Ноги ниже корня игрока: CharacterController 1.7 м с центром в корне.</summary>
        const float FeetOffset = -0.85f;

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int GroundedId = Animator.StringToHash("Grounded");
        static readonly int DrivingId = Animator.StringToHash("Driving");
        static readonly int SittingId = Animator.StringToHash("Sitting");
        static readonly int CarryingId = Animator.StringToHash("Carrying");

        Animator anim;
        bool hasController;
        int carryLayer = -1;
        float carryWeight;
        IAvatar avatar;
        CharacterController cc;
        Transform root;
        Vector3 lastPos;
        float speed, bobPhase;
        Vector3 basePos;

        static GameObject prefab;
        static bool loaded;

        /// <summary>Тёплый «пластилиновый» цвет человечка по умолчанию (соло и хост).</summary>
        public static readonly Color DefaultColor = new Color(1f, 0.78f, 0.45f);

        /// <summary>Цвета игроков: по номеру в сети, одинаково у всех (хост — 0, клиенты — 1, 2, 3…).</summary>
        static readonly Color[] PlayerColors =
        {
            DefaultColor,                       // жёлтый
            new Color(0.25f, 0.55f, 1f),        // синий
            new Color(0.35f, 0.85f, 0.4f),      // зелёный
            new Color(0.85f, 0.35f, 0.85f),     // фиолетовый
        };

        public static Color PlayerColor(ulong clientId) => PlayerColors[(int)(clientId % (ulong)PlayerColors.Length)];

        /// <summary>
        /// Поставить вид на игрока: собранная модель (если есть), иначе человечек из примитивов.
        /// Капсулу-заглушку прячем.
        /// </summary>
        public static CharacterVisual Attach(Transform owner, Color? color = null)
        {
            if (!loaded) { prefab = Resources.Load<GameObject>(ResourcePath); loaded = true; }
            if (owner == null) return null;

            foreach (var name in new[] { "Тело", "Нос" })
            {
                var t = owner.Find(name);
                if (t != null && t.TryGetComponent(out Renderer r)) r.enabled = false;
            }

            var go = prefab != null ? Instantiate(prefab, owner) : BuildDoll(owner, color ?? DefaultColor);
            go.name = "Модель";
            go.transform.localPosition = new Vector3(0f, FeetOffset, 0f);
            go.transform.localRotation = Quaternion.identity;
            var cv = go.GetComponent<CharacterVisual>();
            if (cv == null) cv = go.AddComponent<CharacterVisual>();
            cv.Init(owner);
            return cv;
        }

        void Init(Transform owner)
        {
            root = owner;
            avatar = owner.GetComponent<IAvatar>();
            cc = owner.GetComponent<CharacterController>();
            anim = GetComponentInChildren<Animator>();
            hasController = anim != null && anim.runtimeAnimatorController != null;
            if (hasController)
            {
                anim.applyRootMotion = false;
                carryLayer = anim.GetLayerIndex("Carry");
            }
            lastPos = owner.position;
            basePos = transform.localPosition;
        }

        void LateUpdate()
        {
            if (root == null) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 delta = root.position - lastPos;
            lastPos = root.position;

            bool driving = avatar != null && avatar.Driving != null;
            bool sitting = avatar != null && avatar.Seat != null;
            bool carrying = avatar != null && avatar.Held != null;
            bool seated = driving || sitting;

            delta.y = 0f;
            float target = seated ? 0f : delta.magnitude / dt;
            if (target > 20f) target = 0f;   // телепорт — не бег
            speed = Mathf.Lerp(speed, target, 1f - Mathf.Exp(-10f * dt));

            bool grounded = seated || IsGrounded();

            if (doll != null)
            {
                AnimateDoll(dt, grounded, driving, sitting, carrying);
                return;
            }

            if (hasController)
            {
                anim.SetFloat(SpeedId, speed);
                anim.SetBool(GroundedId, grounded);
                anim.SetBool(DrivingId, driving);
                anim.SetBool(SittingId, sitting);
                anim.SetBool(CarryingId, carrying);
                if (carryLayer >= 0)
                {
                    carryWeight = Mathf.MoveTowards(carryWeight, carrying && !seated ? 1f : 0f, dt * 5f);
                    anim.SetLayerWeight(carryLayer, carryWeight);
                }
                return;
            }

            // Без анимаций: покачивание при ходьбе и лёгкий наклон вперёд.
            float k = Mathf.Clamp01(speed / 4f);
            bobPhase += dt * Mathf.Lerp(0f, 14f, k);
            transform.localPosition = basePos + Vector3.up * (Mathf.Abs(Mathf.Sin(bobPhase)) * 0.06f * k);
            transform.localRotation = Quaternion.Euler(k * 8f, 0f, Mathf.Sin(bobPhase) * 3f * k);
        }

        // ============================ Человечек из примитивов ============================

        /// <summary>Шарниры человечка (поворачиваем их — это и есть анимация).</summary>
        sealed class Doll
        {
            public Transform Body, ArmL, ArmR, LegL, LegR, KneeL, KneeR;
            public float Phase;
            public Vector3 ArmLRot, ArmRRot, LegLRot, LegRRot;   // текущие углы (градусы)
            public float KneeLRot, KneeRRot;
            public float Drop, Lean;
        }

        Doll doll;

        // Размеры, м (рост ~1.73): бёдра на 0.72, плечи на 1.27, центр головы на 1.54.
        const float HipY = 0.72f, ShoulderY = 0.55f, HipX = 0.1f, ShoulderX = 0.25f;

        static GameObject BuildDoll(Transform owner, Color color)
        {
            var root = new GameObject("Модель");
            root.transform.SetParent(owner, false);
            var mat = Material(color);
            var dark = Material(new Color(0.12f, 0.1f, 0.1f));

            var d = new Doll();
            d.Body = Joint("Тело", root.transform, new Vector3(0f, HipY, 0f));
            Part(PrimitiveType.Capsule, "Туловище", d.Body, new Vector3(0f, 0.3f, 0f), new Vector3(0.44f, 0.34f, 0.3f), mat);
            Part(PrimitiveType.Sphere, "Голова", d.Body, new Vector3(0f, 0.82f, 0f), Vector3.one * 0.38f, mat);
            // Глазки — чтобы было видно, куда смотрит.
            Part(PrimitiveType.Sphere, "Глаз", d.Body, new Vector3(-0.07f, 0.86f, 0.17f), new Vector3(0.05f, 0.07f, 0.03f), dark);
            Part(PrimitiveType.Sphere, "Глаз", d.Body, new Vector3(0.07f, 0.86f, 0.17f), new Vector3(0.05f, 0.07f, 0.03f), dark);

            d.ArmL = Joint("Рука_Л", d.Body, new Vector3(-ShoulderX, ShoulderY, 0f));
            d.ArmR = Joint("Рука_П", d.Body, new Vector3(ShoulderX, ShoulderY, 0f));
            foreach (var arm in new[] { d.ArmL, d.ArmR })
                Part(PrimitiveType.Capsule, "Рука", arm, new Vector3(0f, -0.27f, 0f), new Vector3(0.14f, 0.29f, 0.14f), mat);

            d.LegL = Joint("Нога_Л", root.transform, new Vector3(-HipX, HipY, 0f));
            d.LegR = Joint("Нога_П", root.transform, new Vector3(HipX, HipY, 0f));
            // Нога: бедро и голень, колено посередине.
            d.KneeL = Joint("Колено_Л", d.LegL, new Vector3(0f, -0.36f, 0f));
            d.KneeR = Joint("Колено_П", d.LegR, new Vector3(0f, -0.36f, 0f));
            foreach (var leg in new[] { d.LegL, d.LegR })
                Part(PrimitiveType.Capsule, "Бедро", leg, new Vector3(0f, -0.18f, 0f), new Vector3(0.17f, 0.21f, 0.17f), mat);
            foreach (var knee in new[] { d.KneeL, d.KneeR })
                Part(PrimitiveType.Capsule, "Голень", knee, new Vector3(0f, -0.18f, 0f), new Vector3(0.16f, 0.21f, 0.16f), mat);

            // Ноги крепятся к телу, чтобы при посадке опускались вместе с ним.
            d.LegL.SetParent(d.Body, true);
            d.LegR.SetParent(d.Body, true);

            var cv = root.AddComponent<CharacterVisual>();
            cv.doll = d;
            return root;
        }

        static Transform Joint(string name, Transform parent, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static void Part(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            DestroyImmediate(go.GetComponent<Collider>());   // игрок — это CharacterController, части тела ни с чем не сталкиваются
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        static readonly System.Collections.Generic.Dictionary<Color, Material> materials =
            new System.Collections.Generic.Dictionary<Color, Material>();

        static Material Material(Color color)
        {
            if (materials.TryGetValue(color, out var m) && m != null) return m;
            var shader = Shader.Find("Toon Shaders Pro/URP/Toon");
            Color c = color;
            // Этот шейдер умножает на цвет дважды — компенсируем корнем.
            if (shader != null) c = new Color(Mathf.Sqrt(color.r), Mathf.Sqrt(color.g), Mathf.Sqrt(color.b), color.a);
            if (shader == null) shader = Shader.Find("JunkRace/Toon");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(shader) { color = c, name = "Человечек" };
            materials[color] = m;
            return m;
        }

        void AnimateDoll(float dt, bool grounded, bool driving, bool sitting, bool carrying)
        {
            var d = doll;
            bool seated = driving || sitting;
            float walk = Mathf.Clamp01(speed / 2f);   // 0 — стоит, 1 — идёт/бежит
            d.Phase += dt * speed * 2.6f;
            float swing = Mathf.Sin(d.Phase) * 38f * walk;

            // Отрицательный угол по X — конечность вперёд; колено сгибается положительным (голень назад).
            Vector3 legL, legR, armL, armR;
            float kneeL, kneeR;
            float drop = 0f, lean = 0f;
            bool standingDriver = driving && avatar.Driving != null && !avatar.Driving.HasDriverChair;
            if (standingDriver)
            {
                // Кресла нет — стоит за рулём, руки вниз-вперёд к баранке, ноги чуть согнуты.
                legL = new Vector3(-8f, 0f, 4f);
                legR = new Vector3(-8f, 0f, -4f);
                kneeL = kneeR = 16f;
                float steer = Mathf.Sin(Time.time * 3f) * 4f;
                armL = new Vector3(-45f + steer, 0f, 10f);
                armR = new Vector3(-45f - steer, 0f, -10f);
                drop = -0.04f;
            }
            else if (seated)
            {
                drop = -0.3f;                                  // бёдра на сиденье
                legL = new Vector3(-85f, 0f, 3f);              // бёдра вперёд, голени вниз
                legR = new Vector3(-85f, 0f, -3f);
                kneeL = kneeR = 85f;
                if (driving)
                {
                    // Руки на руле, чуть покачиваются, будто рулит.
                    float steer = Mathf.Sin(Time.time * 3f) * 4f;
                    armL = new Vector3(-70f + steer, 0f, 8f);
                    armR = new Vector3(-70f - steer, 0f, -8f);
                }
                else
                {
                    armL = new Vector3(-25f, 0f, 10f);
                    armR = new Vector3(-25f, 0f, -10f);
                }
            }
            else if (!grounded)
            {
                legL = new Vector3(-45f, 0f, 4f);              // ноги поджаты
                legR = new Vector3(-10f, 0f, -4f);
                kneeL = 80f;
                kneeR = 50f;
                armL = new Vector3(0f, 0f, -60f);              // руки в стороны
                armR = new Vector3(0f, 0f, 60f);
            }
            else
            {
                legL = new Vector3(swing, 0f, 2f);
                legR = new Vector3(-swing, 0f, -2f);
                // Колено гнётся, когда нога идёт вперёд (переносим её над землёй).
                float bend = 55f * walk;
                kneeL = Mathf.Max(0f, Mathf.Sin(d.Phase + 1.2f)) * bend;
                kneeR = Mathf.Max(0f, Mathf.Sin(d.Phase + 1.2f + Mathf.PI)) * bend;
                armL = new Vector3(-swing * 0.8f, 0f, -6f);
                armR = new Vector3(swing * 0.8f, 0f, 6f);
                drop = Mathf.Abs(Mathf.Sin(d.Phase)) * 0.04f * walk;
                lean = 6f * walk;
            }
            if (carrying && !seated)
            {
                armL = new Vector3(-80f, 0f, 12f);             // держит перед собой
                armR = new Vector3(-80f, 0f, -12f);
            }

            float k = 1f - Mathf.Exp(-14f * dt);
            d.LegLRot = Vector3.Lerp(d.LegLRot, legL, k);
            d.LegRRot = Vector3.Lerp(d.LegRRot, legR, k);
            d.ArmLRot = Vector3.Lerp(d.ArmLRot, armL, k);
            d.ArmRRot = Vector3.Lerp(d.ArmRRot, armR, k);
            d.KneeLRot = Mathf.Lerp(d.KneeLRot, kneeL, k);
            d.KneeRRot = Mathf.Lerp(d.KneeRRot, kneeR, k);
            d.Drop = Mathf.Lerp(d.Drop, drop, k);
            d.Lean = Mathf.Lerp(d.Lean, lean, k);

            // Ноги висят на теле — наклон тела у них вычитаем, чтобы стояли прямо.
            d.LegL.localRotation = Quaternion.Euler(d.LegLRot - new Vector3(d.Lean, 0f, 0f));
            d.LegR.localRotation = Quaternion.Euler(d.LegRRot - new Vector3(d.Lean, 0f, 0f));
            d.KneeL.localRotation = Quaternion.Euler(d.KneeLRot, 0f, 0f);
            d.KneeR.localRotation = Quaternion.Euler(d.KneeRRot, 0f, 0f);
            d.ArmL.localRotation = Quaternion.Euler(d.ArmLRot);
            d.ArmR.localRotation = Quaternion.Euler(d.ArmRRot);
            d.Body.localPosition = new Vector3(0f, HipY + d.Drop, 0f);
            d.Body.localRotation = Quaternion.Euler(d.Lean, 0f, 0f);
        }

        bool IsGrounded()
        {
            if (cc != null && cc.enabled) return cc.isGrounded;
            // Чужой игрок: луч вниз от ног.
            Vector3 feet = root.position + Vector3.up * (FeetOffset + 0.15f);
            return Physics.Raycast(feet, Vector3.down, 0.35f, ~0, QueryTriggerInteraction.Ignore);
        }
    }
}
