// Платформа в браузере: телефон (сенсор) и Telegram Mini App. Сама настройка Telegram — в шаблоне страницы
// (WebGLTemplates/JunkRace), тут только ответы для игры.
mergeInto(LibraryManager.library, {
    JunkIsTouchDevice: function () {
        try {
            if (window.matchMedia && window.matchMedia("(pointer: coarse)").matches) return 1;
            if (navigator.maxTouchPoints > 1 && /Android|iPhone|iPad|iPod|Mobile|Macintosh/i.test(navigator.userAgent)
                && !(window.matchMedia && window.matchMedia("(pointer: fine)").matches)) return 1;
        } catch (e) { }
        return 0;
    },
    JunkTelegramInit: function () {
        try {
            var tg = window.Telegram && window.Telegram.WebApp;
            if (!tg || !tg.initData) return 0;   // вне Telegram initData пустая
            if (window.junkTelegramSetup) window.junkTelegramSetup();
            return 1;
        } catch (e) { return 0; }
    }
});
