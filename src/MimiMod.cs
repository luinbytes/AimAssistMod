using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(SuperHackerGolf), "SuperHackerGolf", "1.0.0", "luinbytes")]
[assembly: MelonGame]

public partial class SuperHackerGolf : MelonMod
{
    private Component playerMovement;
    private Component playerGolfer;
    private Component golfBall;

    private readonly Dictionary<string, PropertyInfo> playerGolferProperties = new Dictionary<string, PropertyInfo>(8);
    private readonly Dictionary<string, FieldInfo> playerGolferFields = new Dictionary<string, FieldInfo>(4);

    private MethodInfo addSpeedBoostMethod;
    private FieldInfo swingNormalizedPowerBackingField;

    private bool playerFound;
    private bool assistEnabled;
    private bool isLeftMousePressed;
    private bool isRightMousePressed;
    private bool autoReleaseTriggeredThisCharge;
    private bool autoChargeSequenceStarted;
    private int lastAutoSwingReleaseFrame = -1;
    private float nextTryStartChargingTime;
    private readonly float tryStartChargingInterval = 0.05f;
    private float idealSwingPower;
    private float idealSwingPitch;
    private Vector3 flagPosition = Vector3.zero;
    private Vector3 holePosition = Vector3.zero;
    private Vector3 currentAimTargetPosition = Vector3.zero;
    private Vector3 currentSwingOriginPosition = Vector3.zero;
    // E12: wind-compensated aim target produced by TrySolveAimAndSpeedSinglePass.
    // Separate from currentAimTargetPosition so AutoAimCamera (which runs after
    // CalculateIdealSwingParameters) can steer the player yaw toward the
    // compensated aim instead of the raw hole direction. Vector3.zero means
    // "no valid compensation — fall back to raw hole".
    private Vector3 windCompensatedAimTarget = Vector3.zero;
    // E14b: cache the 2D solver's full-physics result so we don't re-run the
    // expensive bounce+roll sim every frame. Invalidated when ball, hole,
    // wind, or pitch move meaningfully.
    private bool solveCacheValid;
    private Vector3 solveCacheBallPos;
    private Vector3 solveCacheHolePos;
    private Vector3 solveCacheWind;
    private float solveCachePitch;
    private Vector3 solveCacheAim;
    private float solveCacheSpeed;
    private bool solveCacheSuccess;
    // E15: per-solve diagnostics captured by TrySolveAimAndSpeedSinglePass
    // so the telemetry CSV can include what the solver actually chose.
    // Without this we can't distinguish "solver converged to a bad aim" from
    // "solver short-circuited" from "solver ran out of iterations".
    private int lastSolverIterCount;
    private float lastSolverFinalErrM;
    private bool lastSolverConverged;
    private Vector3 lastSolverCompensatedAim;
    private float lastSolverSpeedMps;
    private Vector3 aimTargetOffsetLocal = Vector3.zero;
    private readonly Vector3 swingOriginLocalOffset = new Vector3(0.86f, 0.05f, -0.12f);

    private string cachedLocalPlayerDisplayName = "";
    private float nextDisplayNameRefreshTime;
    private readonly float displayNameRefreshInterval = 0.5f;

    private int cachedAllGameObjectsFrame = -1;
    private GameObject[] cachedAllGameObjects;
    private int cachedAllComponentsFrame = -1;
    private Component[] cachedAllComponents;
    private float nextPlayerSearchTime;
    private float nextHoleSearchTime;
    private float nextIdealSwingCalculationTime;
    private float nextBallResolveTime;
    private readonly float playerSearchInterval = 1f;
    private readonly float holeSearchInterval = 0.5f;
    private readonly float idealSwingCalculationInterval = 0.1f;
    private readonly float ballResolveInterval = 0.2f;
    private readonly float puttDistanceThreshold = 12f;

    private bool hadResolvedPlayerContext;
    private bool hadResolvedBallContext;
    private string lastBallResolveSource = "missing";
    private Component initializedPlayerGolfer;

