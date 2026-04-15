using UnityEngine;

public partial class SuperHackerGolf
{
    // ── E30c: FOV circle overlay ──────────────────────────────────────────────
    //
    // Renders a hollow circle centered on the screen with radius proportional
    // to the configured cone half-angle (weaponAssistConeAngleDeg). The cone
    // angle is relative to the camera forward, so we convert it into screen
    // pixels using the active camera's vertical field of view:
    //
    //     radius_px = tan(cone) / tan(fovY/2) * (screenHeight/2)
    //
    // The circle is drawn via a procedurally-generated ring texture (alpha=1
    // in a narrow annulus, alpha=0 elsewhere) to avoid needing GL/Graphics
    // calls — OnGUI doesn't expose those cleanly and Unity's IMGUI only has
    // DrawTexture. The texture is cached and regenerated only when the
    // resolution or tint changes.

    // User-controllable fields (declared here; referenced by GUI)
    internal bool fovCircleShow = true;
    internal Color fovCircleColor = new Color(0.35f, 0.65f, 1f, 0.75f);  // COL_ACCENT-ish
    internal int fovCircleThicknessPx = 2;

    private Texture2D cachedFovCircleTexture;
    private int cachedFovCircleTextureSize;
    private int cachedFovCircleThickness;
    private Color cachedFovCircleColor;

    private void DrawFovCircleOverlay()
    {
        try
        {
            float coneDeg = Mathf.Max(1f, weaponAssistConeAngleDeg);
            float fovY = GetActiveCameraFovY();
            if (fovY <= 1f || fovY >= 179f) fovY = 60f;

            float tanCone = Mathf.Tan(coneDeg * Mathf.Deg2Rad);
            float tanFov = Mathf.Tan(fovY * 0.5f * Mathf.Deg2Rad);
            if (tanFov < 0.0001f) return;

            float screenH = Screen.height;
            float screenW = Screen.width;
            float radiusPx = (tanCone / tanFov) * (screenH * 0.5f);

            // Clamp so we don't allocate an enormous texture at coneAngle=90°
            // where tan(90°) blows up. Visually: ≥half-screen radius means
            // "the whole view", which we cap to 80% of screen height.
            float maxRadius = screenH * 0.4f;
            bool clamped = false;
            if (radiusPx > maxRadius)
            {
                radiusPx = maxRadius;
                clamped = true;
            }
            if (radiusPx < 4f) radiusPx = 4f;

            int texSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(radiusPx * 2f + 8f)), 32, 1024);
            int thickness = Mathf.Clamp(fovCircleThicknessPx, 1, 8);

            EnsureFovCircleTexture(texSize, thickness, fovCircleColor);
            if (cachedFovCircleTexture == null) return;

            float centerX = screenW * 0.5f;
            float centerY = screenH * 0.5f;

            // Texture ring is inscribed at radius = texSize/2 - thickness.
            // We stretch the rect so the ring lands exactly at radiusPx on screen.
            Rect r = new Rect(
                centerX - radiusPx,
                centerY - radiusPx,
                radiusPx * 2f,
                radiusPx * 2f);
            GUI.DrawTexture(r, cachedFovCircleTexture, ScaleMode.StretchToFill, true);

            // Hint when clamped so user knows cone is effectively unlimited
            if (clamped)
            {
                GUI.Label(
                    new Rect(centerX - 80f, centerY + radiusPx + 4f, 160f, 20f),
                    "<color=#F85149>FOV cone ≥ screen</color>");
            }
        }
        catch
        {
            // never let GUI painting tear down a frame
        }
    }

    private float GetActiveCameraFovY()
    {
        try
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = ResolveGameManagerCamera();
            }
            if (cam != null) return cam.fieldOfView;
        }
        catch { }
        return 60f;
    }

    private void EnsureFovCircleTexture(int size, int thickness, Color color)
    {
        if (cachedFovCircleTexture != null &&
            cachedFovCircleTextureSize == size &&
            cachedFovCircleThickness == thickness &&
            cachedFovCircleColor == color)
        {
            return;
        }

        if (cachedFovCircleTexture != null)
        {
            UnityEngine.Object.Destroy(cachedFovCircleTexture);
            cachedFovCircleTexture = null;
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        int half = size / 2;
        // Inner/outer radius for the ring, in texture pixels
        float outer = half - 1f;
        float inner = outer - thickness;
        float outerSq = outer * outer;
        float innerSq = inner * inner;

        Color[] pixels = new Color[size * size];
        Color clear = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < size; y++)
        {
            int dy = y - half;
            int rowBase = y * size;
            for (int x = 0; x < size; x++)
            {
                int dx = x - half;
                float d2 = dx * dx + dy * dy;
                if (d2 <= outerSq && d2 >= innerSq)
                {
                    // Simple analytic antialiasing — fade alpha in the 1-px
                    // boundary regions.
                    float d = Mathf.Sqrt(d2);
                    float aOuter = Mathf.Clamp01(outer - d);
                    float aInner = Mathf.Clamp01(d - inner);
                    float a = Mathf.Min(aOuter, aInner);
                    pixels[rowBase + x] = new Color(color.r, color.g, color.b, color.a * a);
                }
                else
                {
                    pixels[rowBase + x] = clear;
                }
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(false, false);

        cachedFovCircleTexture = tex;
        cachedFovCircleTextureSize = size;
        cachedFovCircleThickness = thickness;
        cachedFovCircleColor = color;
    }
}
