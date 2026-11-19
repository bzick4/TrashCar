using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Громкость по каналам: общая, музыка, двигатель, эффекты. Хранится в PlayerPrefs, у каждого игрока своя.
    /// «Общая» — это AudioListener.volume (глушит вообще всё), остальные множатся на громкость своих источников.
    /// </summary>
    public static class GameAudio
    {
        public enum Channel { Master, Music, Engine, Effects }

        public static readonly string[] Names = { "Общая", "Музыка", "Двигатель", "Эффекты" };
        static readonly float[] Defaults = { 1f, 0.6f, 0.8f, 0.9f };
        static readonly float[] volume = (float[])Defaults.Clone();
        static bool loaded, dirty;
        const string Prefix = "jr_volume_";

        public static float Master => Get(Channel.Master);
        public static float Music => Get(Channel.Music);
        public static float Engine => Get(Channel.Engine);
        public static float Effects => Get(Channel.Effects);

        public static float Get(Channel c)
        {
            Load();
            return volume[(int)c];
        }

        public static void Set(Channel c, float value)
        {
            Load();
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(volume[(int)c], value)) return;
            volume[(int)c] = value;
            PlayerPrefs.SetFloat(Prefix + c, value);
            dirty = true;
            if (c == Channel.Master) AudioListener.volume = value;
        }

        /// <summary>Записать на диск (не на каждый сдвиг ползунка — когда отпустили).</summary>
        public static void Save()
        {
            if (!dirty) return;
            PlayerPrefs.Save();
            dirty = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            loaded = false;
            Load();
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            for (int i = 0; i < volume.Length; i++)
                volume[i] = PlayerPrefs.GetFloat(Prefix + (Channel)i, Defaults[i]);
            AudioListener.volume = volume[(int)Channel.Master];
        }

        /// <summary>Ползунки громкости для меню (GUILayout). label — стиль подписи.</summary>
        public static void DrawSliders(GUIStyle label, float width)
        {
            for (int i = 0; i < volume.Length; i++)
            {
                var c = (Channel)i;
                float v = Get(c);
                GUILayout.Label($"{Names[i]}: {Mathf.RoundToInt(v * 100f)}%", label);
                Set(c, GUILayout.HorizontalSlider(v, 0f, 1f, GUILayout.Width(width), GUILayout.Height(24f)));
                GUILayout.Space(14f);
            }
            if (Event.current.type == EventType.MouseUp) Save();
        }
    }
}
