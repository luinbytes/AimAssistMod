using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public partial class MimiMod
{
    private void MarkHudDirty()
    {
        hudDirty = true;
        nextHudRefreshTime = 0f;
    }

    private void MarkTrailVisualSettingsDirty()
    {
        trailVisualSettingsDirty = true;
        actualTrailLineDirty = true;
        predictedTrailLineDirty = true;
        frozenTrailLineDirty = true;
    }

    private void SetHudText(TextMeshProUGUI textComponent, string nextValue, ref string cachedValue)
    {
        if (textComponent == null || string.Equals(cachedValue, nextValue, System.StringComparison.Ordinal))
        {
            return;
        }

        cachedValue = nextValue;
        textComponent.text = nextValue;
    }

    private void CreateHud()
    {
        if (hudCanvas != null)
        {
            return;
        }

        hudCanvas = new GameObject("MimiHudCanvas");
        Canvas canvas = hudCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10000;

        CanvasScaler scaler = hudCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        hudCanvas.AddComponent<GraphicRaycaster>();
        UnityEngine.Object.DontDestroyOnLoad(hudCanvas);

        leftHudText = CreateHudText(
            hudCanvas.transform,
            "MimiHudLeft",
            new Vector2(0f, 0.86f),
            new Vector2(0.28f, 1f),
            new Vector2(14f, -12f),
            new Vector2(-14f, -14f),
            18,
            TextAlignmentOptions.TopLeft);

        centerHudText = CreateHudText(
            hudCanvas.transform,
            "MimiHudCenter",
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(-64f, -34f),
            new Vector2(64f, -10f),
            12,
            TextAlignmentOptions.Center);

        rightHudText = CreateHudText(
            hudCanvas.transform,
            "MimiHudRight",
            new Vector2(0.72f, 0.86f),
            new Vector2(1f, 1f),
            new Vector2(14f, -12f),
            new Vector2(-14f, -14f),
            18,
            TextAlignmentOptions.TopRight);

        bottomHudText = CreateHudText(
            hudCanvas.transform,
            "MimiHudBottom",
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(-520f, 28f),
            new Vector2(520f, 84f),
            18,
            TextAlignmentOptions.Bottom);

        CreateImpactPreviewHud(hudCanvas.transform);
        MarkHudDirty();
        UpdateHud(true);
    }

    private TextMeshProUGUI CreateHudText(
        Transform parent,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax,
        int fontSize,
        TextAlignmentOptions alignment)
    {
        GameObject textObject = new GameObject(name);
        textObject.transform.SetParent(parent, false);

        RectTransform rectTransform = textObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = anchorMin;
        rectTransform.anchorMax = anchorMax;
        rectTransform.offsetMin = offsetMin;
        rectTransform.offsetMax = offsetMax;

        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.richText = true;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.outlineColor = new Color(0f, 0f, 0f, 0.8f);
        text.outlineWidth = 0.2f;
        text.color = Color.white;
        return text;
    }

    private void UpdateHud(bool force = false)
    {
        if (leftHudText == null || centerHudText == null || rightHudText == null || bottomHudText == null)
        {
            return;
        }

        float currentTime = Time.unscaledTime;
        if (!force && !hudDirty && currentTime < nextHudRefreshTime)
        {
            return;
        }

        nextHudRefreshTime = currentTime + hudRefreshInterval;
        hudDirty = false;

        SetHudText(leftHudText, BuildLeftHudText(), ref cachedLeftHudText);
        SetHudText(centerHudText, "<b><color=#FFB255>Mimi</color></b>", ref cachedCenterHudText);
        SetHudText(rightHudText, BuildRightHudText(), ref cachedRightHudText);
        SetHudText(bottomHudText, BuildBottomHudText(), ref cachedBottomHudText);
    }

    private string BuildLeftHudText()
    {
        StringBuilder builder = new StringBuilder(64);
        builder.Append("<b><color=#8ED9FF>Player</color></b>\n");

        string displayName = GetLocalPlayerDisplayName();
        if (playerFound && playerMovement != null)
        {
            builder.Append("<color=#FFFFFF>").Append(ModTextHelper.EscapeRichText(displayName)).Append("</color>");
        }
        else
        {
            builder.Append("<color=#A8A8A8>Searching...</color>");
        }

        return builder.ToString();
    }

    private string BuildRightHudText()
    {
        string color = assistEnabled ? "#39FF8F" : "#FF5E5E";
        return "<b><color=" + color + ">Assist [" + ModTextHelper.EscapeRichText(assistToggleKeyLabel) + "]</color></b>";
    }

    private string BuildBottomHudText()
    {
        string assistHint = assistEnabled
            ? "<color=#8ED9FF>Hold RMB to aim camera</color>"
            : "<color=#A8A8A8>Press " + ModTextHelper.EscapeRichText(assistToggleKeyLabel) + " to enable assist</color>";
        string nearestBallColor = nearestAnyBallModeEnabled ? "#39FF8F" : "#A8A8A8";
        return assistHint +
               "  <color=#FFD06A>|</color>  <color=#FFD06A>" + ModTextHelper.EscapeRichText(coffeeBoostKeyLabel) + " - give speed boost</color>" +
               "  <color=#FFD06A>|</color>  <color=" + nearestBallColor + ">" + ModTextHelper.EscapeRichText(nearestBallModeKeyLabel) + " - nearest ball</color>" +
               "  <color=#FFD06A>|</color>  <color=#FFD06A>" + ModTextHelper.EscapeRichText(unlockAllCosmeticsKeyLabel) + " - unlock cosmetics</color>";
    }

    private void EnsureTrailRenderers()
    {
        bool createdRenderer = false;
        if (shotPathLine == null)
        {
            CreateActualTrailRenderer();
            createdRenderer = true;
        }

        if (predictedPathLine == null)
        {
            CreatePredictedTrailRenderer();
            createdRenderer = true;
        }

        if (frozenPredictedPathLine == null)
        {
            CreateFrozenTrailRenderer();
            createdRenderer = true;
        }

        if (createdRenderer || trailVisualSettingsDirty)
        {
            ApplyTrailVisualSettings();
            trailVisualSettingsDirty = false;
        }
    }

    private void CreateActualTrailRenderer()
    {
        shotPathObject = new GameObject("MimiShotTrail");
        UnityEngine.Object.DontDestroyOnLoad(shotPathObject);
        shotPathLine = shotPathObject.AddComponent<LineRenderer>();
        shotPathLine.positionCount = 0;
        shotPathLine.startWidth = 0.06f;
        shotPathLine.endWidth = 0.04f;
        shotPathLine.useWorldSpace = true;
        shotPathLine.numCapVertices = 10;
        shotPathLine.numCornerVertices = 10;
        shotPathLine.alignment = LineAlignment.View;
        shotPathLine.textureMode = LineTextureMode.Stretch;
        shotPathLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        shotPathLine.receiveShadows = false;
        shotPathLine.sortingOrder = 32760;

        Shader shader = Shader.Find("Sprites/Default");
        shotPathMaterial = new Material(shader);
        shotPathMaterial.color = new Color(1f, 0.58f, 0.20f, 1f);
        shotPathMaterial.renderQueue = 5000;
        if (shotPathMaterial.HasProperty("_ZWrite"))
        {
            shotPathMaterial.SetInt("_ZWrite", 0);
        }
        if (shotPathMaterial.HasProperty("_ZTest"))
        {
            shotPathMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }
        shotPathLine.material = shotPathMaterial;
    }

    private void CreatePredictedTrailRenderer()
    {
        predictedPathObject = new GameObject("MimiPredictedTrail");
        UnityEngine.Object.DontDestroyOnLoad(predictedPathObject);
        predictedPathLine = predictedPathObject.AddComponent<LineRenderer>();
        predictedPathLine.positionCount = 0;
        predictedPathLine.startWidth = 0.03f;
        predictedPathLine.endWidth = 0.02f;
        predictedPathLine.useWorldSpace = true;
        predictedPathLine.numCapVertices = 8;
        predictedPathLine.numCornerVertices = 8;
        predictedPathLine.alignment = LineAlignment.View;
        predictedPathLine.textureMode = LineTextureMode.Stretch;
        predictedPathLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        predictedPathLine.receiveShadows = false;
        predictedPathLine.sortingOrder = 32759;

        Shader shader = Shader.Find("Sprites/Default");
        predictedPathMaterial = new Material(shader);
        predictedPathMaterial.color = new Color(0.36f, 0.95f, 0.46f, 0.95f);
        predictedPathMaterial.renderQueue = 4999;
        if (predictedPathMaterial.HasProperty("_ZWrite"))
        {
            predictedPathMaterial.SetInt("_ZWrite", 0);
        }
        if (predictedPathMaterial.HasProperty("_ZTest"))
        {
            predictedPathMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }
        predictedPathLine.material = predictedPathMaterial;
    }

    private void CreateFrozenTrailRenderer()
    {
        frozenPredictedPathObject = new GameObject("MimiFrozenTrail");
        UnityEngine.Object.DontDestroyOnLoad(frozenPredictedPathObject);
        frozenPredictedPathLine = frozenPredictedPathObject.AddComponent<LineRenderer>();
        frozenPredictedPathLine.positionCount = 0;
        frozenPredictedPathLine.startWidth = 0.034f;
        frozenPredictedPathLine.endWidth = 0.024f;
        frozenPredictedPathLine.useWorldSpace = true;
        frozenPredictedPathLine.numCapVertices = 8;
        frozenPredictedPathLine.numCornerVertices = 8;
        frozenPredictedPathLine.alignment = LineAlignment.View;
        frozenPredictedPathLine.textureMode = LineTextureMode.Stretch;
        frozenPredictedPathLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        frozenPredictedPathLine.receiveShadows = false;
        frozenPredictedPathLine.sortingOrder = 32758;

        Shader shader = Shader.Find("Sprites/Default");
        frozenPredictedPathMaterial = new Material(shader);
        frozenPredictedPathMaterial.color = new Color(0.36f, 0.74f, 1f, 0.92f);
        frozenPredictedPathMaterial.renderQueue = 4998;
        if (frozenPredictedPathMaterial.HasProperty("_ZWrite"))
        {
            frozenPredictedPathMaterial.SetInt("_ZWrite", 0);
        }
        if (frozenPredictedPathMaterial.HasProperty("_ZTest"))
        {
            frozenPredictedPathMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }
        frozenPredictedPathLine.material = frozenPredictedPathMaterial;
    }

    private void ResetTrailState()
    {
        shotPathPoints.Clear();
        predictedPathPoints.Clear();
        frozenPredictedPathPoints.Clear();
        isRecordingShotPath = false;
        observedBallMotionSinceLastShot = false;
        lockLivePredictedPath = false;
        predictedPathCacheValid = false;
        predictedTrajectoryHideStartTime = 0f;
        lastShotPathMoveTime = 0f;
        lastShotPathBallPosition = golfBall != null ? golfBall.transform.position + Vector3.up * shotPathHeightOffset : Vector3.zero;
        actualTrailLineDirty = false;
        predictedTrailLineDirty = false;
        frozenTrailLineDirty = false;

        if (shotPathLine != null)
        {
            shotPathLine.positionCount = 0;
        }
        if (predictedPathLine != null)
        {
            predictedPathLine.positionCount = 0;
        }
        if (frozenPredictedPathLine != null)
        {
            frozenPredictedPathLine.positionCount = 0;
        }

        ResetImpactPreviewCache(true, true);
    }

    private void SyncLineRendererPoints(LineRenderer lineRenderer, System.Collections.Generic.List<Vector3> points, ref bool lineDirty, bool allowIncrementalAppend)
    {
        if (lineRenderer == null)
        {
            return;
        }

        int pointCount = points.Count;
        if (pointCount <= 0)
        {
            if (lineRenderer.positionCount != 0)
            {
                lineRenderer.positionCount = 0;
            }
            lineDirty = false;
            return;
        }

        if (!lineDirty && allowIncrementalAppend && lineRenderer.positionCount == pointCount - 1)
        {
            lineRenderer.positionCount = pointCount;
            lineRenderer.SetPosition(pointCount - 1, points[pointCount - 1]);
            return;
        }

        if (lineRenderer.positionCount != pointCount)
        {
            lineRenderer.positionCount = pointCount;
        }

        for (int i = 0; i < pointCount; i++)
        {
            lineRenderer.SetPosition(i, points[i]);
        }

        lineDirty = false;
    }

    private void ApplyActualTrailToLine()
    {
        if (shotPathLine == null || !actualTrailEnabled)
        {
            if (shotPathLine != null)
            {
                shotPathLine.positionCount = 0;
            }
            return;
        }

        SyncLineRendererPoints(shotPathLine, shotPathPoints, ref actualTrailLineDirty, true);
    }

    private void ApplyPredictedTrailToLine()
    {
        if (predictedPathLine == null || !predictedTrailEnabled)
        {
            if (predictedPathLine != null)
            {
                predictedPathLine.positionCount = 0;
            }
            return;
        }

        SyncLineRendererPoints(predictedPathLine, predictedPathPoints, ref predictedTrailLineDirty, false);
    }

    private void ApplyFrozenTrailToLine()
    {
        if (frozenPredictedPathLine == null || !frozenTrailEnabled)
        {
            if (frozenPredictedPathLine != null)
            {
                frozenPredictedPathLine.positionCount = 0;
            }
            return;
        }

        SyncLineRendererPoints(frozenPredictedPathLine, frozenPredictedPathPoints, ref frozenTrailLineDirty, false);
    }

    private void ClearPredictedTrails(bool hideFrozenSnapshot)
    {
        predictedPathPoints.Clear();
        predictedPathCacheValid = false;
        predictedTrailLineDirty = false;
        if (predictedPathLine != null)
        {
            predictedPathLine.positionCount = 0;
        }
        ResetImpactPreviewCache(true, false);

        if (hideFrozenSnapshot)
        {
            frozenPredictedPathPoints.Clear();
            frozenTrailLineDirty = false;
            if (frozenPredictedPathLine != null)
            {
                frozenPredictedPathLine.positionCount = 0;
            }
            ResetImpactPreviewCache(false, true);
        }
    }
}
