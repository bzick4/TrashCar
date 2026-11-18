# JunkRace — все доработки одним архивом

Распакуй поверх проекта (папка `Assets` совпадает). Сделано от версии TrashCar из GitHub от 17.11.

## Порядок
1. **Поставь DOTween** (нужен для анимаций меню):
   Asset Store → «DOTween (HOTween v2)» (бесплатный) → Package Manager → My Assets → Import →
   в окне DOTween Utility Panel нажми **Setup DOTween...** → Apply.
2. Распакуй архив поверх проекта, дождись компиляции.
3. **JunkRace → Собрать всё**. Play.

## Что внутри
### Меню
- Арт: кнопки (4 состояния), панель, строки, поле ввода, бирка, иконки, логотип, фон — `Resources/UI`.
- Анимированные иконки: колесо у «Соло», куча у «Кооп» (DOTween) — `Resources/UI/Anim`, `Scripts/UI/MenuIconFx.cs`.
- Заменяются: `Scripts/UI/MainMenu.cs`, `Scripts/UI/InGameMenu.cs`. Новое: `Editor/MenuUiImporter.cs`.

### Атмосфера (в духе Over the Hill)
- Дымка постэффектом, небо-градиент, рельеф без текстур — `Shaders/`.
- `Scripts/World/Atmosphere/` — палитра с пресетами и холмы-задник с лесом.
- Меню редактора: **JunkRace → Атмосфера: применить к открытой сцене**.
- Заменяются: `Scripts/World/Route/RouteGenerator.cs` (3 вставки), `Editor/TestSceneBuilder.cs` (1 строка).

Подробности и что крутить — `MENU_ART_README.md` и `ATMOSPHERE_README.md`.
