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
    private bool telemetryHoledIn;
    // E15 solver diagnostics snapshotted at shot release.
    private Vector3 telemetrySolverAimAtRelease;
    private float telemetrySolverSpeedAtRelease;
    private float telemetrySolverErrAtRelease;
    private int telemetrySolverIterAtRelease;
    private bool telemetrySolverConvergedAtRelease;
    // E21: predicted final rest captured at shot release (last point of the
    // frozen trajectory after bounce+roll). Compared against the ball's true
    // resting position to measure bounce/roll prediction accuracy as a metric
    // distinct from first-impact prediction accuracy.
    private Vector3 telemetryPredictedRestAtRelease;
    private bool telemetryPredictedRestValid;
    private int telemetryPredictedPathPointCountAtRelease;
    // E23: terrain-layer snapshot at shot release. Captures what the game
    // reports as the per-layer linear damping at the ball's xz, so we can
    // correlate roll-prediction accuracy with the damping value.
    private float telemetryLayerLinearDampingAtRelease;
    private float telemetryRollDampingMultiplierAtRelease;
    // E23: predicted OOB flag. Set when the frozen trajectory's max horizontal
    // distance from the hole exceeds a generous threshold (50m). Lets us
    // filter OOB shots from accuracy stats.
    private bool telemetryPredictedPathGoesFar;
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
        telemetryHoledIn = false;
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
        telemetrySolverAimAtRelease = lastSolverCompensatedAim;
        telemetrySolverSpeedAtRelease = lastSolverSpeedMps;
        telemetrySolverErrAtRelease = lastSolverFinalErrM;
        telemetrySolverIterAtRelease = lastSolverIterCount;
        telemetrySolverConvergedAtRelease = lastSolverConverged;

        // E21: snapshot the predicted FINAL REST point from the frozen path.
        telemetryPredictedRestValid = false;
        telemetryPredictedPathPointCountAtRelease = 0;
        telemetryPredictedPathGoesFar = false;
        if (frozenPredictedPathPoints != null && frozenPredictedPathPoints.Count > 0)
        {
            telemetryPredictedRestAtRelease = frozenPredictedPathPoints[frozenPredictedPathPoints.Count - 1];
            telemetryPredictedPathPointCountAtRelease = frozenPredictedPathPoints.Count;
            telemetryPredictedRestValid = true;

            // E23: scan the frozen path for the maximum horizontal distance
            // from the hole. If any point is >50m from the hole, the shot is
            // likely going OOB or dramatically past the green.
            float maxDistSq = 0f;
            for (int i = 0; i < frozenPredictedPathPoints.Count; i++)
            {
                Vector3 p = frozenPredictedPathPoints[i];
                float dx = p.x - telemetryHolePosition.x;
                float dz = p.z - telemetryHolePosition.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > maxDistSq) maxDistSq = d2;
            }
            telemetryPredictedPathGoesFar = maxDistSq > 2500f; // 50m
        }

        // E23: capture terrain layer damping at the ball's xz at shot release.
        // This is what the game's physics use for the first ground contact.
        telemetryLayerLinearDampingAtRelease = 0f;
        telemetryRollDampingMultiplierAtRelease = rollDampingMultiplier;
        try
        {
            Vector3 probe = telemetryShotOrigin;
            float b, df, ld, fsmp, frmp;
            AnimationCurve ac;
            if (TryGetTerrainLayerAtPoint(probe, out b, out df, out ld,
                out fsmp, out frmp, out ac))
            {
                telemetryLayerLinearDampingAtRelease = ld;
            }
        }
        catch { }
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

        float elapsed = Time.time - telemetryCaptureTime;

        // Ball destroyed mid-shot = hole-in (GolfHoleTrigger.OnTriggerEnter
        // despawns the ball). Finalize with the last known ball position
        // instead of silently bailing.
        if (golfBall == null || golfBall.gameObject == null)
        {
            if (telemetryBallHasLaunched && elapsed > telemetryMinShotTime)
            {
                telemetryHoledIn = true;
                FinalizeShotTelemetry(telemetryPrevBallPos, Vector3.zero, elapsed);
            }
            telemetryShotInProgress = false;
            return;
        }

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

        // Mid-flight hole entry — the ball is still alive this frame but
        // inside the GolfHoleTrigger volume. Finalize immediately rather than
        // waiting for the game to despawn it a frame later.
        if (telemetryBallHasLaunched && elapsed > telemetryMinShotTime && IsPositionInHole(ballPos))
        {
            telemetryHoledIn = true;
            if (!telemetryImpactRecorded)
            {
                telemetryActualImpact = ballPos;
                telemetryImpactRecorded = true;
            }
            FinalizeShotTelemetry(ballPos, ballVel, elapsed);
            telemetryShotInProgress = false;
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

        FinalizeShotTelemetry(ballPos, ballVel, elapsed);
        telemetryShotInProgress = false;
    }

    private void FinalizeShotTelemetry(Vector3 finalBallPos, Vector3 finalBallVel, float elapsed)
    {
        telemetryShotCounter++;
        Vector3 finalRest = finalBallPos;

        // OOB detection: if the ball's final rest is very close to the shot
        // origin (< 1.5m) AND elapsed flight time is long enough that it
        // couldn't possibly be a dribble, the game almost certainly respawned
        // the ball at the tee due to out-of-bounds. Flag it so the CSV row
        // doesn't pollute the delta regression.
        bool outOfBounds = false;
        if (!telemetryImpactRecorded &&
            !telemetryHoledIn &&
            elapsed > 1.5f &&
            (finalRest - telemetryShotOrigin).sqrMagnitude < 2.25f) // < 1.5m
        {
            outOfBounds = true;
        }

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

        string impactLabel = telemetryHoledIn ? "HOLED" : (telemetryImpactRecorded ? "impactΔ" : (outOfBounds ? "OOB_rest" : "restΔ"));
        string oobTag = outOfBounds ? " [OOB]" : "";
        string holedTag = telemetryHoledIn ? " [HOLE-IN]" : "";
        string superTag = likelySuperClub ? " [SUPER]" : "";
        float summaryPredRestDelta = 0f;
        if (telemetryPredictedRestValid)
        {
            Vector3 d = new Vector3(
                telemetryPredictedRestAtRelease.x - finalRest.x,
                0f,
                telemetryPredictedRestAtRelease.z - finalRest.z);
            summaryPredRestDelta = d.magnitude;
        }
        string summary = $"#{telemetryShotCounter} dist={shotDistance:F1}m " +
                         $"pwr={telemetryAppliedPower * 100f:F0}% " +
                         $"pitch={telemetryShotPitch:F1}° " +
                         $"{windLabel}{oobTag}{holedTag}{superTag} " +
                         $"{impactLabel}=({impactDelta.x:+0.00;-0.00},{impactDelta.z:+0.00;-0.00}) |{impactDelta.magnitude:F2}|m " +
                         $"bounceΔ=|{summaryPredRestDelta:F2}|m " +
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

            const string expectedHeader = "shot,wind_state,had_airborne_impact,out_of_bounds,holed_in,super_club,solver_converged,pred_goes_far,shot_distance_m,flight_s,wind_x,wind_z,wind_mag,wind_factor,cross_wind_factor,air_drag,swing_power_mul,power_pct,pitch_deg,layer_linear_damping,roll_damping_mul,origin_x,origin_y,origin_z,hole_x,hole_y,hole_z,solver_aim_x,solver_aim_z,solver_aim_offset_m,solver_iter_count,solver_err_m,solver_speed_mps,predict_x,predict_y,predict_z,pred_rest_x,pred_rest_y,pred_rest_z,pred_path_pts,impact_x,impact_y,impact_z,final_x,final_y,final_z,impact_delta_x,impact_delta_y,impact_delta_z,impact_delta_mag,rest_delta_mag,pred_rest_delta_mag,vs_hole_mag";

            if (!telemetryCsvHeaderWritten)
            {
                bool needsRewrite = false;
                if (File.Exists(telemetryCsvPath))
                {
                    try
                    {
                        using (StreamReader sr = new StreamReader(telemetryCsvPath))
                        {
                            string firstLine = sr.ReadLine();
                            if (firstLine == null || firstLine != expectedHeader)
                            {
                                needsRewrite = true;
                            }
                        }
                    }
                    catch { needsRewrite = true; }

                    if (needsRewrite)
                    {
                        // Header changed — rename the existing CSV so we don't
                        // corrupt it with mis-aligned rows, then start fresh.
                        string backup = telemetryCsvPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                        try { File.Move(telemetryCsvPath, backup); } catch { }
                    }
                }

                if (!File.Exists(telemetryCsvPath))
                {
                    sb.AppendLine(expectedHeader);
                }
                telemetryCsvHeaderWritten = true;
            }

            string windState = telemetryWindAtRelease.magnitude < 0.5f ? "calm" : "windy";

            float solverAimOffset = new Vector3(
                telemetrySolverAimAtRelease.x - telemetryHolePosition.x,
                0f,
                telemetrySolverAimAtRelease.z - telemetryHolePosition.z).magnitude;

            // E21: distance from our predicted final rest to the ball's
            // observed final rest — the bounce+roll prediction accuracy metric.
            // Zero if the predicted rest wasn't captured (shouldn't happen
            // once we get frozen path points at shot release).
            float predRestDeltaMag = 0f;
            if (telemetryPredictedRestValid)
            {
                Vector3 prd = new Vector3(
                    telemetryPredictedRestAtRelease.x - finalRest.x,
                    telemetryPredictedRestAtRelease.y - finalRest.y,
                    telemetryPredictedRestAtRelease.z - finalRest.z);
                predRestDeltaMag = prd.magnitude;
            }

            sb.Append(telemetryShotCounter.ToString(ic)).Append(',')
              .Append(windState).Append(',')
              .Append(telemetryImpactRecorded ? "1" : "0").Append(',')
              .Append(outOfBounds ? "1" : "0").Append(',')
              .Append(telemetryHoledIn ? "1" : "0").Append(',')
              .Append(likelySuperClub ? "1" : "0").Append(',')
              .Append(telemetrySolverConvergedAtRelease ? "1" : "0").Append(',')
              .Append(telemetryPredictedPathGoesFar ? "1" : "0").Append(',')
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
              .Append(telemetryLayerLinearDampingAtRelease.ToString("0.####", ic)).Append(',')
              .Append(telemetryRollDampingMultiplierAtRelease.ToString("0.##", ic)).Append(',')
              .Append(telemetryShotOrigin.x.ToString("0.##", ic)).Append(',')
              .Append(telemetryShotOrigin.y.ToString("0.##", ic)).Append(',')
              .Append(telemetryShotOrigin.z.ToString("0.##", ic)).Append(',')
              .Append(telemetryHolePosition.x.ToString("0.##", ic)).Append(',')
              .Append(telemetryHolePosition.y.ToString("0.##", ic)).Append(',')
              .Append(telemetryHolePosition.z.ToString("0.##", ic)).Append(',')
              .Append(telemetrySolverAimAtRelease.x.ToString("0.##", ic)).Append(',')
              .Append(telemetrySolverAimAtRelease.z.ToString("0.##", ic)).Append(',')
              .Append(solverAimOffset.ToString("0.###", ic)).Append(',')
              .Append(telemetrySolverIterAtRelease.ToString(ic)).Append(',')
              .Append(telemetrySolverErrAtRelease.ToString("0.###", ic)).Append(',')
              .Append(telemetrySolverSpeedAtRelease.ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedLanding.x.ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedLanding.y.ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedLanding.z.ToString("0.##", ic)).Append(',')
              .Append((telemetryPredictedRestValid ? telemetryPredictedRestAtRelease.x : 0f).ToString("0.##", ic)).Append(',')
              .Append((telemetryPredictedRestValid ? telemetryPredictedRestAtRelease.y : 0f).ToString("0.##", ic)).Append(',')
              .Append((telemetryPredictedRestValid ? telemetryPredictedRestAtRelease.z : 0f).ToString("0.##", ic)).Append(',')
              .Append(telemetryPredictedPathPointCountAtRelease.ToString(ic)).Append(',')
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
              .Append(predRestDeltaMag.ToString("0.###", ic)).Append(',')
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
