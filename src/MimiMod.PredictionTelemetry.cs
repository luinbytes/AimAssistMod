using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── Shot prediction telemetry ──────────────────────────────────────────────
    //
    // Captures every auto-fired shot so we can measure how close our wind
    // prediction was to reality. For each shot:
    //
    //   1. At the moment of release, snapshot:
    //      - frozen predicted landing (last point of frozenPredictedPathPoints)
    //      - ball origin, hole position, wind vector + magnitude
    //      - ball WindFactor / CrossWindFactor / airDragFactor
    //      - applied power, pitch, swing-power multiplier
    //
    //   2. Each frame after release, watch the ball:
    //      - record the first ground-impact position (when ballY <= holeY
    //        and velocity.y < 0, matching SimulateBallLandingPoint's termination)
    //      - wait until the ball comes to rest (velocity < 0.15)
    //
    //   3. When the ball stops, compute:
    //      - impact delta      = actual_impact - predicted_impact
    //      - final rest delta  = final_rest - predicted_impact
    //      - vs-hole miss      = actual_impact - hole_position
    //      Emit a structured log line and append a CSV row to
    //      Mods/SuperHackerGolf-telemetry.csv. Also keep the last 8 entries in
    //      memory so the settings GUI can show them live.
    //
    // Toggled by config key `telemetry_enabled` (default off). When off, no
    // captures, no logs, no CSV.

    private bool telemetryShotInProgress;
    private bool telemetryImpactRecorded;
    private bool telemetryBallHasLaunched;
    private bool telemetryIsRocketDriverAtRelease;
    private float telemetryMaxY;
    private float telemetryCaptureTime;
    private float telemetryPrevVelY;
    private Vector3 telemetryPrevBallPos;
    private Vector3 telemetryPredictedLanding;
    private Vector3 telemetryShotOrigin;
    private Vector3 telemetryHolePosition;
    private Vector3 telemetryWindAtRelease;
    private Vector3 telemetryActualImpact;
    private float telemetryBallWindFactorAtRelease;
    private float telemetryBallCrossWindFactorAtRelease;
    private float telemetryAirDragFactorAtRelease;
    private float telemetrySwingPowerMultiplierAtRelease;
    private float telemetryAppliedPower;
    private float telemetryShotPitch;
    private int telemetryShotCounter;

    private readonly float telemetryBallStopSpeed = 0.15f;
    private readonly float telemetryMinShotTime = 0.15f;
    private readonly float telemetryMaxShotTime = 20f;
    private readonly int telemetryMaxHistory = 8;
    private readonly List<string> telemetryRecentSummaries = new List<string>(8);
    private string telemetryCsvPath;
    private bool telemetryCsvHeaderWritten;

    /// <summary>
    /// Called from AutoSwingRelease right after FreezePredictedTrajectorySnapshot,
    /// so the frozen trajectory is populated and the ball is about to launch.
    /// </summary>
    internal void CaptureShotTelemetry(float appliedPower, float shotPitch)
    {
        if (!telemetryEnabled)
        {
            return;
        }
        if (golfBall == null)
        {
            return;
        }
        // Predicted landing source:
        //   frozenImpactPreviewPoint — the true raycast-derived ground impact
        //   (Mimi's forward sim continues past impact until step/time limits,
        //   so the last trajectory point is buried underground — useless).
        //
        // Fall back to frozen path last point ONLY if the raycast impact isn't
        // valid; that at least gives us a rough Y for comparison.
        Vector3 predictedLanding;
        if (frozenImpactPreviewValid)
        {
            predictedLanding = frozenImpactPreviewPoint;
        }
        else if (frozenPredictedPathPoints != null && frozenPredictedPathPoints.Count > 0)
        {
            predictedLanding = frozenPredictedPathPoints[frozenPredictedPathPoints.Count - 1];
        }
        else
        {
            return;
        }

        telemetryShotInProgress = true;
        telemetryImpactRecorded = false;
        telemetryBallHasLaunched = false;
        telemetryCaptureTime = Time.time;
        telemetryPredictedLanding = predictedLanding;
        telemetryShotOrigin = golfBall.transform.position;
        telemetryMaxY = telemetryShotOrigin.y;
        telemetryPrevVelY = 0f;
        telemetryPrevBallPos = telemetryShotOrigin;
        telemetryHolePosition = holePosition;
        telemetryWindAtRelease = GetCachedWindVector();
        telemetryBallWindFactorAtRelease = GetBallWindFactor();
        telemetryBallCrossWindFactorAtRelease = GetBallCrossWindFactor();
        telemetryAirDragFactorAtRelease = GetRuntimeLinearAirDragFactor();
        telemetryAppliedPower = appliedPower;
        telemetryShotPitch = shotPitch;
        telemetrySwingPowerMultiplierAtRelease = 1f;
        TryGetServerSwingPowerMultiplier(out telemetrySwingPowerMultiplierAtRelease);
        telemetryIsRocketDriverAtRelease = IsLocalPlayerUsingRocketDriver();
    }

    /// <summary>
    /// Called every frame from OnUpdate while a shot is in flight. Watches for
    /// first ground impact, then for ball stop, then logs + appends CSV.
    /// </summary>
    internal void UpdateShotTelemetry()
    {
        if (!telemetryShotInProgress)
        {
            return;
        }
        if (golfBall == null || golfBall.gameObject == null)
        {
            telemetryShotInProgress = false;
            return;
        }

        float elapsed = Time.time - telemetryCaptureTime;
        if (elapsed > telemetryMaxShotTime)
        {
            // Something went wrong — ball never stopped. Abort without logging.
            telemetryShotInProgress = false;
            return;
        }

        Vector3 ballPos = golfBall.transform.position;

        if (!TryGetGolfBallVelocity(out Vector3 ballVel))
        {
            return;
        }

        // Gate: wait for the ball to actually launch before considering impact.
        // This filters out the "ball is below targetY from frame zero" bug on
        // downhill shots AND handles the no-wind case where the ball might take
        // a frame or two to start moving after FreezePredictedTrajectorySnapshot.
        if (!telemetryBallHasLaunched)
        {
            if ((ballPos - telemetryShotOrigin).sqrMagnitude > 0.25f) // > 0.5m
            {
                telemetryBallHasLaunched = true;
            }
            else if (elapsed > 0.5f)
            {
                // Ball never launched — maybe FreezePredictedTrajectorySnapshot
                // was called without a real fire (e.g. manual swing rejected by
                // the game). Abort this telemetry capture quietly.
                telemetryShotInProgress = false;
                return;
            }
            else
            {
                return;
            }
        }

        // Track apex so we know the ball went airborne.
        if (ballPos.y > telemetryMaxY)
        {
            telemetryMaxY = ballPos.y;
        }

        // Detect first ground impact via VELOCITY SIGN CHANGE, not Y-threshold.
        // Previous approach (ballY <= predictedY + 0.1) missed most shots because
        // ball terrain Y was within epsilon of origin but never dipped below it.
        //
        // Signal: velocity.y was strongly negative last frame, now >= -0.1
        // (ball is no longer falling — just bounced, slid, or stopped). Use
        // the PREVIOUS ball position as the impact point because that's the
        // frame right before the bounce/stop, which is when the ball was
        // actually at ground level.
        if (!telemetryImpactRecorded && elapsed > telemetryMinShotTime)
        {
            bool wentAirborne = (telemetryMaxY - telemetryShotOrigin.y) > 0.3f;
            bool downhillShot = telemetryPredictedLanding.y < telemetryShotOrigin.y - 0.5f;
            if (wentAirborne || downhillShot)
            {
                bool wasFalling = telemetryPrevVelY < -0.5f;
                bool stoppedFalling = ballVel.y >= -0.1f;
                if (wasFalling && stoppedFalling)
                {
                    // Use previous ball pos (one frame before the bounce) —
                    // closer to the actual ground contact than the rebounded
                    // current position.
                    telemetryActualImpact = telemetryPrevBallPos;
                    telemetryImpactRecorded = true;
                }
            }
        }

        telemetryPrevVelY = ballVel.y;
        telemetryPrevBallPos = ballPos;

        // Wait for ball to rest before logging the final result.
        if (elapsed < telemetryMinShotTime || ballVel.sqrMagnitude > telemetryBallStopSpeed * telemetryBallStopSpeed)
        {
            return;
        }

        // Ball has stopped — log.
        telemetryShotCounter++;
        Vector3 finalRest = ballPos;

        // OOB detection: if the ball's final rest is very close to the shot
        // origin (< 1.5m) AND elapsed flight time is long enough that it
        // couldn't possibly be a dribble, the game almost certainly respawned
        // the ball at the tee due to out-of-bounds. Flag it so the CSV row
        // doesn't pollute the delta regression.
        bool outOfBounds = false;
        if (!telemetryImpactRecorded &&
            elapsed > 1.5f &&
            (finalRest - telemetryShotOrigin).sqrMagnitude < 2.25f) // < 1.5m
        {
            outOfBounds = true;
        }

        // E11: rocket driver (super club) flag — captured at shot release via
        // reflection on PlayerInfo.Inventory.GetEffectivelyEquippedItem. No
        // more >100% heuristic; this is the actual game truth.
        bool likelySuperClub = telemetryIsRocketDriverAtRelease;

        Vector3 actualImpact = telemetryImpactRecorded ? telemetryActualImpact : finalRest;
        Vector3 impactDelta = actualImpact - telemetryPredictedLanding;
        Vector3 restDelta = finalRest - telemetryPredictedLanding;
        Vector3 vsHole = actualImpact - telemetryHolePosition;

        float windMag = telemetryWindAtRelease.magnitude;
        float shotDistance = new Vector3(
            telemetryHolePosition.x - telemetryShotOrigin.x,
            0f,
            telemetryHolePosition.z - telemetryShotOrigin.z).magnitude;

        string windLabel = windMag < 0.5f
            ? "wind=CALM"
            : $"wind=({telemetryWindAtRelease.x:F1},{telemetryWindAtRelease.z:F1}) |{windMag:F1}|";

        string impactLabel = telemetryImpactRecorded ? "impactΔ" : (outOfBounds ? "OOB_rest" : "restΔ");
        string oobTag = outOfBounds ? " [OOB]" : "";
        string superTag = likelySuperClub ? " [SUPER]" : "";
        string summary = $"#{telemetryShotCounter} dist={shotDistance:F1}m " +
                         $"pwr={telemetryAppliedPower * 100f:F0}% " +
                         $"pitch={telemetryShotPitch:F1}° " +
                         $"{windLabel}{oobTag}{superTag} " +
                         $"{impactLabel}=({impactDelta.x:+0.00;-0.00},{impactDelta.z:+0.00;-0.00}) |{impactDelta.magnitude:F2}|m " +
                         $"vsHole=|{vsHole.magnitude:F2}|m " +
                         $"flight={elapsed:F1}s";

        MelonLogger.Msg("[SuperHackerGolf] Telemetry " + summary);

        telemetryRecentSummaries.Add(summary);
        if (telemetryRecentSummaries.Count > telemetryMaxHistory)
        {
            telemetryRecentSummaries.RemoveAt(0);
        }

        AppendTelemetryCsv(
            shotDistance: shotDistance,
            finalRest: finalRest,
            actualImpact: actualImpact,
            impactDelta: impactDelta,
            restDelta: restDelta,
            vsHole: vsHole,
            flightTime: elapsed,
            outOfBounds: outOfBounds,
            likelySuperClub: likelySuperClub);

        telemetryShotInProgress = false;
    }

    private void AppendTelemetryCsv(float shotDistance, Vector3 finalRest, Vector3 actualImpact,
                                     Vector3 impactDelta, Vector3 restDelta, Vector3 vsHole, float flightTime,
                                     bool outOfBounds, bool likelySuperClub)
    {
        try
        {
            if (string.IsNullOrEmpty(telemetryCsvPath))
            {
                telemetryCsvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "SuperHackerGolf-telemetry.csv");
            }

            CultureInfo ic = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(512);

            if (!telemetryCsvHeaderWritten && !File.Exists(telemetryCsvPath))
            {
                sb.AppendLine("shot,wind_state,had_airborne_impact,out_of_bounds,super_club,shot_distance_m,flight_s,wind_x,wind_z,wind_mag,wind_factor,cross_wind_factor,air_drag,swing_power_mul,power_pct,pitch_deg,origin_x,origin_y,origin_z,hole_x,hole_y,hole_z,predict_x,predict_y,predict_z,impact_x,impact_y,impact_z,final_x,final_y,final_z,impact_delta_x,impact_delta_y,impact_delta_z,impact_delta_mag,rest_delta_mag,vs_hole_mag");
                telemetryCsvHeaderWritten = true;
            }

            string windState = telemetryWindAtRelease.magnitude < 0.5f ? "calm" : "windy";

            sb.Append(telemetryShotCounter.ToString(ic)).Append(',')
              .Append(windState).Append(',')
              .Append(telemetryImpactRecorded ? "1" : "0").Append(',')
              .Append(outOfBounds ? "1" : "0").Append(',')
              .Append(likelySuperClub ? "1" : "0").Append(',')
              .Append(shotDistance.ToString("0.###", ic)).Append(',')
              .Append(flightTime.ToString("0.###", ic)).Append(',')
              .Append(telemetryWindAtRelease.x.ToString("0.###", ic)).Append(',')
              .Append(telemetryWindAtRelease.z.ToString("0.###", ic)).Append(',')
              .Append(telemetryWindAtRelease.magnitude.ToString("0.###", ic)).Append(',')
              .Append(telemetryBallWindFactorAtRelease.ToString("0.####", ic)).Append(',')
              .Append(telemetryBallCrossWindFactorAtRelease.ToString("0.####", ic)).Append(',')
              .Append(telemetryAirDragFactorAtRelease.ToString("0.######", ic)).Append(',')
              .Append(telemetrySwingPowerMultiplierAtRelease.ToString("0.####", ic)).Append(',')
              .Append((telemetryAppliedPower * 100f).ToString("0.##", ic)).Append(',')
              .Append(telemetryShotPitch.ToString("0.##", ic)).Append(',')
              .Append(telemetryShotOrigin.x.ToString("0.##", ic)).Append(',')
              .Append(telemetryShotOrigin.y.ToString("0.##", ic)).Append(',')
              .Append(telemetryShotOrigin.z.ToString("0.##", ic)).Append(',')
              .Append(telemetryHolePosition.x.ToString("0.##", ic)).Append(',')
              .Append(telemetryHolePosition.y.ToString("0.##", ic)).Append(',')
              .Append(telemetryHolePosition.z.ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedLanding.x.ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedLanding.y.ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedLanding.z.ToString("0.##", ic)).Append(',')
              .Append(actualImpact.x.ToString("0.##", ic)).Append(',')
              .Append(actualImpact.y.ToString("0.##", ic)).Append(',')
              .Append(actualImpact.z.ToString("0.##", ic)).Append(',')
              .Append(finalRest.x.ToString("0.##", ic)).Append(',')
              .Append(finalRest.y.ToString("0.##", ic)).Append(',')
              .Append(finalRest.z.ToString("0.##", ic)).Append(',')
              .Append(impactDelta.x.ToString("0.###", ic)).Append(',')
              .Append(impactDelta.y.ToString("0.###", ic)).Append(',')
              .Append(impactDelta.z.ToString("0.###", ic)).Append(',')
              .Append(impactDelta.magnitude.ToString("0.###", ic)).Append(',')
              .Append(restDelta.magnitude.ToString("0.###", ic)).Append(',')
              .Append(vsHole.magnitude.ToString("0.###", ic))
              .AppendLine();

            File.AppendAllText(telemetryCsvPath, sb.ToString());
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Telemetry CSV write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal IList<string> GetTelemetryRecentSummaries() => telemetryRecentSummaries;
    internal int GetTelemetryShotCount() => telemetryShotCounter;
}
