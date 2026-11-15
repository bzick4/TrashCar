using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Декорация свалки (старая машина, бочка, автобус…). Каждый заезд ставится заново по сиду (YardShuffle):
    /// мелкие — внутри свалки, крупные (Big) — за забором, как фон.
    /// </summary>
    public class YardProp : MonoBehaviour
    {
        [Tooltip("Радиус «пятна» на земле, м.")]
        public float Radius = 1f;
        [Tooltip("Крупная — ставим за забором, чтобы не загораживала свалку.")]
        public bool Big;
    }
}
