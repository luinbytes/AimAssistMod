using UnityEngine;
using UnityEngine.UI;

public partial class MimiMod
{
    private GameObject impactPreviewPanelObject;
    private RawImage impactPreviewRawImage;
    private RenderTexture impactPreviewTexture;
    private GameObject impactPreviewCameraObject;
    private Camera impactPreviewCamera;
    private readonly float impactPreviewLookHeightOffset = 0.65f;
    private readonly float impactPreviewProbeHeight = 36f;
    private readonly float impactPreviewCameraMinDistance = 9.5f;
    private readonly float impactPreviewCameraMaxDistance = 20f;
    private readonly float impactPreviewCameraMinHeight = 6.2f;
    private readonly float impactPreviewCameraMaxHeight = 12.5f;
    private readonly float impactPreviewFieldOfView = 52f;
    private readonly float impactPreviewOrbitDegreesPerSecond = 26f;
    private readonly float impactPreviewGroundClearance = 1.85f;
    private readonly float impactPreviewMinVerticalLookOffset = 2.25f;

    private void CreateImpactPreviewHud(Transform parent)
    {
        if (impactPreviewPanelObject != null)
        {
            return;
        }

        impactPreviewPanelObject = new GameObject("MimiImpactPreviewPanel");
        impactPreviewPanelObject.transform.SetParent(parent, false);

        RectTransform panelRect = impactPreviewPanelObject.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.offsetMin = new Vector2(-374f, -274f);
        panelRect.offsetMax = new Vector2(-14f, -54f);

        Image panelBackground = impactPreviewPanelObject.AddComponent<Image>();
        panelBackground.color = new Color(0.03f, 0.05f, 0.07f, 0.92f);

        GameObject viewportObject = new GameObject("MimiImpactPreviewViewport");
        viewportObject.transform.SetParent(impactPreviewPanelObject.transform, false);

        RectTransform viewportRect = viewportObject.AddComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(3f, 3f);
        viewportRect.offsetMax = new Vector2(-3f, -3f);

        impactPreviewRawImage = viewportObject.AddComponent<RawImage>();
        impactPreviewRawImage.color = Color.white;
        impactPreviewRawImage.raycastTarget = false;

        impactPreviewPanelObject.SetActive(false);
    }

    private void EnsureImpactPreviewResources()
    {
        if (impactPreviewTexture == null ||
            impactPreviewTexture.width != impactPreviewTextureWidth ||
            impactPreviewTexture.height != impactPreviewTextureHeight)
        {
            if (impactPreviewTexture != null)
            {
                if (impactPreviewCamera != null && impactPreviewCamera.targetTexture == impactPreviewTexture)
                {
                    impactPreviewCamera.targetTexture = null;
                }

                impactPreviewTexture.Release();
                UnityEngine.Object.Destroy(impactPreviewTexture);
            }

            impactPreviewTexture = new RenderTexture(impactPreviewTextureWidth, impactPreviewTextureHeight, 16);
            impactPreviewTexture.name = "MimiImpactPreviewTexture";
            impactPreviewTexture.antiAliasing = 1;
            impactPreviewTexture.Create();
        }

        if (impactPreviewCamera == null)
        {
            impactPreviewCameraObject = new GameObject("MimiImpactPreviewCamera");
            UnityEngine.Object.DontDestroyOnLoad(impactPreviewCameraObject);

            impactPreviewCamera = impactPreviewCameraObject.AddComponent<Camera>();
            impactPreviewCamera.enabled = false;
            impactPreviewCamera.depth = -100f;
            impactPreviewCamera.targetTexture = impactPreviewTexture;
            impactPreviewCamera.fieldOfView = impactPreviewFieldOfView;
            impactPreviewCamera.nearClipPlane = 0.05f;
            impactPreviewCamera.farClipPlane = 500f;
            impactPreviewCamera.clearFlags = CameraClearFlags.Skybox;
            impactPreviewCamera.backgroundColor = new Color(0.09f, 0.11f, 0.14f, 1f);
            impactPreviewCamera.cullingMask = -1;
        }

        if (impactPreviewRawImage != null && impactPreviewRawImage.texture != impactPreviewTexture)
        {
            impactPreviewRawImage.texture = impactPreviewTexture;
        }
    }

