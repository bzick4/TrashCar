using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Звук мотора машины: зацикленный звук, высота и громкость — от скорости и газа.
    /// Звук берётся по самому мощному мотору на машине из Resources/Audio/Engine:
    ///   engine_mower (газонокосилка), engine_washer (стиралка), engine_electric (электромотор),
    ///   engine_chainsaw (бензопила), engine — для всех остальных.
    /// Нет ни одного файла — синтезированное «тарахтение», чтобы было слышно уже сейчас.
    /// Звук в пространстве: чужие машины слышно издалека тише. Работает и у клиентов (по скорости).
    /// </summary>
    [RequireComponent(typeof(Vehicle))]
    public class EngineSound : MonoBehaviour
    {
        public const string Folder = "Audio/Engine/";

        Vehicle vehicle;
        AudioSource source;
        EnginePart engine;
        float rev, loud;

        void Awake()
        {
            vehicle = GetComponent<Vehicle>();
            source = gameObject.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 8f;
            source.maxDistance = 90f;
            source.dopplerLevel = 0.3f;
            source.volume = 0f;
            // Мотор меняется только при прикрутке/откручивании — слушаем события машины, а не опрашиваем.
            vehicle.PartAttached += OnPartsChanged;
            vehicle.PartDetached += OnPartsChanged;
            enabled = false;   // пока мотора нет — Update не нужен
        }

        void OnDestroy()
        {
            if (vehicle == null) return;
            vehicle.PartAttached -= OnPartsChanged;
            vehicle.PartDetached -= OnPartsChanged;
        }

        void OnPartsChanged(Part part)
        {
            if (!(part is EnginePart) && !(engine != null && part == engine)) return;
            var best = StrongestEngine();
            if (best == engine) return;
            engine = best;
            source.clip = engine != null ? ClipFor(engine) : null;
            if (source.clip != null) { source.time = Random.Range(0f, source.clip.length * 0.9f); source.Play(); }
            else { source.Stop(); rev = loud = 0f; }
            enabled = source.clip != null;   // нет мотора — нет и покадрового пересчёта
        }

        void Update()
        {

            float dt = Time.deltaTime;
            float speed = Mathf.Abs(vehicle.ForwardSpeed);
            float throttle = vehicle.HasDriver ? Mathf.Abs(vehicle.Input.Throttle) : 0f;
            bool running = vehicle.HasDriver || speed > 0.5f;

            // Обороты: холостые → по газу и скорости. Громкость: тихо на холостых, громко под газом.
            float targetRev = running ? 0.25f + Mathf.Clamp01(speed / 18f) * 0.6f + throttle * 0.25f : 0f;
            rev = Mathf.MoveTowards(rev, targetRev, dt * (targetRev > rev ? 1.6f : 0.9f));
            float targetLoud = running ? 0.35f + throttle * 0.45f + Mathf.Clamp01(speed / 18f) * 0.2f : 0f;
            loud = Mathf.MoveTowards(loud, targetLoud, dt * 2f);

            source.pitch = 0.7f + rev * 1.1f;
            source.volume = loud * GameAudio.Engine;
        }

        EnginePart StrongestEngine()
        {
            EnginePart best = null;
            foreach (var p in vehicle.Parts)
                if (p is EnginePart e && p.GivesPowerIn(p.Slot) && (best == null || e.Power > best.Power)) best = e;
            return best;
        }

        // ---- Звуки ----

        static readonly Dictionary<string, AudioClip> loaded = new Dictionary<string, AudioClip>();
        static AudioClip synth;

        static AudioClip ClipFor(EnginePart e)
        {
            string n = e.DisplayName.ToLowerInvariant();
            string key = n.Contains("газонокос") ? "engine_mower"
                       : n.Contains("стирал") ? "engine_washer"
                       : n.Contains("электро") ? "engine_electric"
                       : n.Contains("пила") ? "engine_chainsaw"
                       : "engine";
            return Load(key) ?? Load("engine") ?? Synth();
        }

        static AudioClip Load(string name)
        {
            if (!loaded.TryGetValue(name, out var clip))
            {
                clip = Resources.Load<AudioClip>(Folder + name);
                loaded[name] = clip;
            }
            return clip;
        }

        /// <summary>
        /// Заглушка: 1 секунда «тарахтения» — низкий гул с хлопками цилиндров. Все частоты целые в герцах,
        /// так что петля без щелчка на стыке.
        /// </summary>
        static AudioClip Synth()
        {
            if (synth != null) return synth;
            const int rate = 44100;
            var data = new float[rate];
            var rnd = new System.Random(7);
            float noise = 0f;
            for (int i = 0; i < rate; i++)
            {
                float t = i / (float)rate;
                float firing = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 24f * t), 6f);   // хлопки 24 раза в секунду
                float hum = Mathf.Sin(2f * Mathf.PI * 48f * t) * 0.5f + Mathf.Sin(2f * Mathf.PI * 96f * t) * 0.25f
                          + Mathf.Sin(2f * Mathf.PI * 144f * t) * 0.12f;
                noise = noise * 0.85f + ((float)rnd.NextDouble() * 2f - 1f) * 0.15f;
                data[i] = (hum * (0.35f + 0.65f * firing) + noise * firing * 0.6f) * 0.5f;
            }
            synth = AudioClip.Create("Мотор (заглушка)", rate, 1, rate, false);
            synth.SetData(data, 0);
            return synth;
        }
    }
}
