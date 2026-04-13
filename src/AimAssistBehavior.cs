using System.Collections.Generic;
using UnityEngine;

namespace AimAssist
{
    /// <summary>
    /// Core MonoBehaviour — survives scene loads.
    ///
    /// Every feature (yaw assist, pitch solve, auto-fire, weapon assist) is
    /// completely independent. Each reads only its own config entries.
    /// </summary>
    // Run our LateUpdate after all game scripts so body yaw gets the final word.
    // Camera rotation is handled in Camera.onPreRender which fires even later.
    [UnityEngine.DefaultExecutionOrder(int.MaxValue)]
    public class AimAssistBehavior : MonoBehaviour
    {
        // ── Persistent toggle states ───────────────────────────────────────────
        private bool _yawToggleOn   = false;
        private bool _pitchToggleOn = false;
        private bool _hudVisible    = true;

        // ── Cached game references ─────────────────────────────────────────────
        private object?  _localGolfer;
        private object?  _localMovement;
        private bool     _holeFound;
        private Vector3  _holePos;
        private float    _refreshTimer;
        private const float RefreshInterval = 0.75f;

        // ── Prediction state ───────────────────────────────────────────────────
        private List<Vector3>? _predictedPath;
        private float  _predictedLandError = -1f;
        private bool   _pitchSolveValid;
        private float  _solvedPitch;
        private float  _solvedYaw;
        private bool   _aimAligned;      // within AutoFireThreshold
        private float  _neededNormalizedPower = 1f; // charge % at which to release for optimal shot
        private string _rangeStatus = "";

        // Alignment trackers for auto-fire cooldown
        private bool  _firedThisCharge  = false;
        private float _alignedTimeAccum = 0f;
        private const float AlignRequiredTime = 0.05f; // must be aligned for 50ms to prevent flicker-fire

        // ── Camera yaw tracking ────────────────────────────────────────────────
        private bool  _yawAssistWasActive  = false;
        private float _cameraDesiredYaw    = 0f;
        // Our own smooth yaw state for the camera — tracked independently of mouse input
        // so the game's camera controller can't fight us frame-to-frame.
        private float _assistCameraYaw    = 0f;
        private bool  _assistCameraActive = false;

        // Speed logging (once)
        private bool _speedLogged;

        // ── IMGUI resources ────────────────────────────────────────────────────
        private GUIStyle? _labelStyle;
        private GUIStyle? _boxStyle;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void Awake()
        {
            GameAccess.Initialize();
            StartCoroutine(InitCameraRig()); // logs hierarchy for diagnostics
            // Hook into the pre-render event — fires after all Update/LateUpdate,
            // giving us the absolute last word on camera rotation before rendering.
            Camera.onPreRender += OnCameraPreRender;
        }

        private System.Collections.IEnumerator InitCameraRig()
        {
            // Camera.main is null during the main menu / loading — wait until it exists
            float waited = 0f;
            while (Camera.main == null && waited < 60f)
            {
                yield return new WaitForSeconds(1f);
                waited += 1f;
            }
            if (Camera.main == null)
            {
                Plugin.Log.LogWarning("[AimAssist] Camera.main never found after 60s");
                yield break;
            }

            // ── Log hierarchy so we can verify which pivot is correct ─────────────
            var sb = new System.Text.StringBuilder("[AimAssist] Camera hierarchy (child→root):");
            int idx = 0;
            var tLog = Camera.main.transform;
            while (tLog != null)
            {
                var comps = tLog.GetComponents<MonoBehaviour>();
                var names = new System.Collections.Generic.List<string>();
                foreach (var c in comps) if (c != null) names.Add(c.GetType().Name);
                sb.Append($"\n  [{idx}] '{tLog.name}' euler=({tLog.eulerAngles.x:F1},{tLog.eulerAngles.y:F1},{tLog.eulerAngles.z:F1})" +
                          (names.Count > 0 ? $"  scripts=[{string.Join(",", names)}]" : ""));
                tLog = tLog.parent;
                idx++;
            }
            Plugin.Log.LogInfo(sb.ToString());

            // Camera rotation is now done via Camera.main.transform directly in
            // OnCameraPreRender — no pivot caching needed. Log just for diagnostics.
        }

