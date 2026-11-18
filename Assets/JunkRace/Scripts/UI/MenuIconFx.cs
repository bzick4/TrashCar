using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Анимированные иконки справа от кнопок главного меню.
    ///   • Колесо («Соло»): наведение — крутится; нажатие — разгоняется, сплющивается в овал, из-под него летит грязь.
    ///   • Куча («Кооп»): наведение — по одной вылетают детали; нажатие — выбрасывает много деталей и оседает.
    ///
    /// Состояния иконок (раскрутка, сплющивание, тряска, «дыхание», оседание, задержка перед переходом)
    /// ведёт DOTween — твины крутят обычные float-поля, а IMGUI их просто рисует.
    /// Летящие частицы (грязь, детали, пыль) — простой цикл с гравитацией: так дешевле, чем твин на каждую.
    ///
    /// Все твины SetUpdate(true) — идут и при timeScale = 0. Картинки — Resources/UI/Anim;
    /// если их нет, Available = false и меню работает без анимаций.
    /// </summary>
    public sealed class MenuIconFx : IDisposable
    {
        // ======================= Настройки =======================

        const float HoverSpin = 420f;          // град/с при наведении на «Соло»
        const float LaunchSpin = 1900f;        // град/с в разгоне
        const float WheelLaunchTime = 0.8f;    // через сколько после нажатия запускаем соло
        const float PileLaunchTime = 0.85f;    // через сколько открываем список серверов
        const int PileBurstParts = 16;

        // ======================= Частицы =======================

        sealed class Particle
        {
            public Texture2D Tex;
            public Vector2 Pos, Vel;
            public float Rot, Spin, Size, Grow, Gravity, Life, MaxLife;
            public bool Behind;   // рисовать за иконкой (пыль)
        }

        readonly List<Particle> particles = new List<Particle>();

        readonly Texture2D wheelTex, pileTex, backdrop;
        readonly List<Texture2D> parts = new List<Texture2D>();
        readonly List<Texture2D> mud = new List<Texture2D>();
        readonly List<Texture2D> dust = new List<Texture2D>();

        public bool Available { get; }

        public MenuIconFx()
        {
            wheelTex = Resources.Load<Texture2D>("UI/Anim/wheel");
            pileTex = Resources.Load<Texture2D>("UI/Anim/pile");
            LoadSeries("part_", parts);
            LoadSeries("mud_", mud);
            LoadSeries("dust_", dust);
            Available = wheelTex != null && pileTex != null && parts.Count > 0 && mud.Count > 0 && dust.Count > 0;
            if (Available) backdrop = MakeBackdrop();
        }

        static void LoadSeries(string prefix, List<Texture2D> into)
        {
            for (int i = 0; i < 16; i++)
            {
                var t = Resources.Load<Texture2D>("UI/Anim/" + prefix + i);
                if (t == null) break;
                into.Add(t);
            }
        }

        /// <summary>Мягкое тёмное пятно под иконкой, чтобы она читалась поверх пёстрого фона.</summary>
        static Texture2D MakeBackdrop()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - d) / 0.35f));   // плотная середина, мягкий край
                t.SetPixel(x, y, new Color(0.09f, 0.07f, 0.06f, a * 0.72f));
            }
            t.Apply();
            return t;
        }

        // ======================= Состояние (его крутят твины) =======================

        // Колесо
        float wheelAngle;        // интегрируется каждый кадр по wheelSpeed
        float wheelSpeed;        // твин
        float wheelSquash;       // твин, 0 — круг, 1 — овал
        Vector3 wheelShake;      // твин (DOTween.Shake)
        bool wheelLaunch;
        float mudTimer, wheelDustTimer;

        // Куча
        float pileScaleY = 1f;   // твин; X считается из Y, чтобы объём «сохранялся»
        float pileBreath;        // твин-петля при наведении, -1..1
        bool pileLaunch;
        float pileSpawnTimer;

        // Где рисуем (обновляется из OnGUI).
        Vector2 wheelCenter, pileCenter;
        // Размеры — от высоты кнопки (ставятся в Draw): иконка сидит на правом краю кнопки.
        float WheelSize = 120f;
        Vector2 PileSize = new Vector2(150f, 93f);

        public bool Busy => wheelLaunch || pileLaunch;

        // Id твинов, чтобы убивать их по группам.
        readonly object wheelId = new object();
        readonly object pileId = new object();

        // ======================= Наведение =======================

        bool wheelHovered;
        public bool WheelHovered
        {
            get => wheelHovered;
            set
            {
                if (value == wheelHovered || wheelLaunch) return;
                wheelHovered = value;
                DOTween.Kill(wheelId);
                // Раскрутка быстрее, чем остановка: на «отпустил» колесо ещё катится по инерции.
                TweenFloat(() => wheelSpeed, v => wheelSpeed = v, value ? HoverSpin : 0f, value ? 0.45f : 0.9f)
                    .SetEase(value ? Ease.OutQuad : Ease.OutCubic).SetId(wheelId);
            }
        }

        bool pileHovered;
        public bool PileHovered
        {
            get => pileHovered;
            set
            {
                if (value == pileHovered || pileLaunch) return;
                pileHovered = value;
                DOTween.Kill(pileId);
                if (value)
                {
                    pileSpawnTimer = 0.1f;
                    pileBreath = -1f;
                    TweenFloat(() => pileBreath, v => pileBreath = v, 1f, 0.22f)
                        .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetId(pileId);
                }
                else
                {
                    TweenFloat(() => pileBreath, v => pileBreath = v, 0f, 0.2f).SetEase(Ease.OutQuad).SetId(pileId);
                }
            }
        }

        // ======================= Нажатия =======================

        public void LaunchWheel(Action onDone)
        {
            if (!Available || Busy) { onDone?.Invoke(); return; }
            wheelLaunch = true;
            DOTween.Kill(wheelId);

            DOTween.Sequence().SetId(wheelId).SetUpdate(true)
                // разгон и сплющивание одновременно, овал «пружинит» в конце
                .Append(TweenFloat(() => wheelSpeed, v => wheelSpeed = v, LaunchSpin, 0.35f).SetEase(Ease.InQuad))
                .Join(TweenFloat(() => wheelSquash, v => wheelSquash = v, 1f, 0.3f).SetEase(Ease.OutBack))
                .Join(DOTween.Shake(() => wheelShake, v => wheelShake = v, WheelLaunchTime, new Vector3(3f, 2f, 0f), 30, 90f, false))
                .AppendInterval(Mathf.Max(0f, WheelLaunchTime - 0.35f))
                .AppendCallback(() => onDone?.Invoke());
        }

        public void LaunchPile(Action onDone)
        {
            if (!Available || Busy) { onDone?.Invoke(); return; }
            pileLaunch = true;
            DOTween.Kill(pileId);
            pileBreath = 0f;

            DOTween.Sequence().SetId(pileId).SetUpdate(true)
                // вспучилась...
                .Append(TweenFloat(() => pileScaleY, v => pileScaleY = v, 1.18f, 0.1f).SetEase(Ease.OutQuad))
                // ...выплюнула детали...
                .AppendCallback(BurstPile)
                // ...и осела с подпрыгиванием
                .Append(TweenFloat(() => pileScaleY, v => pileScaleY = v, 0.62f, 0.45f).SetEase(Ease.OutBounce))
                .AppendInterval(Mathf.Max(0f, PileLaunchTime - 0.55f))
                .AppendCallback(() => onDone?.Invoke());
        }

        void BurstPile()
        {
            Vector2 top = PileTop();
            for (int i = 0; i < PileBurstParts; i++)
                SpawnPart(top + new Vector2(Rnd(-35f, 35f), Rnd(0f, 20f)),
                          new Vector2(Rnd(-460f, 460f), -Rnd(380f, 780f)), Rnd(30f, 50f), Rnd(1.1f, 1.5f));
            Vector2 baseC = pileCenter + new Vector2(0f, PileSize.y * 0.45f);
            for (int i = 0; i < 6; i++)
                SpawnDust(baseC + new Vector2(Rnd(-PileSize.x * 0.5f, PileSize.x * 0.5f), Rnd(-10f, 5f)),
                          new Vector2(Rnd(-140f, 140f), -Rnd(30f, 90f)), Rnd(60f, 90f), Rnd(0.6f, 0.9f));
        }

        /// <summary>Вернулись на главный экран — всё в исходное.</summary>
        public void Reset()
        {
            DOTween.Kill(wheelId);
            DOTween.Kill(pileId);
            particles.Clear();
            wheelAngle = wheelSpeed = wheelSquash = 0f;
            wheelShake = Vector3.zero;
            pileScaleY = 1f;
            pileBreath = 0f;
            wheelLaunch = pileLaunch = false;
            wheelHovered = pileHovered = false;
        }

        public void Dispose()
        {
            DOTween.Kill(wheelId);
            DOTween.Kill(pileId);
        }

        // ======================= Кадр =======================

        public void Update(float dt)
        {
            if (!Available || dt <= 0f) return;
            dt = Mathf.Min(dt, 0.05f);

            wheelAngle = (wheelAngle + wheelSpeed * dt) % 360f;   // по часовой — «едет» вправо

            if (wheelLaunch) EmitMud(dt);
            if (pileHovered && !pileLaunch) EmitHoverPart(dt);

            for (int i = particles.Count - 1; i >= 0; i--)
            {
                var p = particles[i];
                p.Vel.y += p.Gravity * dt;
                p.Pos += p.Vel * dt;
                p.Rot += p.Spin * dt;
                p.Size = Mathf.Max(1f, p.Size + p.Grow * dt);
                p.Life -= dt;
                if (p.Life <= 0f) particles.RemoveAt(i);
            }
        }

        /// <summary>Грязь и пыль назад-вверх из-под колеса, пока оно в разгоне.</summary>
        void EmitMud(float dt)
        {
            Vector2 under = wheelCenter + new Vector2(-WheelSize * 0.25f, WheelSize * 0.42f);
            float intensity = Mathf.Clamp01(wheelSpeed / LaunchSpin);
            if (intensity < 0.2f) return;

            mudTimer -= dt;
            while (mudTimer <= 0f)
            {
                mudTimer += Mathf.Lerp(0.06f, 0.025f, intensity);
                var p = new Particle
                {
                    Tex = mud[UnityEngine.Random.Range(0, mud.Count)],
                    Pos = under + new Vector2(Rnd(-8f, 8f), Rnd(-6f, 4f)),
                    Vel = new Vector2(-Rnd(260f, 620f), -Rnd(160f, 480f)) * Mathf.Lerp(0.6f, 1f, intensity),
                    Rot = Rnd(0f, 360f),
                    Spin = Rnd(-540f, 540f),
                    Size = Rnd(22f, 44f),
                    Gravity = 1500f,
                    MaxLife = Rnd(0.6f, 0.9f),
                };
                p.Life = p.MaxLife;
                particles.Add(p);
            }

            wheelDustTimer -= dt;
            while (wheelDustTimer <= 0f)
            {
                wheelDustTimer += 0.07f;
                SpawnDust(under + new Vector2(-10f, 6f), new Vector2(-Rnd(90f, 220f), -Rnd(10f, 60f)), Rnd(40f, 60f), Rnd(0.5f, 0.8f));
            }
        }

        /// <summary>Наведение на «Кооп»: по одной детали из кучи.</summary>
        void EmitHoverPart(float dt)
        {
            pileSpawnTimer -= dt;
            if (pileSpawnTimer > 0f) return;
            pileSpawnTimer = Rnd(0.28f, 0.45f);
            SpawnPart(PileTop() + new Vector2(Rnd(-20f, 20f), 0f),
                      new Vector2(Rnd(-170f, 170f), -Rnd(380f, 540f)), Rnd(30f, 42f), Rnd(0.9f, 1.2f));
        }

        Vector2 PileTop() => pileCenter + new Vector2(0f, -PileSize.y * 0.3f);

        void SpawnPart(Vector2 pos, Vector2 vel, float size, float life)
        {
            particles.Add(new Particle
            {
                Tex = parts[UnityEngine.Random.Range(0, parts.Count)],
                Pos = pos, Vel = vel,
                Rot = Rnd(0f, 360f), Spin = Rnd(-420f, 420f),
                Size = size, Gravity = 1300f,
                Life = life, MaxLife = life,
            });
        }

        void SpawnDust(Vector2 pos, Vector2 vel, float size, float life)
        {
            particles.Add(new Particle
            {
                Tex = dust[UnityEngine.Random.Range(0, dust.Count)],
                Pos = pos, Vel = vel,
                Rot = Rnd(-15f, 15f), Spin = Rnd(-30f, 30f),
                Size = size, Grow = 60f, Gravity = -40f,   // пыль слегка поднимается и растёт
                Life = life, MaxLife = life, Behind = true,
            });
        }

        // ======================= Отрисовка =======================

        /// <summary>Рисовать вне BeginArea/BeginGroup, чтобы детали не обрезались краем области.</summary>
        public void Draw(Rect soloButton, Rect coopButton)
        {
            if (!Available || Event.current.type != EventType.Repaint) return;

            // Иконка размером с высоту кнопки, центр — на её правом краю (наполовину заходит на кнопку).
            WheelSize = soloButton.height * 1.0f;
            float pileH = coopButton.height * 0.85f;
            PileSize = new Vector2(pileH * 150f / 93f, pileH);
            wheelCenter = new Vector2(soloButton.xMax - WheelSize * 0.05f, soloButton.center.y);
            pileCenter = new Vector2(coopButton.xMax + PileSize.x * 0.05f, coopButton.center.y);

            GUI.DrawTexture(new Rect(wheelCenter.x - WheelSize * 0.85f, wheelCenter.y - WheelSize * 0.8f, WheelSize * 1.7f, WheelSize * 1.6f), backdrop);
            GUI.DrawTexture(new Rect(pileCenter.x - PileSize.x * 0.8f, pileCenter.y - PileSize.y * 1f, PileSize.x * 1.6f, PileSize.y * 2f), backdrop);

            DrawParticles(true);

            // Колесо: в разгоне — овал (шире и ниже), низ остаётся на месте, плюс тряска.
            float sx = 1f + 0.38f * wheelSquash;
            float sy = 1f - 0.24f * wheelSquash;
            Vector2 wc = wheelCenter + new Vector2(wheelShake.x, WheelSize * (1f - sy) * 0.5f + wheelShake.y);
            DrawSpinningOval(wheelTex, wc, WheelSize, new Vector2(sx, sy), wheelAngle);

            // Куча: «дыхание» при наведении + вспучивание/оседание от нижнего края.
            float py = pileScaleY + pileBreath * 0.03f;
            float px = 1f + (1f - pileScaleY) * 0.45f - pileBreath * 0.015f;
            Vector2 ps = new Vector2(PileSize.x * px, PileSize.y * py);
            float bottom = pileCenter.y + PileSize.y * 0.5f;
            GUI.DrawTexture(new Rect(pileCenter.x - ps.x * 0.5f, bottom - ps.y, ps.x, ps.y), pileTex, ScaleMode.StretchToFill, true);

            DrawParticles(false);
        }

        void DrawParticles(bool behind)
        {
            var color = GUI.color;
            foreach (var p in particles)
            {
                if (p.Behind != behind) continue;
                float a = Mathf.Clamp01(p.Life / Mathf.Min(0.3f, p.MaxLife));
                GUI.color = new Color(color.r, color.g, color.b, color.a * a);
                float aspect = p.Tex.height / (float)p.Tex.width;
                DrawRotated(p.Tex, p.Pos, new Vector2(p.Size, p.Size * aspect), p.Rot);
            }
            GUI.color = color;
        }

        /// <summary>
        /// Поворот вокруг центра. Свою матрицу собираем сами: GUIUtility.RotateAroundPivot
        /// промахивается с центром, когда GUI.matrix масштабирован (а меню масштабируется под 1080p).
        /// </summary>
        static void DrawRotated(Texture2D tex, Vector2 center, Vector2 size, float angle)
        {
            var saved = GUI.matrix;
            GUI.matrix = saved
                * Matrix4x4.TRS(new Vector3(center.x, center.y, 0f), Quaternion.Euler(0f, 0f, angle), Vector3.one)
                * Matrix4x4.TRS(new Vector3(-center.x, -center.y, 0f), Quaternion.identity, Vector3.one);
            GUI.DrawTexture(new Rect(center.x - size.x * 0.5f, center.y - size.y * 0.5f, size.x, size.y), tex, ScaleMode.StretchToFill, true);
            GUI.matrix = saved;
        }

        /// <summary>Колесо: картинка крутится, а сплющивание в овал остаётся горизонтальным.</summary>
        static void DrawSpinningOval(Texture2D tex, Vector2 center, float size, Vector2 scale, float angle)
        {
            var saved = GUI.matrix;
            GUI.matrix = saved
                * Matrix4x4.TRS(new Vector3(center.x, center.y, 0f), Quaternion.identity, new Vector3(scale.x, scale.y, 1f))
                * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, angle), Vector3.one)
                * Matrix4x4.TRS(new Vector3(-center.x, -center.y, 0f), Quaternion.identity, Vector3.one);
            GUI.DrawTexture(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size), tex, ScaleMode.StretchToFill, true);
            GUI.matrix = saved;
        }

        /// <summary>Твин float-поля, не зависящий от timeScale.</summary>
        static Tweener TweenFloat(DG.Tweening.Core.DOGetter<float> getter, DG.Tweening.Core.DOSetter<float> setter, float to, float duration) =>
            DOTween.To(getter, setter, to, duration).SetUpdate(true);

        static float Rnd(float a, float b) => UnityEngine.Random.Range(a, b);
    }
}
