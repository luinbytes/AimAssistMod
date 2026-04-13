using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AimAssist
{
    /// <summary>
    /// Reflection-based access to Super Battle Golf's GameAssembly.
    /// All game types live in the global namespace.
    /// </summary>
    internal static class GameAccess
    {
        private static bool _initialized;

        // ── Types ──────────────────────────────────────────────────────────────
        private static Type? _playerGolfer;
        private static Type? _playerMovement;
        private static Type? _golfHole;
        private static Type? _lockOnTargetManager;
        private static Type? _lockOnTarget;
        private static Type? _windManager;

        // ── PlayerGolfer (static) ──────────────────────────────────────────────
        private static PropertyInfo? _propLocalPlayerAsGolfer;

        // ── PlayerGolfer (instance) ───────────────────────────────────────────
        private static PropertyInfo? _propIsAimingSwing;
        private static PropertyInfo? _propIsAimingItem;
        private static PropertyInfo? _propIsChargingSwing;
        private static PropertyInfo? _propSwingNormalizedPower;
        private static PropertyInfo? _propSwingPitch;
        private static PropertyInfo? _propMaxSwingPitch;
        private static MethodInfo?   _methodGetSwingHitSpeed;
        private static PropertyInfo? _propMaxSwingHitSpeed;
        private static PropertyInfo? _propMinSwingHitSpeed;
        private static MethodInfo?   _methodReleaseSwingCharge;

        // ── PlayerMovement (static) ───────────────────────────────────────────
        private static PropertyInfo? _propLocalPlayerMovement;

        // ── PlayerMovement (instance) ─────────────────────────────────────────
        private static PropertyInfo? _propMovementYaw;
        private static PropertyInfo? _propYawSpeedDeg;  // deg/s — ApplyRotation() uses this
        private static PropertyInfo? _propMovementPosition;
        private static MethodInfo?   _methodSetYaw;
        private static FieldInfo?    _fieldTargetYaw;

        // ── Pitch control ─────────────────────────────────────────────────────
        private static FieldInfo?    _fieldTargetPitch;
        private static MethodInfo?   _methodSetPitch;

        // ── GolfBall ──────────────────────────────────────────────────────────
        private static Type?         _golfBall;
        private static PropertyInfo? _propOwnBall;   // PlayerGolfer.OwnBall → the player's ball GameObject
        private static PropertyInfo? _propBallPosition;

        // ── WindManager ───────────────────────────────────────────────────────
        private static PropertyInfo? _propCurrentWindDirection;
        private static PropertyInfo? _propCurrentWindSpeed;

        // ── LockOnTargetManager ───────────────────────────────────────────────
        private static FieldInfo?    _fieldActiveTargets;

        // ── Unity FindObject helpers ──────────────────────────────────────────
        private static MethodInfo? _findObjectOfType;
        private static MethodInfo? _findObjectsOfType;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            Assembly? gameAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "GameAssembly") { gameAsm = asm; break; }

            if (gameAsm == null) { Plugin.Log.LogError("[GameAccess] GameAssembly not found!"); return; }

            _playerGolfer        = gameAsm.GetType("PlayerGolfer");
            _playerMovement      = gameAsm.GetType("PlayerMovement");
            _golfHole            = gameAsm.GetType("GolfHole");
            _lockOnTargetManager = gameAsm.GetType("LockOnTargetManager");
            _lockOnTarget        = gameAsm.GetType("LockOnTarget");
            _windManager         = gameAsm.GetType("WindManager");

            const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            const BindingFlags stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // PlayerGolfer static
            _propLocalPlayerAsGolfer = _playerGolfer?.GetProperty("LocalPlayerAsGolfer", stat);

            // PlayerGolfer instance
            _propIsAimingSwing         = _playerGolfer?.GetProperty("IsAimingSwing",         inst);
            _propIsAimingItem          = _playerGolfer?.GetProperty("IsAimingItem",           inst);
            _propIsChargingSwing       = _playerGolfer?.GetProperty("IsChargingSwing",        inst);
            _propSwingNormalizedPower  = _playerGolfer?.GetProperty("SwingNormalizedPower",   inst);
            _propSwingPitch            = _playerGolfer?.GetProperty("SwingPitch",             inst);
            _propMaxSwingPitch         = _playerGolfer?.GetProperty("MaxSwingPitch",          inst);
            _methodGetSwingHitSpeed    = _playerGolfer?.GetMethod  ("GetSwingHitSpeed",       inst);
            _propMaxSwingHitSpeed      = _playerGolfer?.GetProperty("MaxPowerSwingHitSpeed",  inst);
            _propMinSwingHitSpeed      = _playerGolfer?.GetProperty("MinPowerSwingHitSpeed",  inst);
            _methodReleaseSwingCharge  = _playerGolfer?.GetMethod  ("ReleaseSwingCharge",     inst);
            // SetPitch — the real method that sets pitch AND updates the UI
            _methodSetPitch            = _playerGolfer?.GetMethod  ("SetPitch",                inst);
            // OwnBall — the PlayerGolfer's own GolfBall object (for ball position)
            _propOwnBall               = _playerGolfer?.GetProperty("OwnBall",                inst);

            // PlayerMovement
            _propLocalPlayerMovement = _playerMovement?.GetProperty("LocalPlayerMovement", stat);
            _propMovementYaw         = _playerMovement?.GetProperty("Yaw",         inst);
            _propYawSpeedDeg         = _playerMovement?.GetProperty("YawSpeedDeg", inst);
            _propMovementPosition    = _playerMovement?.GetProperty("Position",    inst);
            // Try common method names for setting yaw
            _methodSetYaw = _playerMovement?.GetMethod("SetYaw",          inst)
                         ?? _playerMovement?.GetMethod("SetAimYaw",       inst)
                         ?? _playerMovement?.GetMethod("SetFacingAngle",  inst)
                         ?? _playerMovement?.GetMethod("SetRotation",     inst)
                         ?? _playerMovement?.GetMethod("SetAngle",        inst);
            // targetYaw field — the field ApplyRotation() moves toward each frame
            _fieldTargetYaw = _playerMovement?.GetField("targetYaw", inst);

            // Try to find pitch control fields/methods with common names
            _fieldTargetPitch = _playerGolfer?.GetField("targetPitch",    inst)
                             ?? _playerGolfer?.GetField("pitchAngle",     inst)
                             ?? _playerGolfer?.GetField("aimPitch",       inst)
                             ?? _playerGolfer?.GetField("swingAngle",     inst)
                             ?? _playerGolfer?.GetField("verticalAngle",  inst)
                             ?? _playerGolfer?.GetField("launchAngle",    inst);

            // Ball type and position
            _golfBall         = gameAsm.GetType("GolfBall") ?? gameAsm.GetType("Ball");
            _propBallPosition = _playerGolfer?.GetProperty("BallPosition",           inst)
                             ?? _playerGolfer?.GetProperty("GolfBallPosition",       inst)
                             ?? _playerGolfer?.GetProperty("CurrentBallPosition",    inst);

            // WindManager — look for instance/Instance accessor first, fall back to FindObjectOfType
            _propCurrentWindDirection = _windManager?.GetProperty("CurrentWindDirection", inst | stat);
            _propCurrentWindSpeed     = _windManager?.GetProperty("CurrentWindSpeed",     inst | stat);

            // LockOnTargetManager
            _fieldActiveTargets = _lockOnTargetManager?.GetField("activeTargets", inst);

            // Unity helpers
            _findObjectOfType  = typeof(UnityEngine.Object).GetMethod(
                "FindObjectOfType",  BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Type) }, null);
            _findObjectsOfType = typeof(UnityEngine.Object).GetMethod(
                "FindObjectsOfType", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Type) }, null);

            // Log what we found for easy debugging
            Plugin.Log.LogInfo(
                $"[GameAccess] PlayerGolfer={_playerGolfer != null}  PlayerMovement={_playerMovement != null}" +
                $"  GolfHole={_golfHole != null}  WindManager={_windManager != null}" +
                $"  targetYaw={_fieldTargetYaw != null}  SetPitch={_methodSetPitch != null}" +
                $"  OwnBall={_propOwnBall != null}  GolfBall={_golfBall != null}" +
                $"  IsAimingSwing={_propIsAimingSwing != null}  SwingPitch={_propSwingPitch != null}" +
                $"  GetSwingHitSpeed={_methodGetSwingHitSpeed != null}  MaxSwingHitSpeed={_propMaxSwingHitSpeed != null}");

            // Always dump PlayerGolfer pitch/speed/ball members for diagnostics
            if (_playerGolfer != null)
            {
                var filtered = new System.Collections.Generic.List<string>();
                foreach (var m in _playerGolfer.GetMembers(inst | stat))
                {
                    string n = m.Name.ToLowerInvariant();
                    bool isWritable = m is System.Reflection.PropertyInfo pi ? pi.CanWrite :
                                      m is System.Reflection.FieldInfo fi && !fi.IsInitOnly && !fi.IsLiteral;
                    if (n.Contains("pitch") || n.Contains("angle") || n.Contains("ball") ||
                        n.Contains("speed") || n.Contains("power") || n.Contains("launch") ||
                        n.Contains("vertical"))
                    {
                        string rw = isWritable ? "rw" : "r";
                        filtered.Add($"{m.MemberType}:{m.Name}[{rw}]");
                    }
                }
                Plugin.Log.LogInfo("[GameAccess] PlayerGolfer pitch/speed/ball members: " + string.Join(", ", filtered));
            }

        }

        // ── PlayerGolfer ───────────────────────────────────────────────────────

        public static object? LocalGolfer
        {
            get
            {
                // Try the game's own static accessor first
                var v = _propLocalPlayerAsGolfer?.GetValue(null);
                if (v != null) return v;

                // Mirror fallback: scan all PlayerGolfer instances for isLocalPlayer == true
                if (_findObjectsOfType != null && _playerGolfer != null)
                {
                    var all = _findObjectsOfType.Invoke(null, new object[] { _playerGolfer }) as Array;
                    if (all != null)
                        foreach (var obj in all)
                            if (IsLocalNetworkObject(obj)) return obj;
                }
                return null;
            }
        }

        public static object? LocalMovement
        {
            get
            {
                // Try the game's own static accessor first
                var v = _propLocalPlayerMovement?.GetValue(null);
                if (v != null) return v;

                // Derive from LocalGolfer's GameObject — they share the same object
                var golfer = LocalGolfer;
                if (golfer is Component c && _playerMovement != null)
                {
                    var mv = c.GetComponent(_playerMovement);
                    if (mv != null) return mv;
                }
                return null;
            }
        }

        /// <summary>
        /// Returns true if the object is the local player's NetworkBehaviour
        /// (Mirror: isLocalPlayer == true).
        /// </summary>
        private static bool IsLocalNetworkObject(object obj)
        {
            if (obj == null) return false;
            var prop = obj.GetType().GetProperty("isLocalPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return prop != null && prop.GetValue(obj) is bool b && b;
        }

        public static bool IsAimingSwing(object golfer) =>
            _propIsAimingSwing != null && (bool)(_propIsAimingSwing.GetValue(golfer) ?? false);

        public static bool IsAimingItem(object golfer) =>
            _propIsAimingItem != null && (bool)(_propIsAimingItem.GetValue(golfer) ?? false);

        public static bool IsChargingSwing(object golfer) =>
            _propIsChargingSwing != null && (bool)(_propIsChargingSwing.GetValue(golfer) ?? false);

        /// <summary>
        /// Release the currently-charging swing — fires the shot.
        /// Only call when IsChargingSwing is true.
        /// </summary>
        public static void ReleaseSwingCharge(object golfer) =>
            _methodReleaseSwingCharge?.Invoke(golfer, null);

        public static float GetSwingNormalizedPower(object golfer)
        {
            var val = _propSwingNormalizedPower?.GetValue(golfer);
            return val is float f ? f : 1f;
        }

        public static float GetSwingPitch(object golfer)
        {
            var val = _propSwingPitch?.GetValue(golfer);
            return val is float f ? f : 0f;
        }

        public static float GetMaxSwingPitch(object golfer)
        {
            var val = _propMaxSwingPitch?.GetValue(golfer);
            return val is float f ? Mathf.Max(f, 1f) : 75f;
        }

        public static void SetSwingPitch(object golfer, float pitch)
        {
            // SetPitch method is the correct entry-point — it updates the UI as well
            if (_methodSetPitch != null)
            {
                _methodSetPitch.Invoke(golfer, new object[] { pitch });
                return;
            }
            // Field fallback (no UI update but works functionally)
            if (_fieldTargetPitch != null)
            {
                _fieldTargetPitch.SetValue(golfer, pitch);
                if (_propSwingPitch?.CanWrite == true) _propSwingPitch.SetValue(golfer, pitch);
                return;
            }
            if (_propSwingPitch?.CanWrite == true)
                _propSwingPitch.SetValue(golfer, pitch);
        }

        /// <summary>
        /// Ball launch speed in m/s at the current normalised power level.
        /// Tries GetSwingHitSpeed(power) → MaxPowerSwingHitSpeed → config fallback.
        /// </summary>
        public static float GetMaxSwingHitSpeed(object golfer)
        {
            // Method: GetSwingHitSpeed(float normalizedPower) → float
            if (_methodGetSwingHitSpeed != null)
            {
                var r = _methodGetSwingHitSpeed.Invoke(golfer, new object[] { 1f });
                if (r is float f && f > 0f) return f;
            }
            // Property fallback
            if (_propMaxSwingHitSpeed != null)
            {
                var r = _propMaxSwingHitSpeed.GetValue(golfer);
                if (r is float f && f > 0f) return f;
            }
            return Plugin.BallSpeedFallback.Value;
        }

        public static float GetMinSwingHitSpeed(object golfer)
        {
            // Method: GetSwingHitSpeed(0f) for min speed
            if (_methodGetSwingHitSpeed != null)
            {
                var r = _methodGetSwingHitSpeed.Invoke(golfer, new object[] { 0f });
                if (r is float f && f >= 0f) return f;
            }
            if (_propMinSwingHitSpeed != null)
            {
                var r = _propMinSwingHitSpeed.GetValue(golfer);
                if (r is float f && f >= 0f) return f;
            }
            return GetMaxSwingHitSpeed(golfer) * 0.25f;
        }

        // ── PlayerMovement ─────────────────────────────────────────────────────

        public static Vector3 GetPosition(object movement)
        {
            if (_propMovementPosition != null)
            {
                var v = _propMovementPosition.GetValue(movement);
                if (v is Vector3 vec) return vec;
            }
            if (movement is MonoBehaviour mb) return mb.transform.position;
            if (movement is Component c)      return c.transform.position;
            return Vector3.zero;
        }

        public static float GetYaw(object movement)
        {
            var v = _propMovementYaw?.GetValue(movement);
            return v is float f ? f : 0f;
        }

        /// <summary>
        /// Set the rotation TARGET — lets the game's own ApplyRotation() smoothly
        /// move the body toward it at the speed set by <see cref="SetYawSpeed"/>.
        /// Does NOT snap the current angle; call <see cref="SnapYaw"/> for instant.
        /// </summary>
        public static void SetYaw(object movement, float yaw)
        {
            if (_fieldTargetYaw != null) { _fieldTargetYaw.SetValue(movement, yaw); return; }
            if (_methodSetYaw   != null) { _methodSetYaw.Invoke(movement, new object[] { yaw }); return; }
            _propMovementYaw?.SetValue(movement, yaw);
        }

        /// <summary>
        /// Instantly snap both the current angle and the target to <paramref name="yaw"/>.
        /// Use for speed=0 instant-aim mode.
        /// </summary>
        public static void SnapYaw(object movement, float yaw)
        {
            _fieldTargetYaw?.SetValue(movement, yaw);
            _propMovementYaw?.SetValue(movement, yaw);
        }

        /// <summary>
        /// Set how fast ApplyRotation() moves toward targetYaw (degrees/second).
        /// </summary>
        public static void SetYawSpeed(object movement, float degPerSec)
        {
            _propYawSpeedDeg?.SetValue(movement, degPerSec);
        }

        public static float GetYawSpeed(object movement)
        {
            var v = _propYawSpeedDeg?.GetValue(movement);
            return v is float f ? f : 360f;
        }

        // ── Wind ──────────────────────────────────────────────────────────────

        private static object? _windManagerInstance;
        private static float   _windCacheAge = 99f;
        private static Vector3 _cachedWind;

        public static Vector3 GetWindVector()
        {
            _windCacheAge += Time.deltaTime;
            if (_windCacheAge < 0.5f) return _cachedWind; // refresh every 0.5s
            _windCacheAge = 0f;

            if (_windManager == null) return Vector3.zero;

            // Lazy-find the WindManager instance
            if (_windManagerInstance == null || (_windManagerInstance as UnityEngine.Object) == null)
                _windManagerInstance = _findObjectOfType?.Invoke(null, new object[] { _windManager });

            if (_windManagerInstance == null) return Vector3.zero;

            var dir   = _propCurrentWindDirection?.GetValue(_windManagerInstance);
            var speed = _propCurrentWindSpeed?.GetValue(_windManagerInstance);

            Vector3 dirVec   = dir   is Vector3 dv ? dv     : Vector3.zero;
            float   spd      = speed is float   sv ? sv     : 0f;
            _cachedWind = dirVec.normalized * spd;
            return _cachedWind;
        }

        // ── Hole position ─────────────────────────────────────────────────────

        public static bool TryGetHolePosition(object? golfer, object? movement, out Vector3 holePos)
        {
            holePos = Vector3.zero;

            // Try TargetHole property on PlayerGolfer
            if (golfer != null)
            {
                const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var tHoleProp = _playerGolfer?.GetProperty("TargetHole", inst)
                             ?? _playerGolfer?.GetProperty("targetHole", inst);
                if (tHoleProp != null)
                {
                    var targetHole = tHoleProp.GetValue(golfer);
                    if (targetHole != null)
                    {
                        holePos = GetTransformPosition(targetHole);
                        if (holePos != Vector3.zero) return true;
                    }
                }
            }

            // Fallback: closest GolfHole in the scene
            if (_findObjectsOfType != null && _golfHole != null)
            {
                var holes = _findObjectsOfType.Invoke(null, new object[] { _golfHole }) as Array;
                if (holes != null && holes.Length > 0)
                {
                    var playerPos = movement != null ? GetPosition(movement) : Vector3.zero;
                    float best = float.MaxValue;
                    foreach (var h in holes)
                    {
                        var p = GetTransformPosition(h);
                        if (p == Vector3.zero) continue;
                        float d = Vector3.Distance(playerPos, p);
                        if (d < best) { best = d; holePos = p; }
                    }
                    return holePos != Vector3.zero;
                }
            }
            return false;
        }

        // ── LockOnTargets (weapon assist) ─────────────────────────────────────

        private static object? _lockOnMgrInstance;

        public static List<Vector3> GetLockOnTargetPositions()
        {
            var result = new List<Vector3>();
            if (_lockOnTargetManager == null || _fieldActiveTargets == null) return result;

            if (_lockOnMgrInstance == null || (_lockOnMgrInstance as UnityEngine.Object) == null)
                _lockOnMgrInstance = _findObjectOfType?.Invoke(null, new object[] { _lockOnTargetManager });

            if (_lockOnMgrInstance == null) return result;

            var targets = _fieldActiveTargets.GetValue(_lockOnMgrInstance);
            if (targets == null) return result;

            foreach (var t in (System.Collections.IEnumerable)targets)
            {
                var pos = GetTransformPosition(t);
                if (pos != Vector3.zero) result.Add(pos);
            }
            return result;
        }

        // ── Ball position ─────────────────────────────────────────────────────

        public static Vector3 GetBallPosition(object golfer, object movement)
        {
            // Primary: PlayerGolfer.OwnBall — the player's own golf ball object
            if (_propOwnBall != null)
            {
                var ball = _propOwnBall.GetValue(golfer);
                if (ball != null)
                {
                    var pos = GetTransformPosition(ball);
                    if (pos != Vector3.zero) return pos;
                }
            }

            // Fallback: scan for closest GolfBall in scene (within 5m of player)
            if (_golfBall != null && _findObjectsOfType != null)
            {
                var balls = _findObjectsOfType.Invoke(null, new object[] { _golfBall }) as System.Array;
                if (balls != null)
                {
                    var playerPos = GetPosition(movement);
                    float best = 5f;
                    Vector3 bestPos = Vector3.zero;
                    foreach (var b in balls)
                    {
                        var bPos = GetTransformPosition(b);
                        if (bPos == Vector3.zero) continue;
                        float d = Vector3.Distance(playerPos, bPos);
                        if (d < best) { best = d; bestPos = bPos; }
                    }
                    if (bestPos != Vector3.zero) return bestPos;
                }
            }

            // Fallback: player position
            return GetPosition(movement);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Vector3 GetTransformPosition(object? obj)
        {
            if (obj == null) return Vector3.zero;
            if (obj is Component c)      return c.transform.position;
            if (obj is MonoBehaviour mb) return mb.transform.position;
            var tProp = obj.GetType()
                           .GetProperty("transform",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (tProp?.GetValue(obj) is Transform tf) return tf.position;
            return Vector3.zero;
        }
    }
}
