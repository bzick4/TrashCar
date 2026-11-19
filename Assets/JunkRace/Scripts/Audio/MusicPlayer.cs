using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using DG.Tweening;

namespace JunkRace
{
    /// <summary>
    /// Музыка по папкам в Resources/Audio/Music: Menu — главное меню, Yard — сборка на свалке, Race — гонка.
    /// Треки в случайном порядке без повторов подряд, плавный переход между треками и при смене экрана.
    /// Пустая папка — берём из любой непустой; музыки нет вовсе — тишина. Живёт между сценами.
    /// Новый трек — плашка «сейчас играет» слева вверху, как в гоночных играх: выезжает, висит и уезжает.
    /// Имя файла «Исполнитель - Название» → две строки; без « - » — только название.
    /// </summary>
    public class MusicPlayer : MonoBehaviour
    {
        public const string Folder = "Audio/Music/";
        const float FadeTime = 1.5f;
        const float BaseVolume = 0.7f;   // музыка не должна забивать мотор

        enum Mood { Menu, Yard, Race }

        static MusicPlayer instance;
        readonly Dictionary<Mood, AudioClip[]> clips = new Dictionary<Mood, AudioClip[]>();
        readonly Dictionary<Mood, List<int>> queue = new Dictionary<Mood, List<int>>();
        AudioSource active, fading;
        float activeWeight, fadingWeight;
        Mood mood;
        bool started;
        AudioClip last;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (instance != null) return;
            var go = new GameObject("Музыка");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MusicPlayer>();
        }

        void Awake()
        {
            useGUILayout = false;   // OnGUI рисует без GUILayout — служебный проход раскладки не нужен
            active = MakeSource();
            fading = MakeSource();
            foreach (Mood m in System.Enum.GetValues(typeof(Mood)))
                clips[m] = Resources.LoadAll<AudioClip>(Folder + m);
        }

