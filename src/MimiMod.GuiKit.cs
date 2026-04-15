using UnityEngine;

public partial class SuperHackerGolf
{
    // ── E31: GuiKit — shared low-level drawing + style primitives ────────────
    //
    // Used by both the settings window and the ESP overlay. All helpers are
    // safe to call from OnGUI Repaint; none allocate in steady state once
    // textures/styles are warmed up via EnsureGuiKitReady().
    //
    // Philosophy (from the cheat-menu research pass):
    //   1. Pre-bake a 1x1 Texture2D PER color used in the GUI. Never allocate
    //      textures per frame.
    //   2. Draw all rects/lines via GUI.DrawTexture with the cached _white tex
    //      and GUI.color. GL.LINES/CommandBuffer is a trap in MelonMod context.
    //   3. Outlined text = 4 passes + 1 fg pass; there's no cleaner IMGUI path.
    //   4. Line rotation via GUIUtility.RotateAroundPivot; always save/restore
    //      GUI.matrix around the call.

    private static Texture2D _guiKitWhiteTex;
    private bool _guiKitReady;

    internal static Texture2D GuiKitWhite
    {
        get
        {
            if (_guiKitWhiteTex == null)
            {
                _guiKitWhiteTex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                _guiKitWhiteTex.SetPixel(0, 0, Color.white);
                _guiKitWhiteTex.Apply(false, false);
                _guiKitWhiteTex.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_guiKitWhiteTex);
            }
            return _guiKitWhiteTex;
        }
    }

    internal void EnsureGuiKitReady()
    {
        if (_guiKitReady) return;
        _guiKitReady = true;
        // Touch the white tex to force-create on the main thread.
        _ = GuiKitWhite;
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    internal static void FillRect(Rect r, Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, GuiKitWhite);
        GUI.color = prev;
    }

    internal static void BoxOutline(Rect r, Color c, float thickness = 1f)
    {
        FillRect(new Rect(r.x, r.y, r.width, thickness), c);
        FillRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), c);
        FillRect(new Rect(r.x, r.y, thickness, r.height), c);
        FillRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), c);
    }

    // Corner brackets — cleaner than full outline at small sizes
    internal static void BoxCorners(Rect r, Color c, float thickness = 1f, float lenFrac = 0.2f)
    {
        float lh = Mathf.Max(4f, r.width * lenFrac);
        float lv = Mathf.Max(4f, r.height * lenFrac);
        // TL
        FillRect(new Rect(r.x, r.y, lh, thickness), c);
        FillRect(new Rect(r.x, r.y, thickness, lv), c);
        // TR
        FillRect(new Rect(r.xMax - lh, r.y, lh, thickness), c);
        FillRect(new Rect(r.xMax - thickness, r.y, thickness, lv), c);
        // BL
        FillRect(new Rect(r.x, r.yMax - thickness, lh, thickness), c);
        FillRect(new Rect(r.x, r.yMax - lv, thickness, lv), c);
        // BR
        FillRect(new Rect(r.xMax - lh, r.yMax - thickness, lh, thickness), c);
        FillRect(new Rect(r.xMax - thickness, r.yMax - lv, thickness, lv), c);
    }

    internal static void HealthBar(Rect r, float pct01, Color bg)
    {
        pct01 = Mathf.Clamp01(pct01);
        FillRect(r, bg);
        Rect fill = new Rect(r.x + 1f, r.y + 1f, (r.width - 2f) * pct01, r.height - 2f);
        Color fillColor = Color.Lerp(new Color(0.95f, 0.25f, 0.25f, 1f), new Color(0.3f, 0.9f, 0.35f, 1f), pct01);
        FillRect(fill, fillColor);
    }

    internal static void Line(Vector2 a, Vector2 b, Color c, float width = 1f)
    {
        Matrix4x4 prevM = GUI.matrix;
        Color prevC = GUI.color;
        try
        {
            GUI.color = c;
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(ang, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), GuiKitWhite);
        }
        finally
        {
            GUI.matrix = prevM;
            GUI.color = prevC;
        }
    }

    private static readonly Vector2[] _outlineOffsets =
    {
        new Vector2(-1f,  0f),
        new Vector2( 1f,  0f),
        new Vector2( 0f, -1f),
        new Vector2( 0f,  1f),
    };

    internal static void LabelOutlined(Rect r, string text, GUIStyle style, Color fg, Color outline)
    {
        if (style == null || string.IsNullOrEmpty(text)) return;
        Color prev = style.normal.textColor;
        style.normal.textColor = outline;
        for (int i = 0; i < _outlineOffsets.Length; i++)
        {
            Vector2 o = _outlineOffsets[i];
            GUI.Label(new Rect(r.x + o.x, r.y + o.y, r.width, r.height), text, style);
        }
        style.normal.textColor = fg;
        GUI.Label(r, text, style);
        style.normal.textColor = prev;
    }

    // Sizes and text + outline at a screen position. Bypasses GUILayout so
    // callers can place labels anywhere.
    internal static void LabelAtScreen(Vector2 gui, string text, GUIStyle style, Color fg, Color outline, TextAnchor anchor = TextAnchor.UpperLeft)
    {
        if (style == null || string.IsNullOrEmpty(text)) return;
        Vector2 size = style.CalcSize(new GUIContent(text));
        Rect r;
        switch (anchor)
        {
            case TextAnchor.UpperCenter:
                r = new Rect(gui.x - size.x * 0.5f, gui.y, size.x, size.y);
                break;
            case TextAnchor.LowerCenter:
                r = new Rect(gui.x - size.x * 0.5f, gui.y - size.y, size.x, size.y);
                break;
            case TextAnchor.MiddleCenter:
                r = new Rect(gui.x - size.x * 0.5f, gui.y - size.y * 0.5f, size.x, size.y);
                break;
            default:
                r = new Rect(gui.x, gui.y, size.x, size.y);
                break;
        }
        LabelOutlined(r, text, style, fg, outline);
    }

    // ── World-to-screen projection ────────────────────────────────────────────

    // Projects a world-space point into GUI pixel coordinates (top-left
    // origin). Returns false if the point is behind the camera or outside
    // the viewport. For unclamped projection (tracers, off-screen points)
    // use TryProjectWorldToGuiPointUnclamped.
    internal static bool TryProjectWorldToGuiPoint(Vector3 world, Camera cam, out Vector2 gui)
    {
        gui = default;
        if (cam == null) return false;
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0.01f) return false;
        if (sp.x < 0f || sp.x > Screen.width) return false;
        if (sp.y < 0f || sp.y > Screen.height) return false;
        gui = new Vector2(sp.x, Screen.height - sp.y);
        return true;
    }

    internal static bool TryProjectWorldToGuiPointUnclamped(Vector3 world, Camera cam, out Vector2 gui, out bool onScreen)
    {
        gui = default;
        onScreen = false;
        if (cam == null) return false;
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z <= 0.01f) return false;
        gui = new Vector2(sp.x, Screen.height - sp.y);
        onScreen = sp.x >= 0f && sp.x <= Screen.width && sp.y >= 0f && sp.y <= Screen.height;
        return true;
    }

    // Project a world-space AABB's 8 corners and return the screen-space
    // bounding rect. Returns false if any corner is behind the camera
    // (strict culling — callers can fall back to center-point projection).
    private static readonly Vector3[] _boundsCorners = new Vector3[8];
    internal static bool WorldBoundsToScreenRect(Bounds b, Camera cam, out Rect rect)
    {
        rect = default;
        if (cam == null) return false;
        Vector3 c = b.center;
        Vector3 e = b.extents;
        _boundsCorners[0] = c + new Vector3(-e.x, -e.y, -e.z);
        _boundsCorners[1] = c + new Vector3(+e.x, -e.y, -e.z);
        _boundsCorners[2] = c + new Vector3(-e.x, +e.y, -e.z);
        _boundsCorners[3] = c + new Vector3(+e.x, +e.y, -e.z);
        _boundsCorners[4] = c + new Vector3(-e.x, -e.y, +e.z);
        _boundsCorners[5] = c + new Vector3(+e.x, -e.y, +e.z);
        _boundsCorners[6] = c + new Vector3(-e.x, +e.y, +e.z);
        _boundsCorners[7] = c + new Vector3(+e.x, +e.y, +e.z);

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 sp = cam.WorldToScreenPoint(_boundsCorners[i]);
            if (sp.z <= 0.01f) return false;
            float y = Screen.height - sp.y;
            if (sp.x < minX) minX = sp.x;
            if (sp.x > maxX) maxX = sp.x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        rect = new Rect(minX, minY, maxX - minX, maxY - minY);
        return true;
    }
}