        void OnDestroy()
        {
            Camera.onPreRender -= OnCameraPreRender;
        }

        // ── Camera rotation (pre-render, fires after ALL Update/LateUpdate) ────────
        //
        // WHY Quaternion.AngleAxis instead of setting eulerAngles.y directly:
        //   Setting eulerAngles.y in world-space recomputes the camera's *local*
        //   rotation against its parent.  If the parent has any pitch or roll
        //   (common in 3rd-person camera rigs), the resulting local X/Z change too,
        //   making the camera look at the sky or ground.
        //
        //   Rotating by Quaternion.AngleAxis(delta, Vector3.up) pre-multiplies in
        //   world-space: it spins the camera around the world Y axis by exactly
        //   `delta` degrees while leaving its pitch and roll completely intact.
        //
        // WHY _assistCameraYaw instead of reading cam.transform.eulerAngles.y:
        //   The game's camera controller applies mouse delta each frame.  If we
        //   re-read the camera angle (which already includes that delta) and then
        //   try to move toward our target, we're playing catch-up with moving
        //   goalposts — the result oscillates.  Tracking our own yaw state and
        //   ignoring the game's intermediate value gives clean, stable rotation.
        private void OnCameraPreRender(Camera cam)
        {
            if (cam != Camera.main) return;
            if (!_yawAssistWasActive || !_holeFound) return;
            if (_localGolfer == null || !GameAccess.IsAimingSwing(_localGolfer)) return;

            // Seed from the real camera angle the first time assist activates each swing
            if (!_assistCameraActive)
            {
                _assistCameraYaw    = cam.transform.eulerAngles.y;
                _assistCameraActive = true;
            }

            float spd = Plugin.GolfYawSpeed.Value;
            _assistCameraYaw = spd <= 0f
                ? _cameraDesiredYaw
                : Mathf.MoveTowardsAngle(_assistCameraYaw, _cameraDesiredYaw, spd * Time.deltaTime);

            // Rotate the camera around the world-Y axis by the remaining delta.
            // This is pitch-safe: the camera's tilt (X) and roll (Z) are untouched.
            float delta = Mathf.DeltaAngle(cam.transform.eulerAngles.y, _assistCameraYaw);
            if (Mathf.Abs(delta) > 0.01f)
                cam.transform.rotation =
                    Quaternion.AngleAxis(delta, Vector3.up) * cam.transform.rotation;
        }

        void Update()
        {
            HandleGlobalHotkeys();

            // Refresh game references periodically
            _refreshTimer += Time.deltaTime;
            if (_refreshTimer >= RefreshInterval)
            {
                _refreshTimer  = 0f;
                _localGolfer   = GameAccess.LocalGolfer;
                _localMovement = GameAccess.LocalMovement;
                if (_localGolfer != null)
                    _holeFound = GameAccess.TryGetHolePosition(_localGolfer, _localMovement, out _holePos);
                else
                    _holeFound = false;
            }

            if (_localGolfer == null || _localMovement == null) return;

            bool aimingSwing  = GameAccess.IsAimingSwing(_localGolfer);
            bool chargingSwing = GameAccess.IsChargingSwing(_localGolfer);
            bool aimingItem   = GameAccess.IsAimingItem(_localGolfer);

            // Reset per-charge state when not charging
            if (!chargingSwing) { _firedThisCharge = false; _alignedTimeAccum = 0f; }

            if (aimingSwing && _holeFound) UpdatePrediction();
            else { _predictedPath = null; _aimAligned = false; }

            if (aimingSwing)
            {
                RunYawAssist();
                RunPitchSolve(aimingSwing);
                RunAutoFire(chargingSwing);
            }

            if (aimingItem) RunWeaponAssist();
        }