    private GameObject hudCanvas;
    private TextMeshProUGUI leftHudText;
    private TextMeshProUGUI centerHudText;
    private TextMeshProUGUI rightHudText;
    private TextMeshProUGUI bottomHudText;

    private bool isAimModeActive;
    private bool wasAimRequestedLastFrame;
    private bool reflectionCacheInitialized;
    private MethodInfo cachedTryGetMethod;
    private MethodInfo cachedOrbitSetYawMethod;
    private MethodInfo cachedOrbitSetPitchMethod;
    private MethodInfo cachedOrbitForceUpdateMethod;
    private MethodInfo cachedEnterSwingAimCameraMethod;
    private MethodInfo cachedExitSwingAimCameraMethod;
    private MethodInfo cachedReachOrbitSteadyStateMethod;
    private Component initializedYawPlayerMovement;
    private PropertyInfo cachedPlayerMovementYawProperty;
    private FieldInfo cachedPlayerMovementYawField;
    private Component initializedYawPlayerGolfer;
    private PropertyInfo cachedPlayerGolferYawProperty;
    private FieldInfo cachedPlayerGolferYawField;
    private bool cameraAimSmoothingInitialized;
    private float smoothedOrbitYaw;
    private float smoothedOrbitPitch;
    private float orbitYawVelocity;
    private float orbitPitchVelocity;
    private readonly float orbitAimSmoothTime = 0.02f;
    private readonly float orbitAimMaxSpeed = 2160f;
    private readonly object[] cachedOrbitModuleQueryArgs = new object[1];
    private readonly object[] cachedOrbitYawArgs = new object[1];
    private readonly object[] cachedOrbitPitchArgs = new object[1];

    private bool swingMathReflectionInitialized;
    private PropertyInfo cachedGolfSettingsProperty;
    private object cachedGolfSettingsObject;
    private PropertyInfo cachedGolfBallSettingsProperty;
    private object cachedGolfBallSettingsObject;
    private MethodInfo cachedBMathEaseInMethod;
    private MethodInfo cachedUpdateSwingNormalizedPowerMethod;
    private bool matchSetupRulesReflectionInitialized;
    private Type cachedMatchSetupRuleEnumType;
    private MethodInfo cachedMatchSetupGetValueMethod;
    private object cachedMatchSetupSwingPowerRuleValue;
    private PropertyInfo cachedLocalPlayerAsGolferProperty;
    private bool localGolferResolverInitialized;
    private MethodInfo cachedTryStartChargingSwingMethod;
    private MethodInfo cachedSetIsChargingSwingMethod;
    private MethodInfo cachedReleaseSwingChargeMethod;
    private float lastObservedSwingPower;

    private readonly float[] launchModelPowers = new float[] { 0.10f, 0.50f, 1.00f, 1.15f };
    private readonly float[] launchModelSpeeds = new float[] { 17.000f, 85.000f, 170.000f, 195.500f };
    private readonly float launchModelReferenceSrvMul = 2.00f;
    private bool golfBallVelocityReflectionInitialized;
    private Type cachedGolfBallTypeForVelocity;
    private PropertyInfo cachedGolfBallRigidbodyProperty;
    private bool rigidbodyVelocityReflectionInitialized;
    private PropertyInfo cachedRigidbodyLinearVelocityProperty;
    private readonly float trajectoryGravity = 9.81f;

