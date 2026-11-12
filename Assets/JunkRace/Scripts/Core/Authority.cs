namespace JunkRace
{
    /// <summary>
    /// Кто принимает решения о физике и поломках.
    /// Офлайн: всегда true (мы сами себе хост).
    /// С Netcode for GameObjects: Authority.Provider = () => NetworkManager.Singleton.IsServer;
    /// Весь остальной код при этом не меняется.
    /// </summary>
    public static class Authority
    {
        public static System.Func<bool> Provider = () => true;
        public static bool IsAuthority => Provider();
    }
}
