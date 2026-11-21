using UnityEditor;

namespace JunkRace.EditorTools
{
    /// <summary>Переключатель тестовой трассы: снег → горы с тоннелем → лес с рекой (всегда одна и та же раскладка).</summary>
    static class TestRouteMenu
    {
        const string Path = "JunkRace/Тестовая трасса (снег → горы с тоннелем → река)";

        [MenuItem(Path, false, 200)]
        static void Toggle() => RouteGenerator.TestRoute = !RouteGenerator.TestRoute;

        [MenuItem(Path, true)]
        static bool Validate()
        {
            Menu.SetChecked(Path, RouteGenerator.TestRoute);
            return true;
        }
    }
}