    private void UpdateImpactPreview()
    {
        if (impactPreviewPanelObject == null || impactPreviewRawImage == null)
        {
            return;
        }

        if (!assistEnabled || !impactPreviewEnabled)
        {
            HideImpactPreview();
            return;
        }

        Vector3 focusPoint;
        Vector3 approachDirection;
        if (!TryGetCachedImpactPreviewFocus(out focusPoint, out approachDirection))
        {
            HideImpactPreview();
            return;
        }

        EnsureImpactPreviewResources();
        if (impactPreviewCamera == null || impactPreviewTexture == null)
        {
            HideImpactPreview();
            return;
        }

        float currentTime = Time.unscaledTime;
        bool panelWasHidden = !impactPreviewPanelObject.activeSelf;
        float renderInterval = GetImpactPreviewRenderInterval();
        if (!panelWasHidden && renderInterval > 0f && currentTime < nextImpactPreviewRenderTime)
        {
            return;
        }

        if (cachedImpactPreviewReferenceCamera == null ||
            !cachedImpactPreviewReferenceCamera.isActiveAndEnabled ||
            currentTime >= nextImpactPreviewReferenceCameraRefreshTime)
        {
            cachedImpactPreviewReferenceCamera = FindReferenceGameplayCamera();
            nextImpactPreviewReferenceCameraRefreshTime = currentTime + impactPreviewReferenceCameraRefreshInterval;
        }

        SyncImpactPreviewCameraSettings(cachedImpactPreviewReferenceCamera);
        PositionImpactPreviewCamera(focusPoint, approachDirection);

        try
        {
            impactPreviewCamera.Render();
            nextImpactPreviewRenderTime = renderInterval > 0f ? currentTime + renderInterval : 0f;
            if (panelWasHidden)
            {
                impactPreviewPanelObject.SetActive(true);
            }
        }
        catch
        {
            HideImpactPreview();
        }
    }

    private void HideImpactPreview()
    {
        if (impactPreviewPanelObject != null && impactPreviewPanelObject.activeSelf)
        {
            impactPreviewPanelObject.SetActive(false);
        }
        nextImpactPreviewRenderTime = 0f;
    }

    private float GetImpactPreviewRenderInterval()
    {
        float targetFps = impactPreviewTargetFps > 0.01f ? impactPreviewTargetFps : impactPreviewAutoTargetFps;
        return targetFps > 0.01f ? 1f / targetFps : 0f;
    }

    private bool TryGetCachedImpactPreviewFocus(out Vector3 focusPoint, out Vector3 approachDirection)
    {
        if (predictedImpactPreviewValid)
        {
            focusPoint = predictedImpactPreviewPoint;
            approachDirection = predictedImpactPreviewApproachDirection;
            return true;
        }

        if (frozenImpactPreviewValid)
        {
            focusPoint = frozenImpactPreviewPoint;
            approachDirection = frozenImpactPreviewApproachDirection;
            return true;
        }

        focusPoint = Vector3.zero;
        approachDirection = GetFallbackPreviewDirection();
        return false;
    }

    private void ResetImpactPreviewCache(bool resetPredicted, bool resetFrozen)
    {
        if (resetPredicted)
        {
            predictedImpactPreviewValid = false;
            predictedImpactPreviewPoint = Vector3.zero;
            predictedImpactPreviewApproachDirection = Vector3.forward;
        }

        if (resetFrozen)
        {
            frozenImpactPreviewValid = false;
            frozenImpactPreviewPoint = Vector3.zero;
            frozenImpactPreviewApproachDirection = Vector3.forward;
        }
    }

    private void StoreImpactPreviewData(System.Collections.Generic.List<Vector3> sourcePoints, bool hasImpactPoint, Vector3 impactPoint, Vector3 approachDirection)
    {
        bool isPredictedPath = ReferenceEquals(sourcePoints, predictedPathPoints);
        bool isFrozenPath = ReferenceEquals(sourcePoints, frozenPredictedPathPoints);
        if (!isPredictedPath && !isFrozenPath)
        {
            return;
        }

        if (!hasImpactPoint)
        {
            if (sourcePoints == null || sourcePoints.Count == 0)
            {
                ResetImpactPreviewCache(isPredictedPath, isFrozenPath);
                return;
            }

            impactPoint = sourcePoints[sourcePoints.Count - 1];
            if (sourcePoints.Count >= 2)
            {
                Vector3 fallbackDirection = sourcePoints[sourcePoints.Count - 1] - sourcePoints[sourcePoints.Count - 2];
                if (fallbackDirection.sqrMagnitude >= 0.0001f)
                {
                    approachDirection = fallbackDirection.normalized;
                }
            }

            ProjectImpactPreviewPointToSurface(ref impactPoint);
        }

        if (approachDirection.sqrMagnitude < 0.0001f)
        {
            approachDirection = GetFallbackPreviewDirection();
        }
        else
        {
            approachDirection.Normalize();
        }

        if (isPredictedPath)
        {
            predictedImpactPreviewValid = true;
            predictedImpactPreviewPoint = impactPoint;
            predictedImpactPreviewApproachDirection = approachDirection;
        }

        if (isFrozenPath)
        {
            frozenImpactPreviewValid = true;
            frozenImpactPreviewPoint = impactPoint;
            frozenImpactPreviewApproachDirection = approachDirection;
        }
    }

