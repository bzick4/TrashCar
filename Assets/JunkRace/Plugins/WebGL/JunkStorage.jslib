// Запасное хранилище для веб-версии: localStorage живёт на сайте (origin) и переживает
// обновление билда, даже если IndexedDB (там Unity держит PlayerPrefs) недоступна или сбрасывается.
mergeInto(LibraryManager.library, {
    JunkStorageSet: function (key, value) {
        try { localStorage.setItem("junkrace." + UTF8ToString(key), UTF8ToString(value)); return 1; }
        catch (e) { return 0; }
    },
    JunkStorageGet: function (key) {
        var v = null;
        try { v = localStorage.getItem("junkrace." + UTF8ToString(key)); } catch (e) { }
        if (v === null) return 0;
        return stringToNewUTF8(v);
    }
});
