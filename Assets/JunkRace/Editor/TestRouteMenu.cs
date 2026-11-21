using UnityEditor;

namespace JunkRace.EditorTools
{
    /// <summary>Переключатель тестовой трассы: горы с тоннелем → лес с рекой → снег (всегда одна и та же раскладка).</summary>
    static class TestRouteMenu
    {
        const string Path = "JunkRace/Тестовая трасса (брод → мост → тоннель)";

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