    private bool TryFindWorldImpactAlongSegment(Vector3 startPoint, Vector3 endPoint, out RaycastHit resolvedHit)
    {
        resolvedHit = default(RaycastHit);

        Vector3 segment = endPoint - startPoint;
        float segmentDistance = segment.magnitude;
        if (segmentDistance <= 0.0001f)
        {
            return false;
        }

        Vector3 segmentDirection = segment / segmentDistance;
        int hitCount = Physics.RaycastNonAlloc(
            startPoint,
            segmentDirection,
            impactPreviewRaycastHits,
            segmentDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float bestDistance = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit candidateHit = impactPreviewRaycastHits[i];
            if (ShouldIgnoreImpactPreviewCollider(candidateHit.collider))
            {
                continue;
            }

            if (candidateHit.distance < bestDistance)
            {
                bestDistance = candidateHit.distance;
                resolvedHit = candidateHit;
            }
        }

        return bestDistance < float.MaxValue;
    }

    private void ProjectImpactPreviewPointToSurface(ref Vector3 point)
    {
        Vector3 probeOrigin = point + Vector3.up * impactPreviewProbeHeight;
        int hitCount = Physics.RaycastNonAlloc(
            probeOrigin,
            Vector3.down,
            impactPreviewGroundProbeHits,
            impactPreviewProbeHeight * 2f,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float bestDistance = float.MaxValue;
        bool foundSurface = false;
        RaycastHit bestHit = default(RaycastHit);

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit candidateHit = impactPreviewGroundProbeHits[i];
            if (ShouldIgnoreImpactPreviewCollider(candidateHit.collider))
            {
                continue;
            }

            if (candidateHit.distance < bestDistance)
            {
                bestDistance = candidateHit.distance;
                bestHit = candidateHit;
                foundSurface = true;
            }
        }

        if (foundSurface)
        {
            point = bestHit.point;
        }
    }

    private bool ShouldIgnoreImpactPreviewCollider(Collider collider)
    {
        if (collider == null || collider.isTrigger)
        {
            return true;
        }

        Transform colliderTransform = collider.transform;
        if (golfBall != null && colliderTransform.IsChildOf(golfBall.transform))
        {
            return true;
        }

        if (playerGolfer != null && colliderTransform.IsChildOf(playerGolfer.transform))
        {
            return true;
        }

        if (playerMovement != null && colliderTransform.IsChildOf(playerMovement.transform))
        {
            return true;
        }

        return impactPreviewCameraObject != null && colliderTransform.IsChildOf(impactPreviewCameraObject.transform);
    }

    private Vector3 GetFallbackPreviewDirection()
    {
        if (golfBall != null && currentAimTargetPosition != Vector3.zero)
        {
            Vector3 targetDirection = currentAimTargetPosition - golfBall.transform.position;
            targetDirection.y = 0f;
            if (targetDirection.sqrMagnitude >= 0.0001f)
            {
                return targetDirection.normalized;
            }
        }

        if (playerGolfer != null)
        {
            Vector3 forward = playerGolfer.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude >= 0.0001f)
            {
                return forward.normalized;
            }
        }