        void OnGUI()
        {
            if (!_hudVisible || !Plugin.HUDEnabled.Value) return;
            if (Event.current.type != EventType.Repaint) return;
            EnsureStyles();
            DrawInfoBox();
        }

        // ── Global hotkeys ────────────────────────────────────────────────────

        private void HandleGlobalHotkeys()
        {
            if (InputHelper.WasPressedThisFrame(Plugin.HUDToggleKey.Value))
                _hudVisible = !_hudVisible;

            // Yaw toggle
            if (Plugin.GolfEnabled.Value && Plugin.GolfYawMode.Value == "toggle" &&
                InputHelper.WasPressedThisFrame(Plugin.GolfYawKey.Value))
                _yawToggleOn = !_yawToggleOn;

            // Pitch toggle
            if (Plugin.PitchSolveEnabled.Value && Plugin.PitchSolveMode.Value == "toggle" &&
                InputHelper.WasPressedThisFrame(Plugin.PitchSolveKey.Value))
                _pitchToggleOn = !_pitchToggleOn;
        }

        // ── Prediction ────────────────────────────────────────────────────────

        private void UpdatePrediction()
        {
            float yaw  = GameAccess.GetYaw(_localMovement!);
            float pitch = GameAccess.GetSwingPitch(_localGolfer!);
            Vector3 wind = GameAccess.GetWindVector();
            Vector3 from = GameAccess.GetBallPosition(_localGolfer!, _localMovement!);

            float maxSpeed = GameAccess.GetMaxSwingHitSpeed(_localGolfer!);
            float minSpeed = GameAccess.GetMinSwingHitSpeed(_localGolfer!);
            float maxPitch = GameAccess.GetMaxSwingPitch(_localGolfer!);

            if (!_speedLogged && maxSpeed > 0f)
            {
                Plugin.Log.LogInfo(
                    $"[AimAssist] MaxSwingHitSpeed={maxSpeed:F1}  MinSwingHitSpeed={minSpeed:F1}" +
                    $"  MaxPitch={maxPitch:F1}°  wind={wind.magnitude:F1} m/s");
                _speedLogged = true;
            }

            // Yaw and trajectory preview (use max speed so preview always shows full-power path)
            _solvedYaw = BallPredictor.CalculateYaw(from, _holePos);
            _predictedPath      = BallPredictor.SimulateTrajectory(from, yaw, pitch, maxSpeed, wind, 150);
            _predictedLandError = BallPredictor.LandingError(_predictedPath, _holePos);

            // ── Optimal (speed, pitch) pair ───────────────────────────────────────
            // Find the PHYSICAL minimum launch speed — the lowest speed at which
            // the projectile can reach the hole at any pitch up to 89° (essentially
            // unconstrained).  For flat terrain this is sqrt(g*d) at 45° pitch.
            // We do NOT use maxPitch here: GetMaxSwingPitch can return an incorrect
            // value that would inflate the estimate.  The pitch solve separately
            // validates against the actual maxPitch before applying the angle.
            float optSpeed = BallPredictor.FindMinimumReachSpeed(
                from, _holePos, 89f, maxSpeed, Plugin.PitchHighArc.Value);

            if (float.IsNaN(optSpeed))
            {
                // Hole is out of range entirely — go full power, pitch solve invalid
                _pitchSolveValid     = false;
                _solvedPitch         = float.NaN;
                _neededNormalizedPower = 1f;
                _rangeStatus         = "OUT OF RANGE";
            }
            else
            {
                // Pitch that matches the optimal speed
                _solvedPitch       = BallPredictor.CalculatePitch(from, _holePos, optSpeed, Plugin.PitchHighArc.Value);
                _pitchSolveValid   = !float.IsNaN(_solvedPitch);
                _rangeStatus       = "IN RANGE";

                // Map optimal speed to a normalised charge level
                if (optSpeed <= minSpeed)
                    _neededNormalizedPower = 0f;   // very close — could fire at any charge
                else
                    _neededNormalizedPower = Mathf.Clamp01(
                        Mathf.InverseLerp(minSpeed, maxSpeed, optSpeed));
            }

            // ── Alignment check for auto-fire ────────────────────────────────────
            float yawErr   = Mathf.Abs(Mathf.DeltaAngle(yaw, _solvedYaw));
            float pitchErr = 0f;
            if (Plugin.PitchSolveEnabled.Value)
            {
                if (!_pitchSolveValid)
                    pitchErr = float.MaxValue; // out of range → block fire
                else
                    pitchErr = Mathf.Abs(pitch - Mathf.Clamp(_solvedPitch, 0f, maxPitch));
            }
            _aimAligned = (yawErr + pitchErr) <= Plugin.AutoFireThreshold.Value;
        }

