using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class SuperHackerGolf
{
    [System.Obsolete]
    public override void OnApplicationStart()
    {
        LoadOrCreateConfig();
        TryInstallAntiCheatBypass();
    }

    public override void OnUpdate()
    {
        // E25: per-call diagnostic try/catches. The old single-outer-catch
        // turned zero-rva exceptions anywhere in the tick into a generic
        // "OnUpdate swallowed X" log, giving us no way to isolate the bad
        // call. Now each major sub-call is wrapped individually and logs
        // its name on first throw. Rate-limited via onUpdateErrorTimestamps
        // so the log isn't drowned in spam.
        float currentTime = Time.time;
        TickGuarded("InvalidateResolvedContextIfLost", InvalidateResolvedContextIfLost);
        TickGuarded("HandleInput", HandleInput);
        TickGuarded("UpdateShotTelemetry", UpdateShotTelemetry);

        if ((playerMovement == null || playerGolfer == null) && currentTime >= nextPlayerSearchTime)
        {
            nextPlayerSearchTime = currentTime + playerSearchInterval;
            TickGuarded("ResolvePlayerContext", () => ResolvePlayerContext());
        }

        TickGuarded("EnsureLocalGolfBallReference", () => EnsureLocalGolfBallReference(false));

        if (playerGolfer != null && currentTime >= nextIdealSwingCalculationTime)
        {
            nextIdealSwingCalculationTime = currentTime + idealSwingCalculationInterval;
            TickGuarded("CalculateIdealSwingParameters", () => CalculateIdealSwingParameters(false));
        }

        TickGuarded("EnsureVisualsInitialized", EnsureVisualsInitialized);
        if (visualsInitialized)
        {
            TickGuarded("UpdateTrails", UpdateTrails);
            TickGuarded("UpdateHud", () => UpdateHud(false));
            TickGuarded("UpdateImpactPreview", UpdateImpactPreview);
        }

        TickGuarded("TickForcedShield", TickForcedShield);
        TickGuarded("TickCombatAssists", TickCombatAssists);
    }

    // Per-method diagnostic wrapper. If the call throws, log its name with the
    // exception type + message, but rate-limit repeated failures to once per
    // 5 seconds per method so a consistent zero-rva doesn't spam the log.
    private Dictionary<string, float> onUpdateErrorTimestamps = new Dictionary<string, float>();

    private void TickGuarded(string label, Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (!onUpdateErrorTimestamps.TryGetValue(label, out last) || now - last > 5f)
            {
                onUpdateErrorTimestamps[label] = now;
                MelonLoader.MelonLogger.Warning(
                    $"[SuperHackerGolf] {label} threw {ex.GetType().Name}: {ex.Message} " +
                    $"(silenced for 5s)");
            }
        }
    }

    public override void OnLateUpdate()
    {
        TickGuarded("AutoAimCamera", AutoAimCamera);

        if (assistEnabled && isLeftMousePressed && !autoReleaseTriggeredThisCharge)
        {
            TickGuarded("AutoSwingRelease", AutoSwingRelease);
        }

        // E31: ESP snapshot built in LateUpdate so OnGUI Repaint can consume
        // it without doing physics/transform reads inside GUI painting.
        TickGuarded("TickEspSnapshot", TickEspSnapshot);
    }

    private void HandleInput()
    {
        UpdateMouseState();
        HandleKeyboardShortcuts();
    }

    private void UpdateMouseState()
    {
        bool previousLeft = isLeftMousePressed;

        if (Mouse.current != null)
        {
            isLeftMousePressed = Mouse.current.leftButton.isPressed;
            isRightMousePressed = Mouse.current.rightButton.isPressed;
        }
        else
        {
            isLeftMousePressed = false;
            isRightMousePressed = false;
        }

        if (isLeftMousePressed && !previousLeft)
        {
            ResetChargeState();
            ResetTrailState();
            if (assistEnabled)
            {
                CalculateIdealSwingParameters(true);
            }
        }
        else if (!isLeftMousePressed && previousLeft)
        {
            ResetChargeState();
            DisableAutoAimCamera();
        }
    }

    private void HandleKeyboardShortcuts()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        // E31b: unified bind system. Replaces the per-bind WasConfiguredKeyPressed
        // fan-out. Single tick walks every registered bind, supports per-bind
        // Hold/Toggle/Released, and also handles the listening state for GUI
        // rebinding.
        TickGuarded("kb.binds", TickBinds);
        TickGuarded("kb.settingsGui", UpdateSettingsGuiHotkey);
    }

    private void ToggleAssist()
    {
        assistEnabled = !assistEnabled;
        MarkHudDirty();

        if (assistEnabled)
        {
            ResolvePlayerContext();
            FindHoleOnly(true);
            CalculateIdealSwingParameters(true);
        }
        else
        {
            DisableAutoAimCamera();
            ResetChargeState();
            ClearPredictedTrails(true);
        }
    }

    private void AddCoffeeBoost()
    {
        if (playerMovement == null || addSpeedBoostMethod == null)
        {
            ResolvePlayerContext();
        }

        if (playerMovement == null || addSpeedBoostMethod == null)
        {
            return;
        }

        try
        {
            cachedSpeedBoostArgs[0] = 500f;
            addSpeedBoostMethod.Invoke(playerMovement, cachedSpeedBoostArgs);
        }
        catch
        {
        }
    }

    private void ToggleNearestBallMode()
    {
        nearestAnyBallModeEnabled = !nearestAnyBallModeEnabled;
        nextNearestAnyBallResolveTime = 0f;
        MarkHudDirty();

        ResolvePlayerContext();
        EnsureLocalGolfBallReference(true);
        ResetTrailState();

        if (playerGolfer != null)
        {
            FindHoleOnly(true);
            CalculateIdealSwingParameters(true);
        }
    }

    private void InvalidateResolvedContextIfLost()
    {
        if (hadResolvedPlayerContext &&
            (playerMovement == null ||
             playerGolfer == null ||
             playerMovement.gameObject == null ||
             playerGolfer.gameObject == null))
        {
            playerFound = false;
            playerMovement = null;
            playerGolfer = null;
            golfBall = null;
            addSpeedBoostMethod = null;
            lastBallResolveSource = "missing";
            hadResolvedPlayerContext = false;
            hadResolvedBallContext = false;
            ClearRuntimeState();
            return;
        }

        if (hadResolvedBallContext &&
            (golfBall == null || golfBall.gameObject == null))
        {
            golfBall = null;
            lastBallResolveSource = "missing";
            hadResolvedBallContext = false;
            ClearRuntimeState();
        }
    }

    private void ResetChargeState()
    {
        autoReleaseTriggeredThisCharge = false;
        autoChargeSequenceStarted = false;
        nextTryStartChargingTime = 0f;
        lastAutoSwingReleaseFrame = -1;
        lastObservedSwingPower = 0f;
    }

    private void ClearRuntimeState()
    {
        DisableAutoAimCamera();
        ResetChargeState();
        ResetTrailState();
        HideImpactPreview();
        cachedImpactPreviewReferenceCamera = null;
        nextImpactPreviewReferenceCameraRefreshTime = 0f;
        nextGolfBallCacheRefreshTime = 0f;
        cachedGolfBalls.Clear();
        nextPredictedPathRefreshTime = 0f;
        currentAimTargetPosition = Vector3.zero;
        windCompensatedAimTarget = Vector3.zero;
        currentSwingOriginPosition = Vector3.zero;
        holePosition = Vector3.zero;
        flagPosition = Vector3.zero;
        nextHoleSearchTime = 0f;
        nextIdealSwingCalculationTime = 0f;
        cachedLocalPlayerDisplayName = "";
        nextDisplayNameRefreshTime = 0f;
        MarkHudDirty();
    }

    private void EnsureVisualsInitialized()
    {
        if (visualsInitialized)
        {
            return;
        }

        if (Time.realtimeSinceStartup < visualsInitializationDelay)
        {
            return;
        }

        CreateHud();
        EnsureTrailRenderers();
        ApplyTrailVisualSettings();
        visualsInitialized = true;
    }
}
