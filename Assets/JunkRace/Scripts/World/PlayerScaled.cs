using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Деталь на свалке есть, только если игроков не меньше MinPlayers. Сцена собрана с запасом на четверых,
    /// при запуске лишнее выключается (YardShuffle) — одинаково у всех, номера деталей для сети не сбиваются.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerScaled : MonoBehaviour
    {
        [Range(1, 4)] public int MinPlayers = 1;
    }
}