        // ── Yaw Assist ────────────────────────────────────────────────────────

        private void RunYawAssist()
        {
            if (!Plugin.GolfEnabled.Value || !_holeFound) return;

            bool active = IsYawAssistActive();

            // When assist deactivates, reset camera tracking so next activation
            // starts fresh from wherever the player's camera currently is.
            if (!active) _assistCameraActive = false;

            _yawAssistWasActive = active;
            if (active)
                _cameraDesiredYaw = _solvedYaw;
        }

        void LateUpdate()
        {
            if (!_holeFound || _localGolfer == null || _localMovement == null) return;
            if (!GameAccess.IsAimingSwing(_localGolfer) || !_yawAssistWasActive) return;

            float currentBodyYaw = GameAccess.GetYaw(_localMovement);
            float angleDiff = Mathf.Abs(Mathf.DeltaAngle(currentBodyYaw, _cameraDesiredYaw));
            if (angleDiff > Plugin.GolfAssistAngle.Value) return;

            // Keep body yaw (aim guide arrow) in sync with the hole direction.
            // Actual camera rotation is handled in OnCameraPreRender.
            float spd = Plugin.GolfYawSpeed.Value;
            if (spd <= 0f)
                GameAccess.SnapYaw(_localMovement, _cameraDesiredYaw);
            else
            {
                float newBodyYaw = Mathf.MoveTowardsAngle(currentBodyYaw, _cameraDesiredYaw, spd * Time.deltaTime);
                GameAccess.SnapYaw(_localMovement, newBodyYaw);
            }
        }

        // ── Pitch Solve ───────────────────────────────────────────────────────

        private void RunPitchSolve(bool aimingSwing)
        {
            if (!Plugin.PitchSolveEnabled.Value || !_holeFound || !_pitchSolveValid) return;

            bool active = Plugin.PitchSolveMode.Value switch
            {
                "auto"     => true,
                "hold"     => InputHelper.IsHeld(Plugin.PitchSolveKey.Value),
                "toggle"   => _pitchToggleOn,
                "with_yaw" => IsYawAssistActive(),
                _          => false
            };
            if (!active) return;

            if (float.IsNaN(_solvedPitch)) return; // out of range — don't override pitch
            float maxPitch     = GameAccess.GetMaxSwingPitch(_localGolfer!);
            float clampedPitch = Mathf.Clamp(_solvedPitch, 0f, maxPitch);
            float currentPitch = GameAccess.GetSwingPitch(_localGolfer!);
            float spd          = Plugin.PitchSolveSpeed.Value;
            float newPitch     = spd <= 0f
                ? clampedPitch
                : Mathf.MoveTowards(currentPitch, clampedPitch, spd * Time.deltaTime);
            GameAccess.SetSwingPitch(_localGolfer!, newPitch);
        }

        private bool IsYawAssistActive() => Plugin.GolfYawMode.Value switch
        {
            "auto"   => true,
            "hold"   => InputHelper.IsHeld(Plugin.GolfYawKey.Value),
            "toggle" => _yawToggleOn,
            _        => false
        };

        // ── Auto-Fire ─────────────────────────────────────────────────────────

