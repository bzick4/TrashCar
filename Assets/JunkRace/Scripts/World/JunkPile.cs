using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Куча мусора. В ней спрятаны детали (stash): при старте они исчезают, а найти их можно,
    /// порывшись в куче (E, 1–3 секунды). Каждый поиск — бросок: может выпасть деталь, может ничего.
    ///
    /// Сеть: спрятанные детали — обычные детали сцены (свой номер в JunkNet), просто скрыты.
    /// Что выпало, решает хост и рассылает всем. Сама куча нумеруется по порядку в иерархии, как детали.
    ///
    /// Для префаба: любые коллайдеры в детях (по ним целятся), компонент на корне, корень — на земле.
    /// </summary>
    public class JunkPile : MonoBehaviour
    {
        public static readonly List<JunkPile> All = new List<JunkPile>();

        [SerializeField] string displayName = "Куча мусора";
        [Tooltip("Детали, спрятанные в куче. При старте они исчезают и находятся поиском.")]
        [SerializeField] List<Part> stash = new List<Part>();
        [Tooltip("Шанс найти что-то за один поиск (пока в куче что-то есть).")]
        [SerializeField, Range(0f, 1f)] float findChance = 0.6f;
        [Tooltip("Сколько секунд длится поиск (случайно в этих пределах).")]
        [SerializeField] float searchTimeMin = 1f, searchTimeMax = 3f;
        [Tooltip("С какого расстояния от центра кучи (по земле) можно в ней рыться, м.")]
        [SerializeField] float reach = 3f;

        public string DisplayName => displayName;
        public float SearchTimeMin => searchTimeMin;
        public float SearchTimeMax => Mathf.Max(searchTimeMin, searchTimeMax);
        public int NetIndex { get; internal set; } = -1;

        /// <summary>Сколько деталей ещё спрятано.</summary>
        public int Remaining
        {
            get
            {
                int n = 0;
                foreach (var p in stash) if (p != null && p.IsHidden) n++;
                return n;
            }
        }

        float topY;

        /// <summary>Где рисовать подпись над кучей.</summary>
        public Vector3 LabelPosition => new Vector3(transform.position.x, topY + 0.6f, transform.position.z);
        readonly List<Part> candidates = new List<Part>();

        /// <summary>Куда убираем спрятанные детали: глубоко под кучу, чтобы никто их не задел.</summary>
        Vector3 ParkPosition => transform.position + Vector3.down * 30f;

        void Awake()
        {
            bool any = false;
            Bounds b = default;
            foreach (var c in GetComponentsInChildren<Collider>())
            {
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            topY = any ? b.max.y : transform.position.y + 1f;
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        // В Start: к этому моменту у всех деталей уже прошёл Awake. У хоста и клиентов одинаково.
        void Start()
        {
            foreach (var p in stash)
                if (p != null && !p.IsHidden) p.Hide(ParkPosition);
        }

        public bool InReach(Vector3 position, float slack = 0f)
        {
            Vector3 d = position - transform.position;
            d.y = 0f;
            float r = reach + slack;
            return d.sqrMagnitude <= r * r;
        }

        /// <summary>Хост: что нашли за этот поиск (null — ничего).</summary>
        public Part Roll()
        {
            candidates.Clear();
            foreach (var p in stash) if (p != null && p.IsHidden) candidates.Add(p);
            if (candidates.Count == 0 || Random.value > findChance) return null;
            return candidates[Random.Range(0, candidates.Count)];
        }

        /// <summary>Откуда вылетает находка: с верха кучи, со стороны того, кто искал.</summary>
        public Vector3 PopPosition(Vector3 searcher) =>
            new Vector3(transform.position.x, topY + 1f, transform.position.z) + DirTo(searcher) * 0.8f;

        /// <summary>Находка подлетает и падает к ногам искавшего.</summary>
        public Vector3 PopVelocity(Vector3 searcher) => DirTo(searcher) * 2.5f + Vector3.up * 3f;

        Vector3 DirTo(Vector3 searcher)
        {
            Vector3 d = searcher - transform.position;
            d.y = 0f;
            return d.sqrMagnitude > 0.01f ? d.normalized : transform.forward;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, reach);
        }
    }
}
