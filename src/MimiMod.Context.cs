using System;
using System.Reflection;
using UnityEngine;

public partial class SuperHackerGolf
{
    private void ResolvePlayerContext()
    {
        Component previousPlayerMovement = playerMovement;
        Component previousPlayerGolfer = playerGolfer;

        TryResolveLocalPlayerGolferViaGameManager();
        if (playerMovement == null)
        {
            GameObject[] allObjects = FindAllGameObjects();
            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject obj = allObjects[i];
                if (obj == null)
                {
                    continue;
                }

                Component[] components = obj.GetComponents<Component>();
                for (int j = 0; j < components.Length; j++)
                {
                    Component comp = components[j];
                    if (comp == null || comp.GetType().Name != "PlayerMovement" || !IsLocalPlayerMovement(comp))
                    {
                        continue;
                    }

                    playerMovement = comp;
                    addSpeedBoostMethod = comp.GetType().GetMethod("AddSpeedBoost", BindingFlags.NonPublic | BindingFlags.Instance);

                    i = allObjects.Length;
                    break;
                }
            }
        }
        else if (addSpeedBoostMethod == null)
        {
            addSpeedBoostMethod = playerMovement.GetType().GetMethod("AddSpeedBoost", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        EnsureLocalGolfBallReference(true);

        playerFound = playerMovement != null && playerGolfer != null;
        bool contextChanged =
            (previousPlayerMovement != null && !ReferenceEquals(previousPlayerMovement, playerMovement)) ||
            (previousPlayerGolfer != null && !ReferenceEquals(previousPlayerGolfer, playerGolfer));

        if (contextChanged)
        {
            ClearRuntimeState();
        }

        hadResolvedPlayerContext = playerMovement != null && playerGolfer != null;
    }

    private bool IsLocalPlayerMovement(Component component)
    {
        try
        {
            PropertyInfo property = component.GetType().GetProperty("isLocalPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property != null && property.PropertyType == typeof(bool) && (bool)property.GetValue(component, null);
        }
        catch
        {
            return false;
        }
    }

    private void CachePlayerGolferAccessors(Component golfer)
    {
        if (golfer == null)
        {
            return;
        }

        EnsurePlayerGolferMetadataCached(golfer);
    }

    private void EnsurePlayerGolferMetadataCached(Component golfer)
    {
        if (golfer == null)
        {
            return;
        }

        if (ReferenceEquals(initializedPlayerGolfer, golfer))
        {
            return;
        }

        initializedPlayerGolfer = golfer;
        playerGolferProperties.Clear();
        playerGolferFields.Clear();
        cachedTryStartChargingSwingMethod = null;
        cachedSetIsChargingSwingMethod = null;
        cachedReleaseSwingChargeMethod = null;
        cachedUpdateSwingNormalizedPowerMethod = null;
        swingNormalizedPowerBackingField = null;

        try
        {
            Type golferType = golfer.GetType();
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            string[] propertyNames =
            {
                "SwingNormalizedPower",
                "SwingPitch",
                "IsChargingSwing",
                "IsSwinging",
                "IsAimingSwing",
                "OwnBall"
            };

            for (int i = 0; i < propertyNames.Length; i++)
            {
                PropertyInfo property = golferType.GetProperty(propertyNames[i], flags);
                if (property != null)
                {
                    playerGolferProperties[propertyNames[i]] = property;
                }
            }

            string[] fieldNames =
            {
                "swingPowerTimestamp"
            };

            for (int i = 0; i < fieldNames.Length; i++)
            {
                FieldInfo field = golferType.GetField(fieldNames[i], flags);
                if (field != null)
                {
                    playerGolferFields[fieldNames[i]] = field;
                }
            }

            cachedTryStartChargingSwingMethod = golferType.GetMethod("TryStartChargingSwing", flags);

            cachedSetIsChargingSwingMethod = golferType.GetMethod("SetIsChargingSwing", BindingFlags.NonPublic | BindingFlags.Instance);

            cachedReleaseSwingChargeMethod = golferType.GetMethod("ReleaseSwingCharge", flags);

            cachedUpdateSwingNormalizedPowerMethod = golferType.GetMethod("UpdateSwingNormalizedPower", BindingFlags.NonPublic | BindingFlags.Instance);
        }
        catch
        {
        }
    }

    private bool TryGetCurrentSwingValues(out float swingPower, out float swingPitch, out bool isChargingSwing, out bool isSwinging)
    {
        swingPower = idealSwingPower;
        swingPitch = idealSwingPitch;
        isChargingSwing = false;
        isSwinging = false;

        if (playerGolfer == null)
        {
            return false;
        }

        try
        {
            PropertyInfo powerProperty;
            PropertyInfo pitchProperty;
            PropertyInfo chargingProperty;
            PropertyInfo swingingProperty;
            if (!playerGolferProperties.TryGetValue("SwingNormalizedPower", out powerProperty) ||
                !playerGolferProperties.TryGetValue("SwingPitch", out pitchProperty) ||
                !playerGolferProperties.TryGetValue("IsChargingSwing", out chargingProperty) ||
                !playerGolferProperties.TryGetValue("IsSwinging", out swingingProperty))
            {
                return false;
            }

            swingPower = Convert.ToSingle(powerProperty.GetValue(playerGolfer, null));
            swingPitch = Convert.ToSingle(pitchProperty.GetValue(playerGolfer, null));
            isChargingSwing = Convert.ToBoolean(chargingProperty.GetValue(playerGolfer, null));
            isSwinging = Convert.ToBoolean(swingingProperty.GetValue(playerGolfer, null));

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void InitializeLocalGolferResolver()
    {
        if (localGolferResolverInitialized)
        {
            return;
        }

        localGolferResolverInitialized = true;
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type gameManagerType = assemblies[i].GetType("GameManager");
                if (gameManagerType == null)
                {
                    continue;
                }

                PropertyInfo property = gameManagerType.GetProperty("LocalPlayerAsGolfer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null)
                {
                    cachedLocalPlayerAsGolferProperty = property;
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private bool TryResolveLocalPlayerGolferViaGameManager()
    {
        InitializeLocalGolferResolver();
        if (cachedLocalPlayerAsGolferProperty == null)
        {
            return false;
        }

        try
        {
            Component golfer = cachedLocalPlayerAsGolferProperty.GetValue(null, null) as Component;
            if (golfer == null)
            {
                return false;
            }

            if (!ReferenceEquals(playerGolfer, golfer))
            {
                playerGolfer = golfer;
                CachePlayerGolferAccessors(golfer);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureLocalGolfBallReference(bool force)
    {
        if (nearestAnyBallModeEnabled)
        {
            return EnsureNearestAnyGolfBallReference(force);
        }

        if (!force && golfBall != null && golfBall.gameObject != null)
        {
            hadResolvedBallContext = true;
            return true;
        }

        float currentTime = Time.time;
        if (!force && currentTime < nextBallResolveTime)
        {
            return golfBall != null;
        }

        nextBallResolveTime = currentTime + ballResolveInterval;

        Component resolvedBall = null;
        string resolvedSource = "missing";

        TryResolveLocalPlayerGolferViaGameManager();

        if (TryGetOwnBallFromGolfer(playerGolfer, out resolvedBall))
        {
            resolvedSource = "OwnBall";
        }

        if (resolvedBall == null)
        {
            HandleTrackedGolfBallChanged(golfBall, null);
            golfBall = null;
            lastBallResolveSource = "missing";
            hadResolvedBallContext = false;
            return false;
        }

        HandleTrackedGolfBallChanged(golfBall, resolvedBall);
        golfBall = resolvedBall;
        lastBallResolveSource = resolvedSource;
        hadResolvedBallContext = true;
        return true;
    }

    private bool EnsureNearestAnyGolfBallReference(bool force)
    {
        if (playerMovement == null && playerGolfer == null)
        {
            golfBall = null;
            lastBallResolveSource = "missing";
            hadResolvedBallContext = false;
            return false;
        }

        float currentTime = Time.time;
        if (!force && golfBall != null && golfBall.gameObject != null && currentTime < nextNearestAnyBallResolveTime)
        {
            hadResolvedBallContext = true;
            return true;
        }

        nextNearestAnyBallResolveTime = currentTime + nearestAnyBallResolveInterval;

        Component nearestBall = FindNearestGolfBallToPlayer(force);
        if (nearestBall == null)
        {
            HandleTrackedGolfBallChanged(golfBall, null);
            golfBall = null;
            lastBallResolveSource = "missing";
            hadResolvedBallContext = false;
            return false;
        }

        HandleTrackedGolfBallChanged(golfBall, nearestBall);
        golfBall = nearestBall;
        lastBallResolveSource = "nearest-any";
        hadResolvedBallContext = true;
        return true;
    }

    private void HandleTrackedGolfBallChanged(Component previousBall, Component nextBall)
    {
        if (ReferenceEquals(previousBall, nextBall))
        {
            return;
        }

        ResetTrailState();
        HideImpactPreview();
        nextPredictedPathRefreshTime = 0f;
        // E15d: invalidate wind-factor + hole caches so the next frame re-resolves
        // against the new ball. In multiplayer the golfBall reference swaps every
        // hole change; stale Hittable component pointers threw NRE every frame.
        ballWindSettingsInitialized = false;
        cachedBallHittableComponent = null;
        solveCacheValid = false;
        lastShotPathBallPosition = nextBall != null
            ? nextBall.transform.position + Vector3.up * shotPathHeightOffset
            : Vector3.zero;
    }

    private void RefreshGolfBallCache(bool force)
    {
        float currentTime = Time.time;
        if (!force && currentTime < nextGolfBallCacheRefreshTime)
        {
            return;
        }

        cachedGolfBalls.Clear();

        Component[] allComponents = FindAllComponents();
        for (int i = 0; i < allComponents.Length; i++)
        {
            Component component = allComponents[i];
            if (component == null || component.gameObject == null || component.GetType().Name != "GolfBall")
            {
                continue;
            }

            cachedGolfBalls.Add(component);
        }

        nextGolfBallCacheRefreshTime = currentTime + (cachedGolfBalls.Count > 0 ? golfBallCacheRefreshInterval : emptyGolfBallCacheRefreshInterval);
    }

    private Component FindNearestGolfBallToPlayer(bool forceRefreshCache)
    {
        Vector3 referencePosition;
        if (playerMovement != null)
        {
            referencePosition = playerMovement.transform.position;
        }
        else if (playerGolfer != null)
        {
            referencePosition = playerGolfer.transform.position;
        }
        else
        {
            return null;
        }

        RefreshGolfBallCache(forceRefreshCache);
        Component nearestBall = null;
        float nearestDistanceSq = float.MaxValue;

        for (int i = cachedGolfBalls.Count - 1; i >= 0; i--)
        {
            Component component = cachedGolfBalls[i];
            if (component == null || component.gameObject == null)
            {
                cachedGolfBalls.RemoveAt(i);
                continue;
            }

            float distanceSq = (component.transform.position - referencePosition).sqrMagnitude;
            if (distanceSq < nearestDistanceSq)
            {
                nearestDistanceSq = distanceSq;
                nearestBall = component;
            }
        }

        return nearestBall;
    }

    private bool TryGetOwnBallFromGolfer(Component golfer, out Component ownBall)
    {
        ownBall = null;
        if (golfer == null)
        {
            return false;
        }

        try
        {
            PropertyInfo property;
            if (playerGolferProperties.TryGetValue("OwnBall", out property) && property != null)
            {
                ownBall = property.GetValue(golfer, null) as Component;
                return ownBall != null;
            }
        }
        catch
        {
        }

        return false;
    }

    private bool TryResolveDisplayNameFromObject(object source, out string displayName)
    {
        displayName = null;
        if (source == null)
        {
            return false;
        }

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type sourceType = source.GetType();
        string[] preferredMembers =
        {
            "PlayerName",
            "displayName",
            "playerNickname",
            "NetworkplayerName"
        };

        for (int i = 0; i < preferredMembers.Length; i++)
        {
            string memberName = preferredMembers[i];

            PropertyInfo property = sourceType.GetProperty(memberName, flags);
            if (property != null && property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    string propertyValue = property.GetValue(source, null) as string;
                    if (ModTextHelper.TryGetValidDisplayName(propertyValue, out displayName))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            FieldInfo field = sourceType.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(string))
            {
                try
                {
                    string fieldValue = field.GetValue(source) as string;
                    if (ModTextHelper.TryGetValidDisplayName(fieldValue, out displayName))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private string GetLocalPlayerDisplayName()
    {
        if (playerMovement == null)
        {
            return "Searching...";
        }

        float currentTime = Time.time;
        if (currentTime < nextDisplayNameRefreshTime && !string.IsNullOrEmpty(cachedLocalPlayerDisplayName))
        {
            return cachedLocalPlayerDisplayName;
        }

        nextDisplayNameRefreshTime = currentTime + displayNameRefreshInterval;

        string resolvedName;
        if (TryResolveDisplayNameFromObject(playerMovement, out resolvedName))
        {
            cachedLocalPlayerDisplayName = resolvedName;
            return cachedLocalPlayerDisplayName;
        }

        GameObject playerObject = playerMovement.gameObject;
        if (playerObject != null)
        {
            Component[] components = playerObject.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component == playerMovement)
                {
                    continue;
                }

                if (TryResolveDisplayNameFromObject(component, out resolvedName))
                {
                    cachedLocalPlayerDisplayName = resolvedName;
                    return cachedLocalPlayerDisplayName;
                }
            }

            string fallbackName = ModTextHelper.NormalizeDisplayName(playerObject.name);
            if (!string.IsNullOrEmpty(fallbackName))
            {
                cachedLocalPlayerDisplayName = fallbackName;
                return cachedLocalPlayerDisplayName;
            }
        }

        cachedLocalPlayerDisplayName = "Unknown";
        return cachedLocalPlayerDisplayName;
    }

    private GameObject[] FindAllGameObjects()
    {
        int currentFrame = Time.frameCount;
        if (cachedAllGameObjects == null || cachedAllGameObjectsFrame != currentFrame)
        {
            cachedAllGameObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            cachedAllGameObjectsFrame = currentFrame;
        }

        return cachedAllGameObjects;
    }

    private Component[] FindAllComponents()
    {
        int currentFrame = Time.frameCount;
        if (cachedAllComponents == null || cachedAllComponentsFrame != currentFrame)
        {
            cachedAllComponents = UnityEngine.Object.FindObjectsByType<Component>(FindObjectsSortMode.None);
            cachedAllComponentsFrame = currentFrame;
        }

        return cachedAllComponents;
    }
}