    private GameObject shotPathObject;
    private LineRenderer shotPathLine;
    private Material shotPathMaterial;
    private GameObject predictedPathObject;
    private LineRenderer predictedPathLine;
    private Material predictedPathMaterial;
    private GameObject frozenPredictedPathObject;
    private LineRenderer frozenPredictedPathLine;
    private Material frozenPredictedPathMaterial;
    private readonly List<Vector3> shotPathPoints = new List<Vector3>(768);
    private readonly List<Vector3> predictedPathPoints = new List<Vector3>(384);
    private readonly List<Vector3> frozenPredictedPathPoints = new List<Vector3>(384);
    private bool predictedImpactPreviewValid;
    private Vector3 predictedImpactPreviewPoint = Vector3.zero;
    private Vector3 predictedImpactPreviewApproachDirection = Vector3.forward;
    private bool frozenImpactPreviewValid;
    private Vector3 frozenImpactPreviewPoint = Vector3.zero;
    private Vector3 frozenImpactPreviewApproachDirection = Vector3.forward;
    private bool lockLivePredictedPath;
    private bool observedBallMotionSinceLastShot;
    private bool isRecordingShotPath;
    private float predictedTrajectoryHideStartTime;
    private float nextPredictedPathRefreshTime;
    private Vector3 lastShotPathBallPosition = Vector3.zero;
    private float lastShotPathMoveTime;
    private readonly float predictedUnlockSpeedThreshold = 0.12f;
    private readonly float shotPathHeightOffset = 0.14f;
    private readonly float shotPathMoveThreshold = 0.004f;
    private readonly float shotPathPointSpacing = 0.012f;
    private readonly int shotPathMaxPoints = 3072;
    private readonly float shotPathStationaryDelay = 0.65f;
    private readonly int predictedPathMaxSteps = 360;
    private readonly float predictedPathMaxTime = 7.2f;
    private readonly float predictedPathPointSpacing = 0.30f;
    private readonly float predictedPathRefreshInterval = 0.05f;
    private readonly float predictedTrajectoryUnlockFallbackDelay = 0.75f;
    private bool predictedPathCacheValid;
    private Component cachedPredictedPathBall;
    private Vector3 cachedPredictedShotOrigin = Vector3.zero;
    private Vector3 cachedPredictedAimTargetPosition = Vector3.zero;
    private float cachedPredictedSwingPower;
    private float cachedPredictedSwingPitch;
    private readonly float predictedPathRebuildDistanceEpsilon = 0.015f;
    private readonly float predictedPathRebuildPowerEpsilon = 0.0025f;
    private readonly float predictedPathRebuildPitchEpsilon = 0.05f;

