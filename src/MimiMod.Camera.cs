using System;
using System.Reflection;
using UnityEngine;

public partial class MimiMod
{
    private void InitializeReflectionCache()
    {
        if (reflectionCacheInitialized)
        {
            return;
        }

        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];

                if (cachedTryGetMethod == null)
                {
                    Type controllerType = assembly.GetType("CameraModuleController");
                    if (controllerType != null)
                    {
                        cachedTryGetMethod = controllerType.GetMethod("TryGetOrbitModule", BindingFlags.Public | BindingFlags.Static);
                    }
                }

                if (cachedEnterSwingAimCameraMethod == null)
                {
                    Type gameplayCameraManagerType = assembly.GetType("GameplayCameraManager");
                    if (gameplayCameraManagerType != null)
                    {
                        cachedEnterSwingAimCameraMethod = gameplayCameraManagerType.GetMethod("EnterSwingAimCamera", BindingFlags.Public | BindingFlags.Static);
                        cachedExitSwingAimCameraMethod = gameplayCameraManagerType.GetMethod("ExitSwingAimCamera", BindingFlags.Public | BindingFlags.Static);
                        cachedReachOrbitSteadyStateMethod = gameplayCameraManagerType.GetMethod("ReachOrbitCameraSteadyState", BindingFlags.Public | BindingFlags.Static);
                    }
                }

