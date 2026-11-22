using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Сохранение прогресса игрока, которое переживает обновление игры.
    /// Пишет в PlayerPrefs и (в браузере) дублирует в localStorage сайта: если одно хранилище
    /// не сработало или сбросилось после нового билда, значение берётся из другого.
    /// </summary>
    public static class Progress
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int JunkStorageSet(string key, string value);
        [DllImport("__Internal")] static extern IntPtr JunkStorageGet(string key);
#endif

        public static bool GetFlag(string key) => Get(key) == "1";
        public static void SetFlag(string key, bool value) => Set(key, value ? "1" : "0");

        public static string Get(string key)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                IntPtr p = JunkStorageGet(key);
                if (p != IntPtr.Zero)
                {
                    string v = Marshal.PtrToStringUTF8(p);
                    Marshal.FreeHGlobal(p);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch (Exception e) { Debug.LogWarning("Progress: localStorage недоступен: " + e.Message); }
#endif
            return PlayerPrefs.GetString(key, null);
        }

        public static void Set(string key, string value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { JunkStorageSet(key, value); }
            catch (Exception e) { Debug.LogWarning("Progress: localStorage недоступен: " + e.Message); }
#endif
            try { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); }
            catch (Exception e) { Debug.LogWarning("Progress: PlayerPrefs не сохранились: " + e.Message); }
        }
    }
}