    private readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "SuperHackerGolf.cfg");
    private string assistToggleKeyName = "F";
    private string coffeeBoostKeyName = "F2";
    private string nearestBallModeKeyName = "F3";
    private string unlockAllCosmeticsKeyName = "F4";
    private string shieldToggleKeyName = "F6";
    private string weaponAssistToggleKeyName = "F7";
    private string mineAssistToggleKeyName = "F9";
    private string bunnyhopToggleKeyName = "F10";
    private string assistToggleKeyLabel = "F";
    private string coffeeBoostKeyLabel = "F2";
    private string nearestBallModeKeyLabel = "F3";
    private string unlockAllCosmeticsKeyLabel = "F4";
    private string shieldToggleKeyLabel = "F6";
    private string weaponAssistToggleKeyLabel = "F7";
    private string mineAssistToggleKeyLabel = "F9";
    private string bunnyhopToggleKeyLabel = "F10";
    private Key assistToggleKey = Key.F;
    private Key coffeeBoostKey = Key.F2;
    private Key nearestBallModeKey = Key.F3;
    private Key unlockAllCosmeticsKey = Key.F4;
    private Key shieldToggleKey = Key.F6;
    private Key weaponAssistToggleKey = Key.F7;
    private Key mineAssistToggleKey = Key.F9;
    private Key bunnyhopToggleKey = Key.F10;
    // E24 feature toggles (runtime state, not persisted to config unless user hits Save)
    // ── E28 Aimbot redesign ───────────────────────────────────────────────
    //
    // Mode-driven aimbot modelled on conventional rage/legit split used in
    // modern CS-style hack frameworks:
    //
    //   OFF    — everything disabled, patch is a no-op
    //   LEGIT  — smoothed aim interpolation, narrow FOV, manual fire by default,
    //            visibility LOS check, prefers closest-to-crosshair
    //   RAGE   — instant snap, wide FOV, auto-fire, skip-protected only
    //   CUSTOM — every setting individually exposed, user has full control
    //
    // Activating LEGIT/RAGE sets sensible defaults on all sub-settings;
    // CUSTOM inherits whatever was set previously.
    public enum AimbotMode { Off, Legit, Rage, Custom }
    public enum TargetSelectionMode { BestScore, ClosestToCrosshair, ClosestByDistance }
    public enum AimbotActivation { Always, HoldKey }
    public enum AimbotSmoothingCurve { Linear, EaseOut, Spring }

    // Master switch — replaces the old `weaponAssistEnabled` bool.
    private AimbotMode aimbotMode = AimbotMode.Off;
    private AimbotActivation aimbotActivation = AimbotActivation.Always;
    private string aimbotActivationKeyName = "Mouse4";  // side-mouse button for hold-to-aim
    private string aimbotActivationKeyLabel = "Mouse4";
    private Key aimbotActivationKey = Key.None; // populated for keyboard-keys; mouse buttons handled separately
    private bool aimbotActivationKeyIsMouse4 = true;    // default activation is Mouse4
    private bool aimbotActivationKeyIsMouse5;

    // Target selection
    private TargetSelectionMode targetSelectionMode = TargetSelectionMode.BestScore;
    private bool weaponAssistTargetPlayers = true;
    private bool weaponAssistTargetDummies;
    private bool weaponAssistTargetMines;
    private bool weaponAssistTargetGolfCarts;

    // E30d / E31c: hitbox selection — multi-select with fixed priority.
    // Priority order: Head → Chest → Legs. First enabled hitbox wins.
    // When all are disabled we fall back to chest (center mass) rather
    // than producing wild shots.
    [System.Flags]
    public enum HitboxFlags
    {
        None  = 0,
        Head  = 1 << 0,  // +1.4m
        Chest = 1 << 1,  //  0.0m
        Legs  = 1 << 2,  // -0.6m
    }
    private HitboxFlags aimbotHitboxFlags = HitboxFlags.Chest;

    private float GetHitboxYOffset()
    {
        if ((aimbotHitboxFlags & HitboxFlags.Head) != 0) return 1.4f;
        if ((aimbotHitboxFlags & HitboxFlags.Chest) != 0) return 0f;
        if ((aimbotHitboxFlags & HitboxFlags.Legs) != 0) return -0.6f;
        return 0f;
    }
    private bool weaponAssistSkipProtected = true;
    private bool weaponAssistVisibilityCheck = true;    // raycast from player to target; skip if blocked

    // Aim geometry
    private float weaponAssistConeAngleDeg = 30f;
    private float weaponAssistMaxRange = 80f;

    // Smoothing (legit only — rage is instant)
    private float aimbotSmoothingFactor = 30f;           // 0 = snap, 100 = very slow; interpolated per-frame
    private AimbotSmoothingCurve aimbotSmoothingCurve = AimbotSmoothingCurve.EaseOut;

    // Sticky lock (hysteresis against target jitter between two equidistant candidates)
    private float aimbotStickyLockMs = 250f;
    private Vector3 aimbotLastLockedAim;
    private double aimbotLastLockTime;
    private bool aimbotHasLockedTarget;
    // Smoothing state (legit mode only — rage snaps instantly)
    private Vector3 aimbotSmoothedAim;
    private Vector3 aimbotSmoothVelocity;
    private bool aimbotHasSmoothedAim;

    // Auto-fire
    private bool weaponAutoFireEnabled;                  // off by default in LEGIT mode
    private float weaponAutoFireMinIntervalSec = 0.2f;   // 5 shots/sec

    // Legacy alias for the old toggle so feature-row helpers keep working;
    // reflects whether mode != Off.
    private bool weaponAssistEnabled => aimbotMode != AimbotMode.Off;

    private bool mineAssistEnabled;
    private bool bunnyhopEnabled;
    private bool actualTrailEnabled = true;
    private bool predictedTrailEnabled = true;
    private bool frozenTrailEnabled = true;
    private bool impactPreviewEnabled = true;
    private bool allowOvercharge;             // default false: clamp auto-fire at 100% instead of the game's 115% overcharge cap
    private bool instaHitEnabled;             // default false: let the player charge manually, mod only auto-releases at optimal power
    private bool telemetryEnabled;            // default false: when true, logs predicted vs actual landing for every auto-fired shot
    private float windStrength = 0.0041f;     // DEAD CODE (E23) — kept for config-compat. Physics uses reflected HittableSettings.Wind.WindFactor.
    private float windDragStrength = 0.04f;   // DEAD CODE (E23) — kept for config-compat. Physics uses reflected HittableSettings.Wind.CrossWindFactor.
    // E23: user-tunable roll damping multiplier. Observed empirically that
    // the sim's roll phase over-predicts distance by ~8-12m on pitch 45 52m
    // shots — ball in sim rolls further than in reality. Multiplying the
    // reflected damping rate by this factor tightens the sim's rest point.
    // Default 1.0 = unchanged, so enabling this cannot regress existing
    // accuracy. User can crank to 2.0-3.0 if their roll predictions run long.
    private float rollDampingMultiplier = 1.0f;
    private bool settingsGuiVisible;
    private string settingsGuiKeyName = "Insert";
    private string settingsGuiKeyLabel = "Insert";
    private Key settingsGuiKey = Key.Insert;
    private float impactPreviewTargetFps;
    private int impactPreviewTextureWidth = 640;
    private int impactPreviewTextureHeight = 360;
    private float actualTrailStartWidth = 0.22f;
    private float actualTrailEndWidth = 0.18f;
    private float predictedTrailStartWidth = 0.18f;
    private float predictedTrailEndWidth = 0.14f;
    private float frozenTrailStartWidth = 0.20f;
    private float frozenTrailEndWidth = 0.16f;
    private Color actualTrailColor = new Color(1f, 0.58f, 0.20f, 1f);
    private Color predictedTrailColor = new Color(0.36f, 0.95f, 0.46f, 0.95f);
    private Color frozenTrailColor = new Color(0.36f, 0.74f, 1f, 0.92f);
    private bool visualsInitialized;
    private readonly float visualsInitializationDelay = 2.5f;
    private bool trailVisualSettingsDirty = true;
    private bool actualTrailLineDirty = true;
    private bool predictedTrailLineDirty = true;
    private bool frozenTrailLineDirty = true;
    private bool hudDirty = true;
    private float nextHudRefreshTime;
    private readonly float hudRefreshInterval = 0.1f;
    private string cachedLeftHudText = "";
    private string cachedCenterHudText = "";
    private string cachedRightHudText = "";
    private string cachedBottomHudText = "";
    private bool nearestAnyBallModeEnabled;
    private float nextNearestAnyBallResolveTime;
    private readonly float nearestAnyBallResolveInterval = 0.1f;
    private readonly List<Component> cachedGolfBalls = new List<Component>(64);
    private float nextGolfBallCacheRefreshTime;
    private readonly float golfBallCacheRefreshInterval = 0.75f;
    private readonly float emptyGolfBallCacheRefreshInterval = 2f;
    private float nextImpactPreviewRenderTime;
    private Camera cachedImpactPreviewReferenceCamera;
    private float nextImpactPreviewReferenceCameraRefreshTime;
    private readonly float impactPreviewReferenceCameraRefreshInterval = 1f;
    private readonly float impactPreviewAutoTargetFps = 60f;
    private readonly RaycastHit[] impactPreviewRaycastHits = new RaycastHit[24];
    private readonly RaycastHit[] impactPreviewGroundProbeHits = new RaycastHit[24];
    private readonly object[] cachedSpeedBoostArgs = new object[1];
    private readonly object[] cachedEaseInArgs = new object[1];
    private readonly object[] cachedMatchSetupGetValueArgs = new object[1];
    private readonly object[] cachedChargingStateArgs = new object[1];
    private readonly object[] cachedUpdateSwingPowerArgs = new object[] { true, false };
}
