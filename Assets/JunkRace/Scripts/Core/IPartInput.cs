namespace JunkRace
{
    /// <summary>
    /// «Кто управляет»: откуда деталь берёт нажатия.
    /// Игрок, бот, автораскладка, позже — сетевой игрок. Детали всё равно.
    /// </summary>
    public interface IPartInput
    {
        float Throttle { get; }   // -1..1, газ/тормоз
        float Steer { get; }      // -1..1, руль
        bool Special { get; }     // спец-кнопка (зонт, пушка, клаксон)
    }

    /// <summary>Никто не управляет: всё по нулям.</summary>
    public sealed class NullPartInput : IPartInput
    {
        public static readonly NullPartInput Instance = new NullPartInput();
        public float Throttle => 0f;
        public float Steer => 0f;
        public bool Special => false;
    }
}