        AudioSource MakeSource()
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 0f;
            s.ignoreListenerPause = true;
            s.volume = 0f;
            return s;
        }

        static Mood CurrentMood()
        {
            if (SceneManager.GetActiveScene().name == GameSession.MenuScene) return Mood.Menu;
            var t = RaceTimer.Instance;
            if (t == null) return Mood.Yard;
            switch (t.Phase)
            {
                case MatchPhase.Countdown:
                case MatchPhase.Race:
                case MatchPhase.Results:
                case MatchPhase.RaceLoading:
                    return Mood.Race;
                default:
                    return Mood.Yard;
            }
        }

        void Update()
        {
            var m = CurrentMood();
            if (!started || m != mood)
            {
                started = true;
                mood = m;
                PlayNext();
            }
            else if (active.clip != null && (!active.isPlaying || active.time >= active.clip.length - FadeTime))
                PlayNext();

            float dt = Time.unscaledDeltaTime;
            activeWeight = Mathf.MoveTowards(activeWeight, active.clip != null ? 1f : 0f, dt / FadeTime);
            fadingWeight = Mathf.MoveTowards(fadingWeight, 0f, dt / FadeTime);
            float vol = GameAudio.Music * BaseVolume;
            active.volume = activeWeight * vol;
            fading.volume = fadingWeight * vol;
            if (fadingWeight <= 0f && fading.isPlaying) fading.Stop();
        }

        /// <summary>Следующий трек настроения: текущий уходит в затухание, новый плавно нарастает.</summary>
        void PlayNext()
        {
            var clip = Pick(mood);
            if (clip == null && active.clip == null) return;
            if (clip == active.clip && active.isPlaying && active.time < active.clip.length - FadeTime) return;

            // Меняем местами: играющий — в затухание.
            (active, fading) = (fading, active);
            fadingWeight = activeWeight;
            activeWeight = 0f;
            active.clip = clip;
            if (clip != null) active.Play();
            last = clip;
            if (clip != null) ShowNowPlaying(clip);
        }

        // ============================ Плашка «сейчас играет» ============================
        // Анимация — DOTween: твин крутит npShow (0 — спрятана, 1 — на месте), IMGUI просто рисует по нему.
        // В игре плашка выезжает слева, в главном меню — проявляется на месте поверх меню и гаснет.
        // SetUpdate(true): идёт и на паузе (timeScale = 0).

        const float ShowTime = 0.35f, HideTime = 0.3f, HoldTime = 4f;
        const string TweenId = "music_plate";
        string npTitle, npArtist;
        float npShow;
        static Texture2D plate;
        static bool plateLoaded;
        GUIStyle npTitleStyle, npArtistStyle;

        void ShowNowPlaying(AudioClip clip)
        {
            string n = clip.name.Replace('_', ' ').Trim();
            int dash = n.IndexOf(" - ", System.StringComparison.Ordinal);
            if (dash > 0) { npArtist = n.Substring(0, dash).Trim(); npTitle = n.Substring(dash + 3).Trim(); }
            else { npArtist = null; npTitle = n; }

            DOTween.Kill(TweenId);
            DOTween.Sequence().SetId(TweenId).SetUpdate(true)
                .Append(DOTween.To(() => npShow, v => npShow = v, 1f, ShowTime).SetEase(Ease.OutBack))
                .AppendInterval(HoldTime)
                .Append(DOTween.To(() => npShow, v => npShow = v, 0f, HideTime).SetEase(Ease.InCubic));
        }

        void OnDestroy() => DOTween.Kill(TweenId);

        void OnGUI()
        {
            if (npShow <= 0.001f || string.IsNullOrEmpty(npTitle)) return;
            if (!plateLoaded) { plate = Resources.Load<Texture2D>("UI/hud/music_plate"); plateLoaded = true; }
            if (npTitleStyle == null)
            {
                npTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                npTitleStyle.normal.textColor = new Color(1f, 0.82f, 0.45f);
                npArtistStyle = new GUIStyle(npTitleStyle) { fontSize = 15, fontStyle = FontStyle.Normal };
                npArtistStyle.normal.textColor = new Color(0.9f, 0.86f, 0.78f);
            }

            GUI.depth = -50;   // поверх фона меню и HUD
            float scale = Screen.height / 1080f;
            var m = GUI.matrix;
            var col = GUI.color;
            bool menu = SceneManager.GetActiveScene().name == GameSession.MenuScene;
            const float W = 376f, H = 102f, Alpha = 0.92f;   // −20% от прежнего; чуть прозрачная
            // Как пуш-уведомление: всегда сверху слева, поверх таймера и мест — висит 4 секунды и уходит.
            const float y = 12f;
            Rect rect;
            if (menu)
            {
                // Меню: проявляется на месте с лёгким «пружинящим» увеличением.
                float a = Mathf.Clamp01(npShow);
                GUI.color = new Color(1f, 1f, 1f, Alpha * a);
                float sc = 0.9f + 0.1f * npShow;
                rect = new Rect(16f + W * (1f - sc) / 2f, y + H * (1f - sc) / 2f, W * sc, H * sc);
            }
            else
            {
                // Игра: выезжает слева (OutBack чуть перелетает и возвращается).
                GUI.color = new Color(1f, 1f, 1f, Alpha);
                rect = new Rect(-W + (W + 16f) * npShow, y, W, H);
            }
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            if (plate != null) GUI.DrawTexture(rect, plate, ScaleMode.StretchToFill);
            else GUI.Box(rect, GUIContent.none);
            // Тёмное окно плашки — справа от кассеты.
            var inner = new Rect(rect.x + rect.width * 0.33f, rect.y + rect.height * 0.24f, rect.width * 0.59f, rect.height * 0.56f);   // правее кассеты, с отступом
            if (string.IsNullOrEmpty(npArtist))
                GUI.Label(inner, npTitle, npTitleStyle);
            else
            {
                GUI.Label(new Rect(inner.x, inner.y, inner.width, inner.height * 0.55f), npTitle, npTitleStyle);
                GUI.Label(new Rect(inner.x, inner.y + inner.height * 0.52f, inner.width, inner.height * 0.45f), npArtist, npArtistStyle);
            }
            GUI.color = col;
            GUI.matrix = m;
        }

        AudioClip Pick(Mood m)
        {
            var set = clips[m];
            if (set.Length == 0)
            {
                // Нет музыки под этот экран — из любой непустой папки.
                foreach (var kv in clips) if (kv.Value.Length > 0) { set = kv.Value; m = kv.Key; break; }
                if (set.Length == 0) return null;
            }
            if (!queue.TryGetValue(m, out var q) || q.Count == 0)
            {
                q = new List<int>();
                for (int i = 0; i < set.Length; i++) q.Add(i);
                for (int i = q.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); (q[i], q[j]) = (q[j], q[i]); }
                if (q.Count > 1 && set[q[0]] == last) (q[0], q[1]) = (q[1], q[0]);   // не тот же трек подряд
                queue[m] = q;
            }
            var clip = set[q[0]];
            q.RemoveAt(0);
            return clip;
        }
    }
}