        return Vector3.forward;
    }

    private Camera FindReferenceGameplayCamera()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera != null &&
            mainCamera != impactPreviewCamera &&
            mainCamera.isActiveAndEnabled &&
            mainCamera.targetTexture == null)
        {
            return mainCamera;
        }

        return null;
    }

    private void SyncImpactPreviewCameraSettings(Camera referenceCamera)
    {
        if (impactPreviewCamera == null)
        {
            return;
        }

        if (referenceCamera != null)
        {
            impactPreviewCamera.clearFlags = referenceCamera.clearFlags;
            impactPreviewCamera.backgroundColor = referenceCamera.backgroundColor;
            impactPreviewCamera.cullingMask = referenceCamera.cullingMask;
            impactPreviewCamera.allowHDR = referenceCamera.allowHDR;
            impactPreviewCamera.allowMSAA = referenceCamera.allowMSAA;
            impactPreviewCamera.useOcclusionCulling = referenceCamera.useOcclusionCulling;
            impactPreviewCamera.depthTextureMode = referenceCamera.depthTextureMode;
            impactPreviewCamera.renderingPath = referenceCamera.renderingPath;
            impactPreviewCamera.nearClipPlane = Mathf.Max(0.03f, referenceCamera.nearClipPlane);
            impactPreviewCamera.farClipPlane = Mathf.Max(200f, referenceCamera.farClipPlane);
        }
        else
        {
            impactPreviewCamera.clearFlags = CameraClearFlags.Skybox;
            impactPreviewCamera.backgroundColor = new Color(0.09f, 0.11f, 0.14f, 1f);
            impactPreviewCamera.cullingMask = -1;
            impactPreviewCamera.allowHDR = true;
            impactPreviewCamera.allowMSAA = true;
            impactPreviewCamera.useOcclusionCulling = true;
            impactPreviewCamera.depthTextureMode = DepthTextureMode.None;
            impactPreviewCamera.renderingPath = RenderingPath.UsePlayerSettings;
            impactPreviewCamera.nearClipPlane = 0.05f;
            impactPreviewCamera.farClipPlane = 500f;
        }

        impactPreviewCamera.fieldOfView = impactPreviewFieldOfView;
        impactPreviewCamera.targetTexture = impactPreviewTexture;
    }

    private void PositionImpactPreviewCamera(Vector3 focusPoint, Vector3 approachDirection)
    {
        Vector3 flatApproach = new Vector3(approachDirection.x, 0f, approachDirection.z);
        if (flatApproach.sqrMagnitude < 0.0001f)
        {
            flatApproach = GetFallbackPreviewDirection();
        }

        flatApproach.Normalize();

        float shotDistance = golfBall != null
            ? Vector3.Distance(golfBall.transform.position, focusPoint)
            : 24f;
        float orbitAngle = Time.unscaledTime * impactPreviewOrbitDegreesPerSecond;
        flatApproach = Quaternion.AngleAxis(orbitAngle, Vector3.up) * flatApproach;

        float cameraDistance = Mathf.Clamp(shotDistance * 0.24f + 7.5f, impactPreviewCameraMinDistance, impactPreviewCameraMaxDistance);
        float cameraHeight = Mathf.Lerp(impactPreviewCameraMinHeight, impactPreviewCameraMaxHeight, Mathf.InverseLerp(0f, 80f, shotDistance));
        Vector3 lookPoint = focusPoint + Vector3.up * impactPreviewLookHeightOffset;
        Vector3 desiredPosition = lookPoint - flatApproach * cameraDistance + Vector3.up * cameraHeight;

        ClampImpactPreviewPositionAboveGround(lookPoint, ref desiredPosition);

        impactPreviewCamera.transform.position = desiredPosition;
        impactPreviewCamera.transform.rotation = Quaternion.LookRotation((lookPoint - desiredPosition).normalized, Vector3.up);
    }

    private void ClampImpactPreviewPositionAboveGround(Vector3 lookPoint, ref Vector3 desiredPosition)
    {
        float minimumY = lookPoint.y + impactPreviewMinVerticalLookOffset;
        desiredPosition.y = Mathf.Max(desiredPosition.y, minimumY);

        Vector3 probeOrigin = new Vector3(desiredPosition.x, desiredPosition.y + impactPreviewProbeHeight, desiredPosition.z);
        int hitCount = Physics.RaycastNonAlloc(
            probeOrigin,
            Vector3.down,
            impactPreviewGroundProbeHits,
            impactPreviewProbeHeight * 2f,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float bestDistance = float.MaxValue;
        bool foundSurface = false;
        RaycastHit bestHit = default(RaycastHit);

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit candidateHit = impactPreviewGroundProbeHits[i];
            if (ShouldIgnoreImpactPreviewCollider(candidateHit.collider))
            {
                continue;
            }

            if (candidateHit.distance < bestDistance)
            {
                bestDistance = candidateHit.distance;
                bestHit = candidateHit;
                foundSurface = true;
            }
        }

        if (foundSurface)
        {
            desiredPosition.y = Mathf.Max(desiredPosition.y, bestHit.point.y + impactPreviewGroundClearance, minimumY);
        }
    }
}
