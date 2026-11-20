using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Картинки на весь экран без выцветания. GUI.DrawTexture в Linear color space добавляет лишний перевод
    /// в гамму — картинка выходит светлее и бледнее исходника. Рисуем через материал UI/Default:
    /// цвет совпадает с исходником один в один. Только из OnGUI; прямоугольник — в пикселях экрана
    /// (GUI.matrix не учитывается — рисовать до его смены).
    /// </summary>
    public static class UiDraw
    {
        static Material mat;

        public static void Tex(Rect r, Texture tex) => Tex(r, tex, new Rect(0f, 0f, 1f, 1f));

        public static void Tex(Rect r, Texture tex, Rect uv)
        {
            if (tex == null) return;
            if (mat == null)
            {
                var sh = Shader.Find("UI/Default");
                if (sh == null) { GUI.DrawTextureWithTexCoords(r, tex, uv); return; }
                mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (Event.current.type != EventType.Repaint) return;
            Graphics.DrawTexture(r, tex, uv, 0, 0, 0, 0, Color.white, mat);
        }

        /// <summary>Заполнить прямоугольник с обрезкой по краям (как ScaleMode.ScaleAndCrop).</summary>
        public static void Cover(Rect r, Texture tex)
        {
            if (tex != null) Tex(r, tex, CropUv(r, tex));
        }

        /// <summary>UV для ScaleAndCrop: заполнить прямоугольник, обрезав лишнее по краям.</summary>
        public static Rect CropUv(Rect r, Texture tex)
        {
            float screenAspect = r.width / r.height, texAspect = (float)tex.width / tex.height;
            if (texAspect > screenAspect) { float u = screenAspect / texAspect; return new Rect((1f - u) * 0.5f, 0f, u, 1f); }
            float v = texAspect / screenAspect;
            return new Rect(0f, (1f - v) * 0.5f, 1f, v);
        }
    }
}