        private void RunAutoFire(bool chargingSwing)
        {
            if (Plugin.AutoFireMode.Value == "off") return;
            if (!chargingSwing || _firedThisCharge) return;
            if (!_aimAligned || !_holeFound) return;
            // Fire when charge reaches the optimal level for the ball to reach the hole.
            // _neededNormalizedPower is 1.0 when the hole is out of range (fall back to full power).
            if (GameAccess.GetSwingNormalizedPower(_localGolfer!) < _neededNormalizedPower) return;

            bool fire = false;

            if (Plugin.AutoFireMode.Value == "key")
            {
                fire = InputHelper.WasPressedThisFrame(Plugin.AutoFireKey.Value);
            }
            else if (Plugin.AutoFireMode.Value == "auto")
            {
                _alignedTimeAccum += Time.deltaTime;
                fire = _alignedTimeAccum >= AlignRequiredTime;
            }

            if (fire)
            {
                // Snap camera and body precisely before releasing — eliminates any
                // residual angular error from smooth interpolation at fire time.
                if (Camera.main != null)
                {
                    float snapDelta = Mathf.DeltaAngle(Camera.main.transform.eulerAngles.y, _cameraDesiredYaw);
                    Camera.main.transform.rotation =
                        Quaternion.AngleAxis(snapDelta, Vector3.up) * Camera.main.transform.rotation;
                }
                GameAccess.SnapYaw(_localMovement!, _cameraDesiredYaw);

                GameAccess.ReleaseSwingCharge(_localGolfer!);
                _firedThisCharge  = true;
                _alignedTimeAccum = 0f;
                Plugin.Log.LogInfo(
                    $"[AimAssist] Auto-fired! power={_neededNormalizedPower * 100f:F0}%  " +
                    $"predictedErr={_predictedLandError:F1}m  " +
                    $"yaw={GameAccess.GetYaw(_localMovement!):F1}°  " +
                    $"pitch={GameAccess.GetSwingPitch(_localGolfer!):F1}°");
            }
        }

        // ── Weapon Assist ─────────────────────────────────────────────────────

        private void RunWeaponAssist()
        {
            if (!Plugin.WeaponEnabled.Value || _localMovement == null) return;

            var   targets    = GameAccess.GetLockOnTargetPositions();
            if (targets.Count == 0) return;

            var   playerPos  = GameAccess.GetPosition(_localMovement);
            float currentYaw = GameAccess.GetYaw(_localMovement);
            float bestScore  = float.MaxValue;
            float bestYaw    = currentYaw;

            foreach (var tPos in targets)
            {
                float tYaw      = BallPredictor.CalculateYaw(playerPos, tPos);
                float angleDiff = Mathf.Abs(Mathf.DeltaAngle(currentYaw, tYaw));
                if (angleDiff > Plugin.WeaponConeAngle.Value) continue;
                float score = angleDiff + Vector3.Distance(playerPos, tPos) * 0.05f;
                if (score < bestScore) { bestScore = score; bestYaw = tYaw; }
            }

            if (Mathf.Approximately(bestYaw, currentYaw)) return;

            float spd    = Plugin.WeaponSnapSpeed.Value;
            float newYaw = spd <= 0f
                ? bestYaw
                : Mathf.MoveTowardsAngle(currentYaw, bestYaw, spd * Time.deltaTime);
            GameAccess.SetYaw(_localMovement, newYaw);
        }

