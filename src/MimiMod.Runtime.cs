using UnityEngine;
using UnityEngine.InputSystem;

public partial class MimiMod
{
    [System.Obsolete]
    public override void OnApplicationStart()
    {
        LoadOrCreateConfig();
    }

    public override void OnUpdate()
    {
        float currentTime = Time.time;

        InvalidateResolvedContextIfLost();
        HandleInput();

        if ((playerMovement == null || playerGolfer == null) && currentTime >= nextPlayerSearchTime)
        {
            nextPlayerSearchTime = currentTime + playerSearchInterval;
            ResolvePlayerContext();
        }

        EnsureLocalGolfBallReference(false);

        if (playerGolfer != null && currentTime >= nextIdealSwingCalculationTime)
        {
            nextIdealSwingCalculationTime = currentTime + idealSwingCalculationInterval;
            CalculateIdealSwingParameters(false);
        }

        EnsureVisualsInitialized();
        if (visualsInitialized)
        {
            UpdateTrails();
            UpdateHud();
            UpdateImpactPreview();
        }

    }

    public override void OnLateUpdate()
    {
        AutoAimCamera();

        if (assistEnabled && isLeftMousePressed && !autoReleaseTriggeredThisCharge)
        {
            AutoSwingRelease();
        }
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

        if (WasConfiguredKeyPressed(assistToggleKey))
        {
            ToggleAssist();
        }

        if (WasConfiguredKeyPressed(coffeeBoostKey))
        {
            AddCoffeeBoost();
        }

        if (WasConfiguredKeyPressed(nearestBallModeKey))
        {
            ToggleNearestBallMode();
        }

        if (WasConfiguredKeyPressed(unlockAllCosmeticsKey))
        {
            UnlockAllCosmetics();
        }
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
