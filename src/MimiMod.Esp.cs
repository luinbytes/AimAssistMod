using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── E31: ESP overlay ──────────────────────────────────────────────────────
    //
    // Architecture: LateUpdate builds a snapshot of visible targets — one
    // entry per in-frustum LockOnTarget — and OnGUI (gated on Repaint)
    // draws each entry. Splitting the work keeps physics/transform reads out
    // of OnGUI, which is called twice per frame (Layout + Repaint). Without
    // this gate every projection runs twice and bounds lookups get costly.
    //
    // Target discovery piggybacks on the existing weapon-aimbot lockon scan
    // (EnumerateActiveLockOnTargets) so we don't double the cost of walking
    // the scene. Visibility uses frustum cull + single Linecast to target
    // center (cheap, ≤20 targets).
    //
    // Bounds resolution priority (cached per target via ConditionalWeakTable-
    // like pattern — dictionary keyed on instance id):
    //   1. SkinnedMeshRenderer.bounds (encapsulates animated mesh)
    //   2. Renderer[] encapsulated
    //   3. Collider.bounds
    //   4. CharacterController synthetic box
    //   5. Fallback capsule at transform.position + upVector * 0.9m

    internal struct EspEntry
    {
        public Vector3 worldCenter;
        public Rect screenRect;             // top-left origin
        public bool hasRect;
        public Vector2 screenCenter;
        public float distance;
        public float healthPct;             // -1 if unknown
        public string name;
        public Color color;
        public bool visible;
        public bool isPlayer;
        public bool isDummy;
        public bool isMine;
        public bool isGolfCart;
        public bool isProtected;
    }

    // User toggles (surfaced in the VISUALS tab)
    internal bool espEnabled = false;
    internal bool espDrawBox = true;
    internal bool espUseCorners = true;         // cornered box vs full outline
    internal bool espDrawName = true;
    internal bool espDrawDistance = true;
    internal bool espDrawHealthBar = false;     // requires resolved health API
    internal bool espDrawTracer = false;
    internal bool espShowInvisible = true;      // draw through walls in dim color
    internal bool espShowPlayers = true;
    internal bool espShowDummies = true;
    internal bool espShowMines = false;
    internal bool espShowGolfCarts = false;
    internal float espMaxDistance = 200f;

    internal Color espColorVisible = new Color(0.30f, 0.85f, 0.40f, 1.00f);
    internal Color espColorInvisible = new Color(0.95f, 0.40f, 0.40f, 0.70f);
    internal Color espColorDummy = new Color(0.95f, 0.75f, 0.20f, 1.00f);
    internal Color espColorMine = new Color(0.95f, 0.30f, 0.95f, 0.85f);
    internal Color espColorCart = new Color(0.30f, 0.65f, 0.95f, 0.85f);

    // Per-frame snapshot, read-only from OnGUI
    private readonly List<EspEntry> espEntries = new List<EspEntry>(32);
    private Camera espCachedCamera;
    private readonly Plane[] espFrustumPlanes = new Plane[6];

    // Per-target bounds renderer cache. Key = instance id of the
    // LockOnTarget transform root, value = chosen Renderer reference OR
    // a synthetic bounds closure.
    private readonly Dictionary<int, Renderer> espBoundsRendererCache = new Dictionary<int, Renderer>();
    private readonly Dictionary<int, Collider> espBoundsColliderCache = new Dictionary<int, Collider>();

    // Health reflection cache — many games stuff a currentHealth float on
    // PlayerInfo. Resolved lazily and may stay null on this game.
    private bool espHealthResolveAttempted;
    private FieldInfo espCachedCurrentHealthField;
    private FieldInfo espCachedMaxHealthField;
    private PropertyInfo espCachedCurrentHealthProperty;
    private PropertyInfo espCachedMaxHealthProperty;

    // Label style — CreateDynamicFontFromOSFont on first use
    private GUIStyle espCachedLabelStyle;
    private GUIStyle espCachedNameStyle;

    // ── Snapshot build (LateUpdate-time) ─────────────────────────────────────

    internal void TickEspSnapshot()
    {
        if (!espEnabled)
        {
            if (espEntries.Count > 0) espEntries.Clear();
            return;
        }

        espEntries.Clear();

        Camera cam = ResolveEspCamera();
        if (cam == null) return;
        espCachedCamera = cam;

        try
        {
            EnsureWeaponAssistReflectionInitialized();
        }
        catch { return; }

        IEnumerable targets = EnumerateActiveLockOnTargets();
        if (targets == null) return;

        GeometryUtility.CalculateFrustumPlanes(cam, espFrustumPlanes);
        float maxDistSq = espMaxDistance * espMaxDistance;
        Vector3 camPos = cam.transform.position;

        foreach (object targetObj in targets)
        {
            if (targetObj == null) continue;
            Component comp = targetObj as Component;
            if (comp == null) continue;
            Transform t;
            try { t = comp.transform; } catch { continue; }
            if (t == null) continue;

            bool isPlayer = false, isDummy = false, isMine = false, isGolfCart = false;
            try
            {
                if (cachedPlayerInfoTypeForFilter != null)
                    isPlayer = comp.GetComponentInParent(cachedPlayerInfoTypeForFilter) != null;
                if (!isPlayer && cachedTargetDummyType != null)
                    isDummy = comp.GetComponentInParent(cachedTargetDummyType) != null;
                if (!isPlayer && !isDummy && cachedLandmineTypeForFilter != null)
                    isMine = comp.GetComponentInParent(cachedLandmineTypeForFilter) != null;
                if (!isPlayer && !isDummy && !isMine && cachedGolfCartTypeForFilter != null)
                    isGolfCart = comp.GetComponentInParent(cachedGolfCartTypeForFilter) != null;
            }
            catch { }

            if (!isPlayer && !isDummy && !isMine && !isGolfCart) continue;
            if (isPlayer && !espShowPlayers) continue;
            if (isDummy && !espShowDummies) continue;
            if (isMine && !espShowMines) continue;
            if (isGolfCart && !espShowGolfCarts) continue;

            Bounds bounds;
            if (!TryResolveBounds(comp, out bounds))
            {
                // Fallback synthetic capsule
                bounds = new Bounds(t.position + Vector3.up * 0.9f, new Vector3(0.6f, 1.8f, 0.6f));
            }

            Vector3 dPos = bounds.center - camPos;
            float distSq = dPos.sqrMagnitude;
            if (distSq > maxDistSq) continue;

            if (!GeometryUtility.TestPlanesAABB(espFrustumPlanes, bounds)) continue;

            Rect rect;
            bool hasRect = WorldBoundsToScreenRect(bounds, cam, out rect);
            Vector2 center;
            bool ok = TryProjectWorldToGuiPoint(bounds.center, cam, out center);
            if (!hasRect && !ok) continue;

            // Visibility via single linecast to bounds center
            bool visible;
            try
            {
                RaycastHit hit;
                visible = !Physics.Linecast(camPos, bounds.center, out hit, ~0, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                visible = true;
            }
            if (!visible && !espShowInvisible) continue;

            bool isProtected = false;
            try { isProtected = IsTargetProtected(targetObj); } catch { }

            EspEntry entry = new EspEntry
            {
                worldCenter = bounds.center,
                screenRect = rect,
                hasRect = hasRect,
                screenCenter = ok ? center : rect.center,
                distance = Mathf.Sqrt(distSq),
                healthPct = TryResolveHealth(comp, isPlayer),
                name = ResolveEspLabel(comp, isPlayer, isDummy, isMine, isGolfCart),
                color = PickEspColor(isPlayer, isDummy, isMine, isGolfCart, visible, isProtected),
                visible = visible,
                isPlayer = isPlayer,
                isDummy = isDummy,
                isMine = isMine,
                isGolfCart = isGolfCart,
                isProtected = isProtected,
            };
            espEntries.Add(entry);
        }
    }

    // ── OnGUI draw (Repaint-gated) ───────────────────────────────────────────

    internal void DrawEspOverlay()
    {
        if (!espEnabled) return;
        if (espEntries.Count == 0) return;
        if (Event.current.type != EventType.Repaint) return;

        EnsureEspStyles();

        Vector2 screenBottomCenter = new Vector2(Screen.width * 0.5f, Screen.height);

        for (int i = 0; i < espEntries.Count; i++)
        {
            EspEntry e = espEntries[i];
            Color col = e.color;

            if (espDrawBox && e.hasRect && e.screenRect.width > 2f && e.screenRect.height > 2f)
            {
                if (espUseCorners)
                {
                    BoxCorners(e.screenRect, col, 1.5f, 0.25f);
                }
                else
                {
                    BoxOutline(e.screenRect, col, 1.5f);
                }
            }

            float labelX, labelY;
            if (e.hasRect)
            {
                labelX = e.screenRect.center.x;
                labelY = e.screenRect.yMin - 2f;
            }
            else
            {
                labelX = e.screenCenter.x;
                labelY = e.screenCenter.y;
            }

            if (espDrawName && !string.IsNullOrEmpty(e.name))
            {
                LabelAtScreen(new Vector2(labelX, labelY - 14f),
                    e.name, espCachedNameStyle, col, Color.black, TextAnchor.UpperCenter);
            }

            if (espDrawDistance)
            {
                string distStr = e.distance < 10f ? e.distance.ToString("F1") + "m" : ((int)e.distance).ToString() + "m";
                Vector2 pos = e.hasRect
                    ? new Vector2(e.screenRect.center.x, e.screenRect.yMax + 2f)
                    : new Vector2(labelX, labelY + 6f);
                LabelAtScreen(pos, distStr, espCachedLabelStyle, col, Color.black, TextAnchor.UpperCenter);
            }

            if (espDrawHealthBar && e.healthPct >= 0f && e.hasRect)
            {
                Rect barRect = new Rect(e.screenRect.xMin - 5f, e.screenRect.yMin, 3f, e.screenRect.height);
                HealthBar(barRect, e.healthPct, new Color(0f, 0f, 0f, 0.7f));
            }

            if (espDrawTracer)
            {
                Vector2 tracerTarget = e.hasRect ? new Vector2(e.screenRect.center.x, e.screenRect.yMax) : e.screenCenter;
                Line(screenBottomCenter, tracerTarget, col, 1f);
            }

            if (e.isProtected)
            {
                Vector2 shieldPos = e.hasRect
                    ? new Vector2(e.screenRect.xMax + 4f, e.screenRect.yMin)
                    : new Vector2(labelX + 8f, labelY);
                LabelAtScreen(shieldPos, "[PROT]",
                    espCachedLabelStyle, new Color(0.4f, 0.7f, 1f, 1f), Color.black);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Camera ResolveEspCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) cam = ResolveGameManagerCamera();
        return cam;
    }

    private void EnsureEspStyles()
    {
        if (espCachedLabelStyle != null && espCachedNameStyle != null) return;

        Font f = null;
        try
        {
            f = Font.CreateDynamicFontFromOSFont(
                new[] { "Arial", "Liberation Sans", "DejaVu Sans", "Verdana" }, 11);
        }
        catch { }

        espCachedLabelStyle = new GUIStyle
        {
            font = f,
            fontSize = 11,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.UpperLeft,
            richText = false,
            wordWrap = false,
        };
        espCachedLabelStyle.normal.textColor = Color.white;

        espCachedNameStyle = new GUIStyle(espCachedLabelStyle)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
        };
    }

    private bool TryResolveBounds(Component comp, out Bounds bounds)
    {
        bounds = default;
        if (comp == null) return false;
        int id;
        try { id = comp.transform.root.GetInstanceID(); }
        catch { return false; }

        Renderer cached;
        if (espBoundsRendererCache.TryGetValue(id, out cached))
        {
            if (cached != null)
            {
                try
                {
                    bounds = cached.bounds;
                    return true;
                }
                catch { espBoundsRendererCache.Remove(id); }
            }
            else
            {
                espBoundsRendererCache.Remove(id);
            }
        }

        Collider cachedCol;
        if (espBoundsColliderCache.TryGetValue(id, out cachedCol))
        {
            if (cachedCol != null)
            {
                try
                {
                    bounds = cachedCol.bounds;
                    return true;
                }
                catch { espBoundsColliderCache.Remove(id); }
            }
            else
            {
                espBoundsColliderCache.Remove(id);
            }
        }

        try
        {
            SkinnedMeshRenderer smr = comp.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.updateWhenOffscreen = true;
                espBoundsRendererCache[id] = smr;
                bounds = smr.bounds;
                return true;
            }
        }
        catch { }

        try
        {
            Renderer[] rs = comp.GetComponentsInChildren<Renderer>();
            if (rs != null && rs.Length > 0)
            {
                Renderer first = null;
                Bounds acc = default;
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] == null) continue;
                    if (first == null)
                    {
                        first = rs[i];
                        acc = rs[i].bounds;
                    }
                    else
                    {
                        acc.Encapsulate(rs[i].bounds);
                    }
                }
                if (first != null)
                {
                    espBoundsRendererCache[id] = first;
                    bounds = acc;
                    return true;
                }
            }
        }
        catch { }

        try
        {
            Collider c = comp.GetComponentInChildren<Collider>();
            if (c != null)
            {
                espBoundsColliderCache[id] = c;
                bounds = c.bounds;
                return true;
            }
        }
        catch { }

        try
        {
            CharacterController cc = comp.GetComponentInChildren<CharacterController>();
            if (cc != null)
            {
                Vector3 center = cc.transform.position + cc.center;
                bounds = new Bounds(center, new Vector3(cc.radius * 2f, cc.height, cc.radius * 2f));
                return true;
            }
        }
        catch { }

        return false;
    }

    private float TryResolveHealth(Component comp, bool isPlayer)
    {
        if (!isPlayer) return -1f;
        if (!espHealthResolveAttempted)
        {
            espHealthResolveAttempted = true;
            try
            {
                if (cachedPlayerInfoTypeForFilter != null)
                {
                    string[] curNames = { "CurrentHealth", "currentHealth", "Health", "health", "hp" };
                    string[] maxNames = { "MaxHealth", "maxHealth", "HealthMax", "healthMax" };
                    BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    for (int i = 0; i < curNames.Length && espCachedCurrentHealthField == null && espCachedCurrentHealthProperty == null; i++)
                    {
                        espCachedCurrentHealthField = cachedPlayerInfoTypeForFilter.GetField(curNames[i], bf);
                        if (espCachedCurrentHealthField == null)
                            espCachedCurrentHealthProperty = cachedPlayerInfoTypeForFilter.GetProperty(curNames[i], bf);
                    }
                    for (int i = 0; i < maxNames.Length && espCachedMaxHealthField == null && espCachedMaxHealthProperty == null; i++)
                    {
                        espCachedMaxHealthField = cachedPlayerInfoTypeForFilter.GetField(maxNames[i], bf);
                        if (espCachedMaxHealthField == null)
                            espCachedMaxHealthProperty = cachedPlayerInfoTypeForFilter.GetProperty(maxNames[i], bf);
                    }
                }
            }
            catch { }
        }

        if (espCachedCurrentHealthField == null && espCachedCurrentHealthProperty == null) return -1f;

        try
        {
            object playerInfo = null;
            if (cachedPlayerInfoTypeForFilter != null)
            {
                Component pi = comp.GetComponentInParent(cachedPlayerInfoTypeForFilter);
                if (pi != null) playerInfo = pi;
            }
            if (playerInfo == null) return -1f;

            float cur = -1f, max = -1f;
            if (espCachedCurrentHealthField != null)
                cur = Convert.ToSingle(espCachedCurrentHealthField.GetValue(playerInfo));
            else if (espCachedCurrentHealthProperty != null)
                cur = Convert.ToSingle(espCachedCurrentHealthProperty.GetValue(playerInfo, null));

            if (espCachedMaxHealthField != null)
                max = Convert.ToSingle(espCachedMaxHealthField.GetValue(playerInfo));
            else if (espCachedMaxHealthProperty != null)
                max = Convert.ToSingle(espCachedMaxHealthProperty.GetValue(playerInfo, null));

            if (max <= 0f) max = 100f;
            return Mathf.Clamp01(cur / max);
        }
        catch
        {
            return -1f;
        }
    }

    private string ResolveEspLabel(Component comp, bool isPlayer, bool isDummy, bool isMine, bool isGolfCart)
    {
        if (isPlayer) return "PLAYER";
        if (isDummy) return "DUMMY";
        if (isMine) return "MINE";
        if (isGolfCart) return "CART";
        return comp.gameObject != null ? comp.gameObject.name : "?";
    }

    private Color PickEspColor(bool isPlayer, bool isDummy, bool isMine, bool isGolfCart, bool visible, bool isProtected)
    {
        if (isMine) return espColorMine;
        if (isGolfCart) return espColorCart;
        if (isDummy) return espColorDummy;
        if (!visible) return espColorInvisible;
        if (isProtected) return new Color(0.4f, 0.7f, 1f, 1f);
        return espColorVisible;
    }
}