        // ── HUD ───────────────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            _labelStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white },
                wordWrap  = true,
            };
            _boxStyle ??= new GUIStyle(GUI.skin.box)
                { padding = new RectOffset(6, 6, 4, 4) };
        }

        private void DrawInfoBox()
        {
            bool aimingSwing = _localGolfer != null && GameAccess.IsAimingSwing(_localGolfer);
            bool aimingItem  = _localGolfer != null && GameAccess.IsAimingItem(_localGolfer);

            // Line 1: hole distance + miss error
            string holeLine;
            if (_localGolfer == null)
                holeLine = "Hole: no player";
            else if (!_holeFound)
                holeLine = "Hole: none in scene";
            else if (_localMovement != null)
            {
                float dist = Vector3.Distance(GameAccess.GetPosition(_localMovement), _holePos);
                holeLine = $"Hole: {dist:F0}m" +
                           (aimingSwing && _predictedLandError >= 0f ? $"  miss: {_predictedLandError:F1}m" : "");
            }
            else
                holeLine = "Hole: found";

            // Line 2: yaw status
            string yawLine = BuildStatusLine(
                Plugin.GolfEnabled.Value,
                Plugin.GolfYawMode.Value,
                Plugin.GolfYawKey.Value,
                _yawToggleOn,
                aimingSwing,
                "Yaw");

            // Line 3: pitch status
            string pitchLine = BuildStatusLine(
                Plugin.PitchSolveEnabled.Value,
                Plugin.PitchSolveMode.Value == "with_yaw" ? Plugin.GolfYawMode.Value : Plugin.PitchSolveMode.Value,
                Plugin.PitchSolveMode.Value == "with_yaw" ? Plugin.GolfYawKey.Value  : Plugin.PitchSolveKey.Value,
                Plugin.PitchSolveMode.Value == "with_yaw" ? _yawToggleOn             : _pitchToggleOn,
                aimingSwing,
                "Pitch");

            if (Plugin.PitchSolveEnabled.Value && aimingSwing)
            {
                if (_rangeStatus == "OUT OF RANGE")
                    pitchLine += " \u26a0 OOR";
                else if (_pitchSolveValid)
                    pitchLine += $" {_solvedPitch:F0}\u00b0";
            }

            // Line 4: auto-fire — only show aligning/ready when actively swinging
            string fireLine;
            if (Plugin.AutoFireMode.Value == "off")
            {
                fireLine = "Fire: OFF";
            }
            else if (!aimingSwing)
            {
                fireLine = $"Fire: {Plugin.AutoFireMode.Value.ToUpper()}";
            }
            else
            {
                // Show needed power % so the user knows what charge level triggers the shot
                string pwr  = _neededNormalizedPower >= 0.99f ? "100%" : $"{_neededNormalizedPower * 100f:F0}%";
                string cue  = _aimAligned ? "\u25cf ALIGNED!" : "(aligning...)";
                fireLine = Plugin.AutoFireMode.Value == "auto"
                    ? $"Fire: AUTO [{pwr}] {cue}"
                    : $"Fire: [{Plugin.AutoFireKey.Value}] [{pwr}] {cue}";
            }

            // Line 5: weapon
            string weapLine = !Plugin.WeaponEnabled.Value
                ? "Weap: OFF"
                : aimingItem ? "Weap: ACTIVE \u25cf" : "Weap: standby";

            string body = $"{holeLine}\n{yawLine}\n{pitchLine}\n{fireLine}\n{weapLine}";

            float boxW   = 195f, boxH = 90f;
            int   corner = Plugin.HUDCorner.Value;
            int   margin = 10;
            float bx     = corner is 0 or 2 ? Screen.width  - margin - boxW : (float)margin;
            float by     = corner is 0 or 1 ? Screen.height - margin - boxH : (float)margin;

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.Box(new Rect(bx, by, boxW, boxH), GUIContent.none, _boxStyle!);
            GUI.color = old;
            GUI.Label(new Rect(bx + 6f, by + 4f, boxW - 8f, boxH), body, _labelStyle!);
        }

        private string BuildStatusLine(bool enabled, string mode, string key,
                                       bool toggleOn, bool aiming, string label)
        {
            if (!enabled) return $"{label}: OFF";
            if (!aiming)  return $"{label}: ready";
            return mode switch
            {
                "auto"   => $"{label}: AUTO \u25cf",
                "hold"   => InputHelper.IsHeld(key)
                    ? $"{label}: HOLD [{key}] \u25cf"
                    : $"{label}: hold [{key}]",
                "toggle" => toggleOn
                    ? $"{label}: ON [{key}] \u25cf"
                    : $"{label}: off [{key}]",
                _        => $"{label}: ?",
            };
        }

    }
}