                if (cachedTryGetMethod != null && cachedEnterSwingAimCameraMethod != null)
                {
                    break;
                }
            }

            object orbitModule = TryGetOrbitModule();
            if (orbitModule != null)
            {
                Type orbitType = orbitModule.GetType();
                cachedOrbitSetYawMethod = orbitType.GetMethod("SetYaw", BindingFlags.Public | BindingFlags.Instance);
                cachedOrbitSetPitchMethod = orbitType.GetMethod("SetPitch", BindingFlags.Public | BindingFlags.Instance);
                cachedOrbitForceUpdateMethod = orbitType.GetMethod("ForceUpdateModule", BindingFlags.Public | BindingFlags.Instance);
            }
        }
        catch
        {
        }

        reflectionCacheInitialized = true;
    }

    private object TryGetOrbitModule()
    {
        if (cachedTryGetMethod == null)
        {
            return null;
        }

        try
        {
            cachedOrbitModuleQueryArgs[0] = null;
            bool result = (bool)cachedTryGetMethod.Invoke(null, cachedOrbitModuleQueryArgs);
            return result ? cachedOrbitModuleQueryArgs[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private FieldInfo FindYawField(Type componentType)
    {
        FieldInfo field = componentType.GetField("targetYaw", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field != null && field.FieldType == typeof(float) ? field : null;
    }

    private void CacheYawAccessorsForComponent(
        Component component,
        ref Component initializedComponent,
        ref PropertyInfo yawProperty,
        ref FieldInfo yawField)
    {
        if (component == null)
        {
            initializedComponent = null;
            yawProperty = null;
            yawField = null;
            return;
        }

        if (ReferenceEquals(initializedComponent, component))
        {
            return;
        }

        initializedComponent = component;
        Type componentType = component.GetType();
        yawProperty = componentType.GetProperty("Yaw", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        yawField = FindYawField(componentType);
    }

    private void EnsureAimYawAccessors()
    {
        CacheYawAccessorsForComponent(
            playerMovement,
            ref initializedYawPlayerMovement,
            ref cachedPlayerMovementYawProperty,
            ref cachedPlayerMovementYawField);

        CacheYawAccessorsForComponent(
            playerGolfer,
            ref initializedYawPlayerGolfer,
            ref cachedPlayerGolferYawProperty,
            ref cachedPlayerGolferYawField);
    }

    private void ApplyYawToAimComponent(Component component, PropertyInfo yawProperty, FieldInfo yawField, float targetYaw)
    {
        if (component == null)
        {
            return;
        }

        try
        {
            if (yawProperty != null && yawProperty.CanWrite && yawProperty.PropertyType == typeof(float))
            {
                yawProperty.SetValue(component, targetYaw, null);
            }
        }
        catch
        {
        }

        try
        {
            if (yawField != null && yawField.FieldType == typeof(float))
            {
                yawField.SetValue(component, targetYaw);
            }
        }
        catch
        {
        }

        try
        {
            component.transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
        }
        catch
        {
        }
    }

    private void ApplyAimDirectionToSwingSubject(float targetYaw)
    {
        EnsureAimYawAccessors();

        ApplyYawToAimComponent(
            playerMovement,
            cachedPlayerMovementYawProperty,
            cachedPlayerMovementYawField,
            targetYaw);

        if (!ReferenceEquals(playerGolfer, playerMovement))
        {
            ApplyYawToAimComponent(
                playerGolfer,
                cachedPlayerGolferYawProperty,
                cachedPlayerGolferYawField,
                targetYaw);
        }
    }

    private void SetSwingAimCameraState(bool enabled)
    {
        if (enabled == wasAimRequestedLastFrame)
        {
            return;
        }

        wasAimRequestedLastFrame = enabled;

        MethodInfo method = enabled ? cachedEnterSwingAimCameraMethod : cachedExitSwingAimCameraMethod;
        if (method == null)
        {
            return;
        }

        try
        {
            method.Invoke(null, null);
            if (enabled && cachedReachOrbitSteadyStateMethod != null)
            {
                cachedReachOrbitSteadyStateMethod.Invoke(null, null);
            }
        }
        catch
        {
        }
    }

    private void DisableAutoAimCamera()
    {
        isAimModeActive = false;
        cameraAimSmoothingInitialized = false;
        orbitYawVelocity = 0f;
        orbitPitchVelocity = 0f;
        SetSwingAimCameraState(false);
    }

    private void AutoAimCamera()
    {
        bool shouldAim = assistEnabled && (isLeftMousePressed || isRightMousePressed);
        if (!shouldAim || playerGolfer == null || holePosition == Vector3.zero)
        {
            DisableAutoAimCamera();
            return;
        }

        try
        {
            InitializeReflectionCache();

            object orbitModule = TryGetOrbitModule();
            if (orbitModule == null || cachedOrbitSetYawMethod == null || cachedOrbitSetPitchMethod == null)
            {
                DisableAutoAimCamera();
                return;
            }

            SetSwingAimCameraState(true);

            Vector3 playerPosition = playerGolfer.transform.position;
            Vector3 aimReferencePosition = golfBall != null ? golfBall.transform.position : playerPosition;
            currentAimTargetPosition = GetAimTargetPosition(aimReferencePosition);
            currentSwingOriginPosition = GetSwingOriginPosition();

            if (currentAimTargetPosition == Vector3.zero || currentSwingOriginPosition == Vector3.zero)
            {
                DisableAutoAimCamera();
                return;
            }

            Vector3 flatAimDirection = currentAimTargetPosition - currentSwingOriginPosition;
            flatAimDirection.y = 0f;
            if (flatAimDirection.sqrMagnitude < 0.0001f)
            {
                DisableAutoAimCamera();
                return;
            }

            float targetYaw = Mathf.Atan2(flatAimDirection.x, flatAimDirection.z) * Mathf.Rad2Deg;
            float flatDistance = flatAimDirection.magnitude;
            float cameraDistance = Mathf.Clamp(flatDistance * 0.9f, 4f, 15f);
            float cameraHeight = Mathf.Lerp(3.25f, 8f, Mathf.InverseLerp(0f, 25f, flatDistance));
            Vector3 desiredPosition = playerPosition - flatAimDirection.normalized * cameraDistance;
            desiredPosition.y += cameraHeight;

            Vector3 lookDirection = (currentAimTargetPosition - desiredPosition).normalized;
            float targetPitch = -Mathf.Asin(Mathf.Clamp(lookDirection.y, -0.999f, 0.999f)) * Mathf.Rad2Deg;

            if (!cameraAimSmoothingInitialized || !isAimModeActive)
            {
                smoothedOrbitYaw = targetYaw;
                smoothedOrbitPitch = targetPitch;
                orbitYawVelocity = 0f;
                orbitPitchVelocity = 0f;
                cameraAimSmoothingInitialized = true;
            }
            else
            {
                float deltaTime = Mathf.Max(0.0001f, Time.deltaTime);
                smoothedOrbitYaw = Mathf.SmoothDampAngle(smoothedOrbitYaw, targetYaw, ref orbitYawVelocity, orbitAimSmoothTime, orbitAimMaxSpeed, deltaTime);
                smoothedOrbitPitch = Mathf.SmoothDampAngle(smoothedOrbitPitch, targetPitch, ref orbitPitchVelocity, orbitAimSmoothTime, orbitAimMaxSpeed, deltaTime);
            }

            ApplyAimDirectionToSwingSubject(smoothedOrbitYaw);

            cachedOrbitYawArgs[0] = smoothedOrbitYaw;
            cachedOrbitPitchArgs[0] = smoothedOrbitPitch;
            cachedOrbitSetYawMethod.Invoke(orbitModule, cachedOrbitYawArgs);
            cachedOrbitSetPitchMethod.Invoke(orbitModule, cachedOrbitPitchArgs);

            if (cachedOrbitForceUpdateMethod != null)
            {
                cachedOrbitForceUpdateMethod.Invoke(orbitModule, null);
            }

            isAimModeActive = true;
        }
        catch
        {
            DisableAutoAimCamera();
        }
    }
}
