using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public partial class SuperHackerGolf
{
    private void UpdateTrails()
    {
        UpdateActualTrail();
        bool ballMoving = IsBallMovingForPrediction();
        UpdatePredictedTrail(ballMoving);
    }

    private void UpdateActualTrail()
    {
        EnsureTrailRenderers();

        if (!actualTrailEnabled)
        {
            if (shotPathLine != null)
            {
                shotPathLine.positionCount = 0;
            }
            return;
        }

        if (shotPathLine == null || golfBall == null)
        {
            return;
        }

        Vector3 currentBallPosition = golfBall.transform.position + Vector3.up * shotPathHeightOffset;
        if (lastShotPathBallPosition == Vector3.zero)
        {
            lastShotPathBallPosition = currentBallPosition;
        }

        float moveThresholdSq = shotPathMoveThreshold * shotPathMoveThreshold;
        float pointSpacingSq = shotPathPointSpacing * shotPathPointSpacing;
        float moveDistanceSq = (currentBallPosition - lastShotPathBallPosition).sqrMagnitude;

        if (moveDistanceSq >= moveThresholdSq)
        {
            if (!isRecordingShotPath)
            {
                isRecordingShotPath = true;
                if (shotPathPoints.Count == 0)
                {
                    shotPathPoints.Add(lastShotPathBallPosition);
                }
                else if ((shotPathPoints[shotPathPoints.Count - 1] - lastShotPathBallPosition).sqrMagnitude >= pointSpacingSq)
                {
                    shotPathPoints.Add(lastShotPathBallPosition);
                }
            }

            if (shotPathPoints.Count == 0 || (shotPathPoints[shotPathPoints.Count - 1] - currentBallPosition).sqrMagnitude >= pointSpacingSq)
            {
                shotPathPoints.Add(currentBallPosition);
                if (shotPathPoints.Count > shotPathMaxPoints)
                {
                    int trimCount = shotPathPoints.Count - shotPathMaxPoints;
                    shotPathPoints.RemoveRange(0, trimCount);
                    actualTrailLineDirty = true;
                }
                ApplyActualTrailToLine();
            }

            lastShotPathMoveTime = Time.time;
        }
        else if (isRecordingShotPath && Time.time - lastShotPathMoveTime > shotPathStationaryDelay)
        {
            isRecordingShotPath = false;
        }

        lastShotPathBallPosition = currentBallPosition;
    }

    private void UpdatePredictedTrail(bool ballMoving)
    {
        EnsureTrailRenderers();
        if (predictedPathLine == null || frozenPredictedPathLine == null)
        {
            return;
        }


        if (ballMoving)
        {
            observedBallMotionSinceLastShot = true;
            ClearPredictedTrails(false);
            return;
        }

        if (lockLivePredictedPath)
        {
            bool fallbackExpired = predictedTrajectoryHideStartTime > 0f &&
                                   Time.time - predictedTrajectoryHideStartTime >= predictedTrajectoryUnlockFallbackDelay;

            if (observedBallMotionSinceLastShot || fallbackExpired)
            {
                lockLivePredictedPath = false;
                observedBallMotionSinceLastShot = false;
                predictedTrajectoryHideStartTime = 0f;
            }
            else
            {
                ClearPredictedTrails(false);
                return;
            }
        }

        if (!assistEnabled || playerGolfer == null || golfBall == null || currentAimTargetPosition == Vector3.zero)
        {
            ClearPredictedTrails(true);
            return;
        }

        float currentTime = Time.time;
        if (predictedPathPoints.Count > 0 && currentTime < nextPredictedPathRefreshTime)
        {
            return;
        }

        nextPredictedPathRefreshTime = currentTime + predictedPathRefreshInterval;

        float predictedPower;
        float predictedPitch;
        if (!TryResolvePredictedSwingParameters(out predictedPower, out predictedPitch))
        {
            ClearPredictedTrails(true);
            return;
        }

        Vector3 shotOrigin = golfBall.transform.position + Vector3.up * shotPathHeightOffset;
        if (!ShouldRebuildPredictedTrajectory(shotOrigin, predictedPower, predictedPitch))
        {
            return;
        }

        BuildPredictedTrajectoryPoints(predictedPower, predictedPitch, predictedPathPoints);
        CachePredictedTrajectoryInputs(shotOrigin, predictedPower, predictedPitch);
        ApplyPredictedTrailToLine();
    }

    private bool ShouldRebuildPredictedTrajectory(Vector3 shotOrigin, float shotPower, float swingPitch)
    {
        if (!predictedPathCacheValid || predictedPathPoints.Count == 0 || !ReferenceEquals(cachedPredictedPathBall, golfBall))
        {
            return true;
        }

        float distanceEpsilonSq = predictedPathRebuildDistanceEpsilon * predictedPathRebuildDistanceEpsilon;
        if ((cachedPredictedShotOrigin - shotOrigin).sqrMagnitude > distanceEpsilonSq)
        {
            return true;
        }

        if ((cachedPredictedAimTargetPosition - currentAimTargetPosition).sqrMagnitude > distanceEpsilonSq)
        {
            return true;
        }

        if (Mathf.Abs(cachedPredictedSwingPower - shotPower) > predictedPathRebuildPowerEpsilon)
        {
            return true;
        }

        return Mathf.Abs(cachedPredictedSwingPitch - swingPitch) > predictedPathRebuildPitchEpsilon;
    }

    private void CachePredictedTrajectoryInputs(Vector3 shotOrigin, float shotPower, float swingPitch)
    {
        predictedPathCacheValid = true;
        cachedPredictedPathBall = golfBall;
        cachedPredictedShotOrigin = shotOrigin;
        cachedPredictedAimTargetPosition = currentAimTargetPosition;
        cachedPredictedSwingPower = shotPower;
        cachedPredictedSwingPitch = swingPitch;
    }

    private bool TryResolvePredictedSwingParameters(out float shotPower, out float swingPitch)
    {
        shotPower = Mathf.Clamp(idealSwingPower > 0.0001f ? idealSwingPower : 0.05f, 0.05f, 2f);
        swingPitch = idealSwingPitch;

        if (playerGolfer == null)
        {
            return false;
        }

        float currentPower;
        bool isChargingSwing;
        bool isSwinging;
        if (TryGetCurrentSwingValues(out currentPower, out swingPitch, out isChargingSwing, out isSwinging))
        {
            if (float.IsNaN(swingPitch) || float.IsInfinity(swingPitch))
            {
                swingPitch = idealSwingPitch;
            }
        }

        double targetTimestamp;
        float resolvedPower;
        if (TryCalculateChargeTimestampForPower(shotPower, out targetTimestamp, out resolvedPower))
        {
            shotPower = Mathf.Clamp(resolvedPower, 0.05f, 2f);
        }

        if (float.IsNaN(swingPitch) || float.IsInfinity(swingPitch))
        {
            swingPitch = idealSwingPitch;
        }

        return !float.IsNaN(shotPower) && !float.IsInfinity(shotPower);
    }

    private bool IsBallMovingForPrediction()
    {
        Vector3 velocity;
        if (TryGetGolfBallVelocity(out velocity) && velocity.magnitude > predictedUnlockSpeedThreshold)
        {
            return true;
        }

        if (isRecordingShotPath)
        {
            return true;
        }

        return lastShotPathMoveTime > 0f && Time.time - lastShotPathMoveTime <= shotPathStationaryDelay;
    }

    private Vector3 BuildPredictedShotDirection(Vector3 shotOrigin, float swingPitch)
    {
        Vector3 toTarget = currentAimTargetPosition - shotOrigin;
        Vector3 horizontal = new Vector3(toTarget.x, 0f, toTarget.z);
        Vector3 horizontalDirection = horizontal.sqrMagnitude > 0.0001f
            ? horizontal.normalized
            : new Vector3(playerGolfer.transform.forward.x, 0f, playerGolfer.transform.forward.z).normalized;

        if (horizontalDirection.sqrMagnitude < 0.0001f)
        {
            horizontalDirection = Vector3.forward;
        }

        float pitchRad = swingPitch * Mathf.Deg2Rad;
        Vector3 direction = horizontalDirection * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad);
        return direction.sqrMagnitude < 0.0001f ? GetSwingDirection(swingPitch) : direction.normalized;
    }

    private void BuildPredictedTrajectoryPoints(float shotPower, float swingPitch, List<Vector3> outputPoints)
    {
        try
        {
            BuildPredictedTrajectoryPointsCore(shotPower, swingPitch, outputPoints);
        }
        catch (Exception ex)
        {
            // E20: scene-teardown hardening. See FreezePredictedTrajectorySnapshot.
            MelonLoader.MelonLogger.Warning($"[SuperHackerGolf] BuildPredictedTrajectoryPoints swallowed {ex.GetType().Name}: {ex.Message}");
            try { outputPoints?.Clear(); } catch { }
            predictedPathCacheValid = false;
        }
    }

    private void BuildPredictedTrajectoryPointsCore(float shotPower, float swingPitch, List<Vector3> outputPoints)
    {
        outputPoints.Clear();
        ResetImpactPreviewCache(ReferenceEquals(outputPoints, predictedPathPoints), ReferenceEquals(outputPoints, frozenPredictedPathPoints));
        if (playerGolfer == null || golfBall == null || currentAimTargetPosition == Vector3.zero)
        {
            return;
        }

        Vector3 shotOrigin = golfBall.transform.position + Vector3.up * shotPathHeightOffset;
        Vector3 shotDirection = BuildPredictedShotDirection(shotOrigin, swingPitch);

        // E11: EXACT game launch-speed formula from decompiled GetSwingHitSpeed.
        // Rocket driver (super club) uses a separate BMath.Remap branch with
        // per-ball min/max rocket hit speeds. Putt vs full swing also has its
        // own max speed constant. MatchSetupRules.GetValue(Rule.SwingPower) is
        // applied inside GetGameExactLaunchSpeed.
        bool isRocketDriver = IsLocalPlayerUsingRocketDriver();
        bool isPutt = swingPitch <= 0f;
        float launchSpeed = Mathf.Max(0.1f, GetGameExactLaunchSpeed(shotPower, isPutt, isRocketDriver));

        float dt = Time.fixedDeltaTime;  // match game fixed step exactly (no clamping)
        Vector3 gravity = Physics.gravity;
        // E11: rocket driver swings use a different air drag coefficient.
        float airDragFactor = GetGameExactAirDragFactor(isRocketDriver);
        float pointSpacingSq = predictedPathPointSpacing * predictedPathPointSpacing;

        // E8 graft: EXACT reimplementation of Hittable.ApplyAirDamping.
        // E11: per-contact terrain layer settings replace the ball-material
        // bounciness/friction read (game bypasses Unity's PhysicsMaterial
        // pipeline entirely via ContactModifyEvent + TerrainLayerSettings).
        RefreshBallWindFactors();
        RefreshHoleBounds();
        Vector3 windVector = GetCachedWindVector();
        float ballWindFactor = GetBallWindFactor();
        float ballCrossWindFactor = GetBallCrossWindFactor();

        outputPoints.Add(shotOrigin);

        Vector3 position = shotOrigin;
        Vector3 velocity = shotDirection * launchSpeed;
        float elapsed = 0f;
        bool trackImpactPreview = ReferenceEquals(outputPoints, predictedPathPoints) || ReferenceEquals(outputPoints, frozenPredictedPathPoints);
        bool impactResolved = false;
        Vector3 impactPoint = Vector3.zero;
        Vector3 impactNormal = Vector3.up;
        Vector3 impactApproachDirection = GetFallbackPreviewDirection();

        bool holedOut = false;

        // ── Phase 1: airborne flight ──────────────────────────────────────────
        for (int i = 0; i < predictedPathMaxSteps && elapsed <= predictedPathMaxTime; i++)
        {
            Vector3 previousPosition = position;
            velocity += gravity * dt;

            // E8: exact Hittable.ApplyAirDamping reimplementation.
            Vector3 effectiveWind = Vector3.zero;
            if (windVector.sqrMagnitude > 0.0001f && velocity.sqrMagnitude > 0.0001f)
            {
                Vector3 windAlong = Vector3.Project(windVector, velocity);
                Vector3 windCross = windVector - windAlong;
                effectiveWind = windAlong * ballWindFactor + windCross * ballCrossWindFactor;
            }
            Vector3 relVel = velocity - effectiveWind;
            float relSqrMag = relVel.sqrMagnitude;
            float dragDelta = Mathf.Max(0f, airDragFactor * relSqrMag * dt);
            velocity -= relVel * dragDelta;

            position += velocity * dt;

            // E11: hole-in detection — GolfHoleTrigger.ballTrigger overlap with
            // the ball's current position. No velocity/angle gates (game's real
            // OnTriggerEnter check doesn't have any either).
            if (IsPositionInHole(position))
            {
                holedOut = true;
                impactResolved = true;
                impactPoint = position;
                impactNormal = Vector3.up;
                break;
            }

            if (!impactResolved)
            {
                RaycastHit impactHit;
                if (TryFindWorldImpactAlongSegment(previousPosition, position, out impactHit))
                {
                    impactResolved = true;
                    impactPoint = impactHit.point;
                    impactNormal = impactHit.normal.sqrMagnitude > 0.0001f ? impactHit.normal : Vector3.up;
                    Vector3 segmentDirection = position - previousPosition;
                    if (segmentDirection.sqrMagnitude >= 0.0001f)
                    {
                        impactApproachDirection = segmentDirection.normalized;
                    }
                    position = impactPoint;
                    break;
                }
            }

            if ((outputPoints[outputPoints.Count - 1] - position).sqrMagnitude >= pointSpacingSq)
            {
                outputPoints.Add(position);
            }

            if (position.y < -200f)
            {
                break;
            }

            if (elapsed > 1f && velocity.sqrMagnitude < 0.001f)
            {
                break;
            }

            elapsed += dt;
        }

        // ── Phase 2: bounces + roll ───────────────────────────────────────────
        //
        // Reconstructed from GolfBall.ApplyGroundDamping + g__GetDamping|114_1
        // and augmented with a Unity-style bounce reflection based on the ball's
        // real PhysicsMaterial bounciness + dynamic friction.
        //
        // On each ground contact:
        //   v_normal  = dot(v, n) * n          // component pushing into ground
        //   v_tangent = v - v_normal            // sliding component
        //   v_out     = v_tangent * (1 - df) - v_normal * bounciness
        //
        // If |v_out.y| is still large, the ball bounces back into the air and we
        // continue simulating with air drag + wind until the next ground contact.
        // Once the incoming normal velocity drops below a threshold the ball is
        // "grounded" and we switch to the ground damping formula with per-frame
        // raycasts to follow the terrain contour.

        if (impactResolved && !holedOut)
        {
            outputPoints.Add(position);

            // ── E11b: bounce chain using REAL terrain layer settings ──────────
            //
            // The game bypasses Unity's PhysicsMaterial pipeline entirely. Every
            // ball-ground contact, PhysicsManager.ModifyContactsInternal
            // overwrites bounciness/friction from TerrainLayerSettings of whatever
            // terrain layer the ball is touching. So we query TerrainManager's
            // GetDominantLayerSettingsAtPoint at each bounce point.
            const float BOUNCE_LIFTOFF_SPEED = 0.5f;
            const int MAX_BOUNCES = 12;

            Vector3 currentGroundNormal = impactNormal;
            if (currentGroundNormal.y < 0.0001f) currentGroundNormal = Vector3.up;

            int bounces = 0;
            bool grounded = false;

            while (bounces < MAX_BOUNCES)
            {
                bounces++;

                // Query the real terrain layer at this contact point — this is
                // what the game uses for bounciness/friction, NOT the ball's
                // Unity PhysicsMaterial.
                float layerBounciness, layerDynFriction, layerLinearDamping,
                      layerStopMaxPitch, layerRollMinPitch;
                AnimationCurve layerCurve;
                TryGetTerrainLayerAtPoint(position,
                    out layerBounciness, out layerDynFriction, out layerLinearDamping,
                    out layerStopMaxPitch, out layerRollMinPitch, out layerCurve);

                float normalDot = Vector3.Dot(velocity, currentGroundNormal);
                Vector3 vNormal = currentGroundNormal * normalDot;
                Vector3 vTangent = velocity - vNormal;

                // Coefficient-of-restitution bounce using the terrain's bounciness.
                Vector3 vOut = vTangent - vNormal * Mathf.Clamp01(layerBounciness);
                float outNormalSpeed = Vector3.Dot(vOut, currentGroundNormal);

                if (outNormalSpeed < BOUNCE_LIFTOFF_SPEED)
                {
                    velocity = vTangent;
                    grounded = true;
                    break;
                }
                velocity = vOut;

                // Fly until the next impact (inline air-phase step loop).
                bool nextImpact = false;
                for (int j = 0; j < predictedPathMaxSteps && elapsed < predictedPathMaxTime; j++)
                {
                    Vector3 prev = position;
                    velocity += gravity * dt;

                    Vector3 eWind = Vector3.zero;
                    if (windVector.sqrMagnitude > 0.0001f && velocity.sqrMagnitude > 0.0001f)
                    {
                        Vector3 wa = Vector3.Project(windVector, velocity);
                        Vector3 wc = windVector - wa;
                        eWind = wa * ballWindFactor + wc * ballCrossWindFactor;
                    }
                    Vector3 rv = velocity - eWind;
                    float rvSq = rv.sqrMagnitude;
                    float dd = Mathf.Max(0f, airDragFactor * rvSq * dt);
                    velocity -= rv * dd;
                    position += velocity * dt;

                    // Hole check during bounce arcs too.
                    if (IsPositionInHole(position))
                    {
                        holedOut = true;
                        outputPoints.Add(position);
                        nextImpact = false; // exit bounce loop
                        break;
                    }

                    RaycastHit hit;
                    if (TryFindWorldImpactAlongSegment(prev, position, out hit))
                    {
                        position = hit.point;
                        currentGroundNormal = hit.normal.sqrMagnitude > 0.0001f ? hit.normal : Vector3.up;
                        nextImpact = true;

                        if ((outputPoints[outputPoints.Count - 1] - position).sqrMagnitude >= pointSpacingSq * 0.25f)
                        {
                            outputPoints.Add(position);
                        }
                        break;
                    }

                    if ((outputPoints[outputPoints.Count - 1] - position).sqrMagnitude >= pointSpacingSq)
                    {
                        outputPoints.Add(position);
                    }

                    if (position.y < -200f) break;
                    elapsed += dt;
                }

                if (holedOut || !nextImpact)
                {
                    grounded = false;
                    break;
                }
            }

            // ── E11b: roll phase using REAL per-contact terrain layer settings ─
            if (grounded && !holedOut)
            {
                float rollingDownhillTime = 0f;
                float rollElapsed = 0f;
                float maxRollTime = predictedPathMaxTime;
                Vector3 rollNormal = currentGroundNormal;

                for (int i = 0; i < predictedPathMaxSteps && rollElapsed < maxRollTime; i++)
                {
                    // Query terrain layer at the current roll position (updates
                    // as the ball crosses fairway → green → rough, etc.).
                    float layerBounciness, layerDynFriction, layerLinearDamping,
                          layerStopMaxPitch, layerRollMinPitch;
                    AnimationCurve layerCurve;
                    TryGetTerrainLayerAtPoint(position,
                        out layerBounciness, out layerDynFriction, out layerLinearDamping,
                        out layerStopMaxPitch, out layerRollMinPitch, out layerCurve);

                    float groundPitchDeg = Vector3.Angle(Vector3.up, rollNormal);

                    Vector3 velAlongGround = Vector3.ProjectOnPlane(velocity, rollNormal);
                    float speedAlong = velAlongGround.magnitude;

                    if (speedAlong < 0.05f)
                    {
                        break;
                    }

                    // Use the terrain-layer LinearDamping directly (the game's
                    // ApplyCollisionSettings overwrites contact friction with this).
                    // The game's full stop / roll blend still applies — mirror the
                    // reconstructed g__GetDamping formula but with the terrain values.
                    float damping = ComputeTerrainDamping(
                        groundPitchDeg, speedAlong, rollingDownhillTime,
                        layerLinearDamping, layerStopMaxPitch, layerRollMinPitch, layerCurve);

                    float fac = Mathf.Max(0f, 1f - damping * dt);
                    velocity = velocity - velAlongGround + velAlongGround * fac;

                    position += velocity * dt;

                    // Hole-in check: the ball may roll into the cup from across the green.
                    if (IsPositionInHole(position))
                    {
                        holedOut = true;
                        outputPoints.Add(position);
                        break;
                    }

                    // Per-frame ground raycast to follow terrain curvature.
                    Vector3 probeOrigin = position + Vector3.up * 0.5f;
                    RaycastHit groundHit;
                    if (Physics.Raycast(probeOrigin, Vector3.down, out groundHit, 2f, GetBallGroundableMask(), QueryTriggerInteraction.Ignore))
                    {
                        position = groundHit.point;
                        rollNormal = groundHit.normal.sqrMagnitude > 0.0001f ? groundHit.normal : Vector3.up;
                    }

                    if ((outputPoints[outputPoints.Count - 1] - position).sqrMagnitude >= pointSpacingSq * 0.25f)
                    {
                        outputPoints.Add(position);
                    }

                    if (velocity.y < -0.005f)
                    {
                        rollingDownhillTime += dt;
                    }
                    else
                    {
                        rollingDownhillTime = 0f;
                    }

                    rollElapsed += dt;
                    elapsed += dt;
                }
            }

            outputPoints.Add(position);
        }
        else if (holedOut)
        {
            outputPoints.Add(position);
        }

        if (trackImpactPreview)
        {
            StoreImpactPreviewData(outputPoints, impactResolved, impactPoint, impactApproachDirection);
        }
    }

    private void FreezePredictedTrajectorySnapshot(float shotPower, float swingPitch)
    {
        // E20: scene-teardown hardening. When the player quits back to the
        // main menu while a swing is mid-release, the Unity scene tears down
        // faster than our cached component references notice. Touching
        // those references (especially the Unity internal-call bridge for
        // `Time.get_time`, LineRenderer setters, etc.) throws a
        // BadImageFormatException "Method has zero rva" which spams the log
        // and causes the mod to stop ticking. Wrap the whole method in a
        // try/catch that swallows and logs. Real shots running in a normal
        // scene don't hit this path.
        try
        {
            if (!frozenTrailEnabled)
            {
                frozenPredictedPathPoints.Clear();
                predictedPathCacheValid = false;
                frozenTrailLineDirty = false;
                if (frozenPredictedPathLine != null)
                {
                    frozenPredictedPathLine.positionCount = 0;
                }
                return;
            }

            EnsureTrailRenderers();
            if (frozenPredictedPathLine == null)
            {
                return;
            }

            BuildPredictedTrajectoryPoints(Mathf.Clamp(shotPower, 0.05f, 2f), swingPitch, frozenPredictedPathPoints);
            ApplyFrozenTrailToLine();

            lockLivePredictedPath = true;
            observedBallMotionSinceLastShot = false;
            predictedTrajectoryHideStartTime = Time.time;
            predictedPathPoints.Clear();
            predictedPathCacheValid = false;
            predictedTrailLineDirty = false;

            if (predictedPathLine != null)
            {
                predictedPathLine.positionCount = 0;
            }
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[SuperHackerGolf] FreezePredictedTrajectorySnapshot swallowed {ex.GetType().Name}: {ex.Message}");
            // Reset the state caches so we don't re-enter a bad code path.
            try { frozenPredictedPathPoints?.Clear(); } catch { }
            predictedPathCacheValid = false;
            frozenTrailLineDirty = false;
            lockLivePredictedPath = false;
        }
    }

    private Vector3 GetAimTargetPosition(Vector3 playerPosition)
    {
        Vector3 baseTarget = holePosition != Vector3.zero ? holePosition : flagPosition;
        if (baseTarget == Vector3.zero)
        {
            return Vector3.zero;
        }

        float puttDistanceThresholdSq = puttDistanceThreshold * puttDistanceThreshold;
        if ((playerPosition - baseTarget).sqrMagnitude <= puttDistanceThresholdSq)
        {
            baseTarget.y = playerPosition.y;
        }

        Vector3 shotForward = baseTarget - playerPosition;
        shotForward.y = 0f;
        if (shotForward.sqrMagnitude < 0.0001f && playerGolfer != null)
        {
            shotForward = playerGolfer.transform.forward;
            shotForward.y = 0f;
        }

        if (shotForward.sqrMagnitude >= 0.0001f)
        {
            shotForward.Normalize();
            Vector3 shotRight = Vector3.Cross(Vector3.up, shotForward).normalized;
            baseTarget += shotRight * aimTargetOffsetLocal.x;
            baseTarget += Vector3.up * aimTargetOffsetLocal.y;
            baseTarget += shotForward * aimTargetOffsetLocal.z;
        }
        else
        {
            baseTarget += aimTargetOffsetLocal;
        }

        return baseTarget;
    }

    private Vector3 GetSwingOriginPosition()
    {
        Transform referenceTransform = playerGolfer != null ? playerGolfer.transform : (playerMovement != null ? playerMovement.transform : null);
        return referenceTransform == null ? Vector3.zero : referenceTransform.TransformPoint(swingOriginLocalOffset);
    }

    private Vector3 GetSwingDirection(float pitch)
    {
        if (playerGolfer == null)
        {
            return Vector3.forward;
        }

        Vector3 forward = playerGolfer.transform.forward;
        float pitchRad = pitch * Mathf.Deg2Rad;
        Vector3 horizontal = new Vector3(forward.x, 0f, forward.z).normalized;
        if (horizontal.sqrMagnitude < 0.0001f)
        {
            horizontal = Vector3.forward;
        }

        Vector3 direction = horizontal * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad);
        return direction.normalized;
    }

    private float EstimateLaunchSpeedFromPower(float power)
    {
        float modelSpeedAtReference = EvaluatePiecewiseLinear(Mathf.Clamp(power, 0.05f, 2f), launchModelPowers, launchModelSpeeds);
        float currentSrvMul;
        if (!TryGetServerSwingPowerMultiplier(out currentSrvMul))
        {
            currentSrvMul = 1f;
        }

        return modelSpeedAtReference * Mathf.Max(0.01f, currentSrvMul / Mathf.Max(0.01f, launchModelReferenceSrvMul));
    }

    private float EstimatePowerFromLaunchSpeed(float speed)
    {
        float currentSrvMul;
        if (!TryGetServerSwingPowerMultiplier(out currentSrvMul))
        {
            currentSrvMul = 1f;
        }

        float normalizedSpeed = Mathf.Max(0.1f, speed) / Mathf.Max(0.01f, currentSrvMul / Mathf.Max(0.01f, launchModelReferenceSrvMul));
        return Mathf.Clamp(EvaluatePiecewiseLinear(normalizedSpeed, launchModelSpeeds, launchModelPowers), 0.05f, 2f);
    }

    private float EvaluatePiecewiseLinear(float x, float[] xs, float[] ys)
    {
        if (xs == null || ys == null || xs.Length < 2 || ys.Length < 2 || xs.Length != ys.Length)
        {
            return x;
        }

        int last = xs.Length - 1;
        if (x <= xs[0])
        {
            float t = (x - xs[0]) / Mathf.Max(0.0001f, xs[1] - xs[0]);
            return ys[0] + t * (ys[1] - ys[0]);
        }

        for (int i = 0; i < last; i++)
        {
            if (x <= xs[i + 1])
            {
                float t = (x - xs[i]) / Mathf.Max(0.0001f, xs[i + 1] - xs[i]);
                return ys[i] + t * (ys[i + 1] - ys[i]);
            }
        }

        float tailT = (x - xs[last - 1]) / Mathf.Max(0.0001f, xs[last] - xs[last - 1]);
        return ys[last - 1] + tailT * (ys[last] - ys[last - 1]);
    }

    private bool TryGetGolfBallVelocity(out Vector3 velocity)
    {
        velocity = Vector3.zero;
        Rigidbody ballRigidbody;
        if (!TryGetGolfBallRigidbody(out ballRigidbody) || ballRigidbody == null)
        {
            return false;
        }

        if (!rigidbodyVelocityReflectionInitialized)
        {
            rigidbodyVelocityReflectionInitialized = true;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type rigidbodyType = ballRigidbody.GetType();
            cachedRigidbodyLinearVelocityProperty = rigidbodyType.GetProperty("linearVelocity", flags);
        }

        try
        {
            if (cachedRigidbodyLinearVelocityProperty != null && cachedRigidbodyLinearVelocityProperty.PropertyType == typeof(Vector3))
            {
                velocity = (Vector3)cachedRigidbodyLinearVelocityProperty.GetValue(ballRigidbody, null);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private bool TryGetGolfBallRigidbody(out Rigidbody ballRigidbody)
    {
        ballRigidbody = null;
        if (golfBall == null && (!EnsureLocalGolfBallReference(false) || golfBall == null))
        {
            return false;
        }

        Type golfBallType = golfBall.GetType();
        if (!golfBallVelocityReflectionInitialized || cachedGolfBallTypeForVelocity != golfBallType)
        {
            golfBallVelocityReflectionInitialized = true;
            cachedGolfBallTypeForVelocity = golfBallType;
            rigidbodyVelocityReflectionInitialized = false;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            cachedGolfBallRigidbodyProperty = golfBallType.GetProperty("Rigidbody", flags);
        }

        try
        {
            if (cachedGolfBallRigidbodyProperty != null)
            {
                ballRigidbody = cachedGolfBallRigidbodyProperty.GetValue(golfBall, null) as Rigidbody;
                if (ballRigidbody != null)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void InitializeSwingMathReflection()
    {
        if (swingMathReflectionInitialized)
        {
            return;
        }

        swingMathReflectionInitialized = true;
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];

                if (cachedGolfSettingsProperty == null)
                {
                    Type gameManagerType = assembly.GetType("GameManager");
                    if (gameManagerType != null)
                    {
                        cachedGolfSettingsProperty = gameManagerType.GetProperty("GolfSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        cachedGolfBallSettingsProperty = gameManagerType.GetProperty("GolfBallSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                }

                if (cachedBMathEaseInMethod == null)
                {
                    Type bMathType = assembly.GetType("BMath");
                    if (bMathType != null)
                    {
                        cachedBMathEaseInMethod = bMathType.GetMethod("EaseIn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(float) }, null);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private object GetGolfSettingsObject()
    {
        if (cachedGolfSettingsObject != null)
        {
            return cachedGolfSettingsObject;
        }

        InitializeSwingMathReflection();

        try
        {
            if (cachedGolfSettingsProperty != null)
            {
                cachedGolfSettingsObject = cachedGolfSettingsProperty.GetValue(null, null);
            }
        }
        catch
        {
        }

        return cachedGolfSettingsObject;
    }

    private object GetGolfBallSettingsObject()
    {
        if (cachedGolfBallSettingsObject != null)
        {
            return cachedGolfBallSettingsObject;
        }

        InitializeSwingMathReflection();

        try
        {
            if (cachedGolfBallSettingsProperty != null)
            {
                cachedGolfBallSettingsObject = cachedGolfBallSettingsProperty.GetValue(null, null);
            }
        }
        catch
        {
        }

        return cachedGolfBallSettingsObject;
    }

    private void InitializeMatchSetupRulesReflection()
    {
        if (matchSetupRulesReflectionInitialized)
        {
            return;
        }

        matchSetupRulesReflectionInitialized = true;
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type rulesType = assemblies[i].GetType("MatchSetupRules");
                if (rulesType == null)
                {
                    continue;
                }

                cachedMatchSetupRuleEnumType = rulesType.GetNestedType("Rule", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo[] methods = rulesType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    MethodInfo method = methods[methodIndex];
                    if (method.Name != "GetValue")
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && (cachedMatchSetupRuleEnumType == null || parameters[0].ParameterType == cachedMatchSetupRuleEnumType))
                    {
                        cachedMatchSetupGetValueMethod = method;
                        break;
                    }
                }

                if (cachedMatchSetupRuleEnumType != null)
                {
                    try
                    {
                        cachedMatchSetupSwingPowerRuleValue = Enum.Parse(cachedMatchSetupRuleEnumType, "SwingPower", true);
                    }
                    catch
                    {
                        cachedMatchSetupSwingPowerRuleValue = null;
                    }
                }

                if (cachedMatchSetupGetValueMethod != null && cachedMatchSetupSwingPowerRuleValue != null)
                {
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private bool TryGetServerSwingPowerMultiplier(out float swingPowerMultiplier)
    {
        swingPowerMultiplier = 1f;
        InitializeMatchSetupRulesReflection();

        if (cachedMatchSetupGetValueMethod == null || cachedMatchSetupSwingPowerRuleValue == null)
        {
            return false;
        }

        try
        {
            cachedMatchSetupGetValueArgs[0] = cachedMatchSetupSwingPowerRuleValue;
            object result = cachedMatchSetupGetValueMethod.Invoke(null, cachedMatchSetupGetValueArgs);
            if (result == null)
            {
                return false;
            }

            float value = Convert.ToSingle(result);
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            {
                return false;
            }

            swingPowerMultiplier = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private float GetRuntimeLinearAirDragFactor()
    {
        object golfBallSettings = GetGolfBallSettingsObject();
        float drag = ModReflectionHelper.GetFloatMemberValue(golfBallSettings, "LinearAirDragFactor", 0.0003f);
        return float.IsNaN(drag) || float.IsInfinity(drag) || drag <= 0f ? 0.0003f : drag;
    }

    private float EvaluateVerticalAtHorizontalDistanceWithDrag(float launchSpeed, float pitchRad, float targetDistance, float airDragFactor, float deltaTime, float gravityY)
    {
        float vx = launchSpeed * Mathf.Cos(pitchRad);
        float vy = launchSpeed * Mathf.Sin(pitchRad);
        if (vx <= 0.0001f)
        {
            return float.NaN;
        }

        float x = 0f;
        float y = 0f;
        float previousX = 0f;
        float previousY = 0f;

        for (int i = 0; i < 600; i++)
        {
            previousX = x;
            previousY = y;

            vy += gravityY * deltaTime;
            float speedSquared = vx * vx + vy * vy;
            float damping = Mathf.Max(0f, 1f - airDragFactor * speedSquared * deltaTime);
            vx *= damping;
            vy *= damping;

            x += vx * deltaTime;
            y += vy * deltaTime;

            if (x >= targetDistance)
            {
                float segment = x - previousX;
                if (segment <= 0.00001f)
                {
                    return y;
                }

                float t = Mathf.Clamp01((targetDistance - previousX) / segment);
                return Mathf.Lerp(previousY, y, t);
            }

            if (Mathf.Abs(vx) < 0.0001f)
            {
                break;
            }
        }

        return float.NaN;
    }

    private bool TrySolveRequiredSpeedWithDrag(float horizontalDistance, float heightDifference, float swingPitch, out float solvedSpeed)
    {
        solvedSpeed = 0f;
        if (horizontalDistance < 0.01f)
        {
            solvedSpeed = 0.1f;
            return true;
        }

        float pitchRad = swingPitch * Mathf.Deg2Rad;
        float cos = Mathf.Cos(pitchRad);
        if (cos <= 0.0001f)
        {
            return false;
        }

        float airDrag = GetRuntimeLinearAirDragFactor();
        float dt = Mathf.Clamp(Time.fixedDeltaTime, 0.005f, 0.04f);
        float gravityY = -Mathf.Abs(trajectoryGravity);
        float bestSpeed = 0f;
        float bestAbsError = float.MaxValue;
        bool hasPrev = false;
        float previousSpeed = 0f;
        float previousError = 0f;
        bool hasBracket = false;
        float lowSpeed = 0f;
        float highSpeed = 0f;
        float lowError = 0f;

        for (float speed = 2f; speed <= 240f; speed += 2f)
        {
            float yAtDistance = EvaluateVerticalAtHorizontalDistanceWithDrag(speed, pitchRad, horizontalDistance, airDrag, dt, gravityY);
            if (float.IsNaN(yAtDistance) || float.IsInfinity(yAtDistance))
            {
                continue;
            }

            float error = yAtDistance - heightDifference;
            float absError = Mathf.Abs(error);
            if (absError < bestAbsError)
            {
                bestAbsError = absError;
                bestSpeed = speed;
            }

            if (hasPrev && Mathf.Sign(error) != Mathf.Sign(previousError))
            {
                hasBracket = true;
                lowSpeed = previousSpeed;
                highSpeed = speed;
                lowError = previousError;
                break;
            }

            hasPrev = true;
            previousSpeed = speed;
            previousError = error;
        }

        if (hasBracket)
        {
            for (int i = 0; i < 16; i++)
            {
                float midSpeed = (lowSpeed + highSpeed) * 0.5f;
                float midY = EvaluateVerticalAtHorizontalDistanceWithDrag(midSpeed, pitchRad, horizontalDistance, airDrag, dt, gravityY);
                if (float.IsNaN(midY) || float.IsInfinity(midY))
                {
                    break;
                }

                float midError = midY - heightDifference;
                float midAbsError = Mathf.Abs(midError);
                if (midAbsError < bestAbsError)
                {
                    bestAbsError = midAbsError;
                    bestSpeed = midSpeed;
                }

                if (Mathf.Sign(midError) == Mathf.Sign(lowError))
                {
                    lowSpeed = midSpeed;
                    lowError = midError;
                }
                else
                {
                    highSpeed = midSpeed;
                }
            }
        }

        if (bestSpeed <= 0.0001f || float.IsNaN(bestSpeed) || float.IsInfinity(bestSpeed))
        {
            return false;
        }

        solvedSpeed = bestSpeed;
        return true;
    }

    // E10: 2D aim + speed compensation solver.
    //
    // The 1D speed-only solver (TrySolveLaunchSpeedWindAware) aims directly at
    // the hole and finds the best power for that aim. Crosswind drift is never
    // cancelled because the aim direction isn't touched. User reported the
    // ball lands "slightly left of the hole" in a left-blowing crosswind —
    // exactly what this solver produces.
    //
    // Fix: iteratively nudge the aim target by the landing miss vector so
    // under wind the ball curves into the hole. Converges in 3–5 iterations
    // because wind force is small relative to ball speed (near-linear response
    // to aim changes). Emits both the compensated aim target AND the optimal
    // speed so CalculateIdealSwingParameters can update currentAimTargetPosition
    // — that propagates to Mimi's predicted trail, camera aim assist, and the
    // release-power selection.
    private bool TrySolveWindCompensatedAim(Vector3 shotOrigin, Vector3 holePos, float swingPitch,
                                             out Vector3 compensatedAim, out float solvedSpeed)
    {
        compensatedAim = holePos;
        solvedSpeed = 0f;

        Vector3 wind = GetCachedWindVector();
        float ballWF = GetBallWindFactor();
        float ballCWF = GetBallCrossWindFactor();
        float airDrag = GetRuntimeLinearAirDragFactor();

        for (int iter = 0; iter < 6; iter++)
        {
            // Find best speed for the *current* compensated aim direction.
            if (!TrySolveLaunchSpeedWindAware(shotOrigin, compensatedAim, swingPitch, out float iterSpeed))
            {
                return false;
            }
            solvedSpeed = iterSpeed;

            // Forward-sim at that speed to see where the ball *actually* lands.
            Vector3 horizToAim = compensatedAim - shotOrigin;
            horizToAim.y = 0f;
            if (horizToAim.sqrMagnitude < 0.0001f) return true;
            Vector3 aimDirHoriz = horizToAim.normalized;
            float pitchRad = swingPitch * Mathf.Deg2Rad;
            Vector3 launchDir = aimDirHoriz * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad);
            Vector3 landing = SimulateBallLandingPoint(shotOrigin, launchDir, iterSpeed,
                                                       wind, ballWF, ballCWF, airDrag, holePos.y);

            // Horizontal miss — we ignore Y because the landing sim already
            // terminates at holePos.y on the way down.
            Vector3 miss = holePos - landing;
            miss.y = 0f;
            float missSq = miss.sqrMagnitude;
            if (missSq < 0.25f) // < 0.5m from hole center
            {
                return true;
            }

            // Nudge aim by the full miss vector. Damping wasn't needed in
            // practice — the wind response is close enough to linear that a
            // single-step correction converges within 3 iterations.
            compensatedAim += miss;
        }

        // Didn't converge but last iteration is close enough for gameplay.
        return true;
    }

    // E12 iterative crosswind aim compensation.
    //
    // Previous single-pass approach landed within 0.5-2m of the hole. Good,
    // but not perfect. Iterating the correction (using the prior pass's
    // aim+speed as the starting point for the next sim) converges to <0.25m
    // for most shots in 2-3 iterations.
    //
    // Algorithm:
    //   aim_k = hole                  (initial guess)
    //   for k in 1..maxIter:
    //     speed_k = 1D-solve(origin, aim_k, pitch)
    //     landing_k = sim(origin, aim_k, speed_k)
    //     err_k = hole - landing_k
    //     if |err_k| < epsilon: done
    //     aim_{k+1} = aim_k + err_k   (shift aim by observed miss)
    //
    // The nonlinearity of wind drift vs aim direction means each iteration's
    // correction is approximate, but the error shrinks geometrically because
    // the drift error is smooth and small after the first pass.
    private bool TrySolveAimAndSpeedSinglePass(Vector3 shotOrigin, Vector3 holePos, float swingPitch,
                                                out Vector3 compensatedAim, out float solvedSpeed)
    {
        compensatedAim = holePos;
        solvedSpeed = 0f;

        Vector3 horizToHole = holePos - shotOrigin;
        horizToHole.y = 0f;
        if (horizToHole.sqrMagnitude < 0.0001f)
        {
            return TrySolveLaunchSpeedWindAware(shotOrigin, holePos, swingPitch, out solvedSpeed);
        }

        Vector3 wind = GetCachedWindVector();
        float ballWF = GetBallWindFactor();
        float ballCWF = GetBallCrossWindFactor();
        bool isRocketDriver = IsLocalPlayerUsingRocketDriver();
        float airDrag = GetGameExactAirDragFactor(isRocketDriver);
        float pitchRad = swingPitch * Mathf.Deg2Rad;

        // E14b: cache the full-physics solve. The 2D iterative solver runs
        // SimulateBallFullRestPoint up to maxIter times, each doing ~5000
        // raycast-driven physics steps. At 10Hz call rate this is hundreds
        // of thousands of raycasts per second — enough to tank the frame
        // rate. Skip the solve entirely if the inputs haven't meaningfully
        // changed since the last successful solve.
        const float ballEpsSq = 0.25f;      // 0.5m
        const float holeEpsSq = 0.25f;      // 0.5m
        const float windEpsSq = 1f;         // 1 m/s
        const float pitchEps = 0.5f;        // 0.5°
        if (solveCacheValid &&
            (shotOrigin - solveCacheBallPos).sqrMagnitude < ballEpsSq &&
            (holePos - solveCacheHolePos).sqrMagnitude < holeEpsSq &&
            (wind - solveCacheWind).sqrMagnitude < windEpsSq &&
            Mathf.Abs(swingPitch - solveCachePitch) < pitchEps)
        {
            compensatedAim = solveCacheAim;
            solvedSpeed = solveCacheSpeed;
            // diagnostics already populated from the prior fresh solve — leave as-is.
            return solveCacheSuccess;
        }

        Vector3 aimCurrent = holePos;
        Vector3 bestAim = holePos;
        float bestSpeed = 0f;
        float bestErrSq = float.MaxValue;
        bool anySolved = false;
        const int maxIter = 5;
        const float convergenceEpsilonSq = 0.0625f; // 0.25m
        lastSolverConverged = false;
        lastSolverIterCount = 0;
        // Hard cap on how far the iterative correction may drift the aim
        // away from the original hole target. Drift larger than this is
        // almost always the iteration going haywire (oscillation, local
        // minimum, or a bad 1D solve on iter 0) rather than legitimate
        // wind compensation — so we abort and use the last-known-good aim.
        const float maxAimDriftSq = 144f; // 12m

        for (int iter = 0; iter < maxIter; iter++)
        {
            // Safety clamp: if aim has drifted absurdly far from the hole,
            // stop iterating and use whichever aim was best so far.
            if ((aimCurrent - holePos).sqrMagnitude > maxAimDriftSq)
            {
                break;
            }

            if (!TrySolveLaunchSpeedWindAware(shotOrigin, aimCurrent, swingPitch, out float iterSpeed))
            {
                // 1D solver failed — if we already have a previous iteration's
                // solution, keep it. Otherwise bail.
                if (!anySolved) return false;
                break;
            }
            anySolved = true;

            Vector3 horizToAim = aimCurrent - shotOrigin;
            horizToAim.y = 0f;
            if (horizToAim.sqrMagnitude < 0.0001f) break;
            Vector3 aimDirHoriz = horizToAim.normalized;
            Vector3 launchDir = aimDirHoriz * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad);

            // E15b: revert to impact-target. E14 used SimulateBallFullRestPoint
            // to target the ball's rest position (accounting for bounce+roll),
            // but the simplified dt + stepping in that helper didn't match the
            // real bounce/roll tightly enough, so the solver would converge
            // to aim points that produced landing errors several meters off.
            // E13's simple impact-target solver landed within 1-2m of hole
            // consistently — the ball still rolls past but that's a lesser
            // evil than the aim being systematically wrong. Keeping the full
            // rest helper in the codebase for the future.
            Vector3 landing = SimulateBallLandingPoint(shotOrigin, launchDir, iterSpeed,
                                                        wind, ballWF, ballCWF, airDrag, holePos.y);

            Vector3 err = holePos - landing;
            err.y = 0f;
            float errSq = err.sqrMagnitude;

            // Track the best aim across iterations — high-wind shots can
            // oscillate near the minimum instead of converging monotonically,
            // so we snapshot the iteration that achieved the lowest landing
            // error rather than trusting the last one.
            if (errSq < bestErrSq)
            {
                bestErrSq = errSq;
                bestAim = aimCurrent;
                bestSpeed = iterSpeed;
            }

            if (errSq < convergenceEpsilonSq)
            {
                bestAim = aimCurrent;
                bestSpeed = iterSpeed;
                bestErrSq = errSq;
                lastSolverIterCount = iter + 1;
                lastSolverConverged = true;
                break;
            }

            // Shift aim by the observed miss, scaled down as iterations go
            // to damp oscillation in strong-wind regimes where the landing
            // function is highly sensitive to aim direction.
            float damp;
            if (iter == 0) damp = 1.0f;
            else if (iter < 3) damp = 0.85f;
            else if (iter < 6) damp = 0.6f;
            else damp = 0.4f;
            aimCurrent += err * damp;
        }

        if (!lastSolverConverged)
        {
            lastSolverIterCount = maxIter;
        }
        lastSolverFinalErrM = Mathf.Sqrt(bestErrSq);
        compensatedAim = bestAim;
        solvedSpeed = bestSpeed;

        // E15f: roll-offset correction REMOVED. The one-shot correction pass
        // assumed the roll vector was invariant under small aim shifts — it
        // isn't. When the solver shifted aim back by the observed roll, the
        // new (shorter) aim triggered a lower 1D-solved launch speed, which
        // meant less real roll, so the shift was systematically too large.
        // For short shots (4-10m) this pushed the aim PAST the player origin,
        // producing wild 20m misses. The impact-target solver alone (E13/E15b)
        // reliably lands the ball within 0.5-1.5m of the hole — ball then
        // rolls past, but that's a predictable bias better corrected by a
        // pitch/club selection layer than by aim-shift math.
        lastSolverCompensatedAim = compensatedAim;
        lastSolverSpeedMps = solvedSpeed;

        solveCacheValid = true;
        solveCacheBallPos = shotOrigin;
        solveCacheHolePos = holePos;
        solveCacheWind = wind;
        solveCachePitch = swingPitch;
        solveCacheAim = compensatedAim;
        solveCacheSpeed = solvedSpeed;
        solveCacheSuccess = anySolved;
        return anySolved;
    }

    // E8b: wind-aware launch-speed solver.
    //
    // Forward-sims the ball using the exact Hittable.ApplyAirDamping physics
    // (same formula as the predicted trail) and searches for the launch speed
    // that puts the ball closest to the 3D target. Unlike TrySolveRequiredSpeedWithDrag
    // this accounts for wind — the ball will accelerate/decelerate depending on
    // whether the wind is a head or tail component, and lateral drift in crosswind
    // shifts the landing point.
    //
    // Search strategy: coarse scan 5..220 m/s in 3 m/s steps → refine around best
    // in 0.5 m/s steps. Returns false if nothing gets within 50m of target
    // (likely out of range regardless).
    private bool TrySolveLaunchSpeedWindAware(Vector3 shotOrigin, Vector3 targetPos, float swingPitch, out float solvedSpeed)
    {
        solvedSpeed = 0f;

        Vector3 toTarget = targetPos - shotOrigin;
        Vector3 horizToTarget = new Vector3(toTarget.x, 0f, toTarget.z);
        float targetDist = horizToTarget.magnitude;
        if (targetDist < 0.5f)
        {
            solvedSpeed = 5f;
            return true;
        }

        Vector3 aimHoriz = horizToTarget.normalized;
        float pitchRad = swingPitch * Mathf.Deg2Rad;
        Vector3 launchDir = aimHoriz * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad);

        Vector3 wind = GetCachedWindVector();
        float ballWF = GetBallWindFactor();
        float ballCWF = GetBallCrossWindFactor();
        // E11: rocket driver swings use a different air drag coefficient.
        bool solverIsRocketDriver = IsLocalPlayerUsingRocketDriver();
        float airDrag = GetGameExactAirDragFactor(solverIsRocketDriver);

        float bestSpeed = 100f;
        float bestDistSq = float.MaxValue;

        // E15c: cap search at the game's actual max launch speed. Previously
        // scanned up to 220 m/s, which allowed the solver to return speeds
        // > 85 m/s (MaxPowerSwingHitSpeed) — physically unreachable. The game
        // caps power at 100% so any "solved" speed above 85 just yields an
        // 85 m/s launch and the ball falls short. Checked at build time via
        // reflected GolfBallSettings but fall back to 86 for safety.
        float maxSearchSpeed = GetBallMaxSwingHitSpeed();
        if (maxSearchSpeed < 10f) maxSearchSpeed = 86f;
        maxSearchSpeed += 1f; // 1m/s headroom for refine band

        // Coarse scan
        for (float speed = 5f; speed <= maxSearchSpeed; speed += 2f)
        {
            Vector3 landing = SimulateBallLandingPoint(shotOrigin, launchDir, speed, wind, ballWF, ballCWF, airDrag, targetPos.y);
            float d2 = (landing - targetPos).sqrMagnitude;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                bestSpeed = speed;
            }
        }

        // E15c: tighter refine step — 0.2 m/s instead of 0.5 m/s. At 52m range
        // with pitch 45°, a 0.5 m/s speed step shifts landing by ~1m, which
        // was the noise floor keeping the 2D aim solver from converging on
        // high-arc shots.
        float lo = Mathf.Max(1f, bestSpeed - 2f);
        float hi = Mathf.Min(maxSearchSpeed, bestSpeed + 2f);
        for (float speed = lo; speed <= hi; speed += 0.2f)
        {
            Vector3 landing = SimulateBallLandingPoint(shotOrigin, launchDir, speed, wind, ballWF, ballCWF, airDrag, targetPos.y);
            float d2 = (landing - targetPos).sqrMagnitude;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                bestSpeed = speed;
            }
        }

        if (bestDistSq > 2500f) // >50m miss → bail, let the caller fall back
        {
            return false;
        }

        solvedSpeed = bestSpeed;
        return true;
    }

    // E14: full-physics sim including bounce + roll, returns the ball's final
    // rest point. Used by the 2D aim solver so it can target rest-on-hole
    // instead of first-impact-on-hole (short shots at 45° pitch bounce and
    // roll 10m+ past impact on the green, so aiming for impact-on-hole
    // leaves the ball rolling far past).
    //
    // Mirrors BuildPredictedTrajectoryPoints' physics:
    //   - airborne phase with wind + drag
    //   - bounce chain using TerrainLayerSettings per-contact bounciness
    //   - ground roll phase using terrain layer damping
    //   - hole-in termination
    //
    // Deliberately skips: trail painting, impact-preview storage, path caching.
    // Limits step/time counts match BuildPredictedTrajectoryPoints for fidelity.
    private Vector3 SimulateBallFullRestPoint(Vector3 shotOrigin, Vector3 launchDir, float launchSpeed,
                                                Vector3 windVector, float ballWF, float ballCWF, float airDragFactor,
                                                bool includeBounceRoll)
    {
        // E14b: the solver calls this inside an iteration loop, so cap step
        // counts aggressively — the solver only needs a ballpark rest point,
        // not visual-fidelity physics. ~1.5x the base dt keeps accuracy high
        // enough while cutting raycasts by 66%.
        float dt = Time.fixedDeltaTime * 1.5f;
        int maxAirSteps = 180;
        int maxRollSteps = 180;
        float maxTime = 6f;
        Vector3 gravity = Physics.gravity;
        Vector3 position = shotOrigin;
        Vector3 velocity = launchDir * launchSpeed;
        float elapsed = 0f;
        LayerMask groundMask = GetBallGroundableMask();

        Vector3 impactPoint = position;
        Vector3 impactNormal = Vector3.up;
        bool impactResolved = false;

        // ── Airborne phase ──
        for (int i = 0; i < maxAirSteps && elapsed < maxTime; i++)
        {
            Vector3 prev = position;
            velocity += gravity * dt;

            Vector3 effectiveWind = Vector3.zero;
            if (windVector.sqrMagnitude > 0.0001f && velocity.sqrMagnitude > 0.0001f)
            {
                Vector3 wa = Vector3.Project(windVector, velocity);
                Vector3 wc = windVector - wa;
                effectiveWind = wa * ballWF + wc * ballCWF;
            }
            Vector3 relVel = velocity - effectiveWind;
            float dragDelta = Mathf.Max(0f, airDragFactor * relVel.sqrMagnitude * dt);
            velocity -= relVel * dragDelta;
            position += velocity * dt;

            // NOTE: no IsPositionInHole check during airborne phase in the
            // solver sim. A 45° shot aimed directly at the hole descends
            // through the hole volume in one step, which would make the
            // sim report "holed in" and short-circuit with no bounce/roll.
            // That would collapse the 2D solver to always-aim-at-hole. We
            // only want hole-in to count if the ball is rolling on the
            // ground at the hole — that's handled in the roll phase below.
            RaycastHit hit;
            if (Physics.Linecast(prev, position, out hit, groundMask, QueryTriggerInteraction.Ignore))
            {
                impactResolved = true;
                impactPoint = hit.point;
                impactNormal = hit.normal.sqrMagnitude > 0.0001f ? hit.normal : Vector3.up;
                position = impactPoint;
                break;
            }
            if (position.y < -200f) return position;
            elapsed += dt;
        }

        if (!impactResolved || !includeBounceRoll)
        {
            return position;
        }

        // ── Bounce chain ──
        const float BOUNCE_LIFTOFF_SPEED = 0.5f;
        const int MAX_BOUNCES = 12;
        Vector3 currentGroundNormal = impactNormal;
        if (currentGroundNormal.y < 0.0001f) currentGroundNormal = Vector3.up;
        bool grounded = false;

        for (int b = 0; b < MAX_BOUNCES; b++)
        {
            float layerBounciness, layerDynFriction, layerLinearDamping,
                  layerStopMaxPitch, layerRollMinPitch;
            AnimationCurve layerCurve;
            TryGetTerrainLayerAtPoint(position,
                out layerBounciness, out layerDynFriction, out layerLinearDamping,
                out layerStopMaxPitch, out layerRollMinPitch, out layerCurve);

            float normalDot = Vector3.Dot(velocity, currentGroundNormal);
            Vector3 vNormal = currentGroundNormal * normalDot;
            Vector3 vTangent = velocity - vNormal;
            Vector3 vOut = vTangent - vNormal * Mathf.Clamp01(layerBounciness);
            float outNormalSpeed = Vector3.Dot(vOut, currentGroundNormal);

            if (outNormalSpeed < BOUNCE_LIFTOFF_SPEED)
            {
                velocity = vTangent;
                grounded = true;
                break;
            }
            velocity = vOut;

            bool nextImpact = false;
            for (int j = 0; j < maxAirSteps && elapsed < maxTime; j++)
            {
                Vector3 prev = position;
                velocity += gravity * dt;

                Vector3 eWind = Vector3.zero;
                if (windVector.sqrMagnitude > 0.0001f && velocity.sqrMagnitude > 0.0001f)
                {
                    Vector3 wa = Vector3.Project(windVector, velocity);
                    Vector3 wc = windVector - wa;
                    eWind = wa * ballWF + wc * ballCWF;
                }
                Vector3 rv = velocity - eWind;
                float dd = Mathf.Max(0f, airDragFactor * rv.sqrMagnitude * dt);
                velocity -= rv * dd;
                position += velocity * dt;

                // E15e: NO IsPositionInHole during solver bounce arcs either.
                // The solver wants the actual rest point so it can compute the
                // roll offset. Letting the sim short-circuit on hole overlap
                // made short-iron shots return rest==hole even when the real
                // ball rolls past, killing the roll correction.

                RaycastHit hit;
                if (Physics.Linecast(prev, position, out hit, groundMask, QueryTriggerInteraction.Ignore))
                {
                    position = hit.point;
                    currentGroundNormal = hit.normal.sqrMagnitude > 0.0001f ? hit.normal : Vector3.up;
                    nextImpact = true;
                    break;
                }
                if (position.y < -200f) return position;
                elapsed += dt;
            }
            if (!nextImpact) return position;
        }

        if (!grounded) return position;

        // ── Roll phase ──
        float rollingDownhillTime = 0f;
        float rollElapsed = 0f;
        Vector3 rollNormal = currentGroundNormal;
        float maxRollTime = maxTime;

        for (int i = 0; i < maxRollSteps && rollElapsed < maxRollTime; i++)
        {
            float layerBounciness, layerDynFriction, layerLinearDamping,
                  layerStopMaxPitch, layerRollMinPitch;
            AnimationCurve layerCurve;
            TryGetTerrainLayerAtPoint(position,
                out layerBounciness, out layerDynFriction, out layerLinearDamping,
                out layerStopMaxPitch, out layerRollMinPitch, out layerCurve);

            float groundPitchDeg = Vector3.Angle(Vector3.up, rollNormal);
            Vector3 velAlongGround = Vector3.ProjectOnPlane(velocity, rollNormal);
            float speedAlong = velAlongGround.magnitude;
            if (speedAlong < 0.05f) break;

            float damping = ComputeTerrainDamping(groundPitchDeg, speedAlong, rollingDownhillTime,
                layerLinearDamping, layerStopMaxPitch, layerRollMinPitch, layerCurve);

            float fac = Mathf.Max(0f, 1f - damping * dt);
            velocity = velocity - velAlongGround + velAlongGround * fac;
            position += velocity * dt;

            // E15e: NO IsPositionInHole during solver roll phase either. Our
            // IsPositionInHole is an AABB test that fires for any ball rolling
            // near the cup, but real GolfHoleTrigger.OnTriggerEnter needs the
            // collider to actually overlap + fall in — a fast-rolling ball
            // passes over the rim without holing. Letting the sim report
            // rest==hole on near-misses defeated the roll correction.

            Vector3 probeOrigin = position + Vector3.up * 0.5f;
            RaycastHit groundHit;
            if (Physics.Raycast(probeOrigin, Vector3.down, out groundHit, 2f, groundMask, QueryTriggerInteraction.Ignore))
            {
                position = groundHit.point;
                rollNormal = groundHit.normal.sqrMagnitude > 0.0001f ? groundHit.normal : Vector3.up;
            }

            if (velocity.y < -0.005f) rollingDownhillTime += dt;
            else rollingDownhillTime = 0f;

            rollElapsed += dt;
            elapsed += dt;
        }

        return position;
    }

    // Forward-sim a ball launch until it descends through targetGroundY. Matches
    // the exact physics of BuildPredictedTrajectoryPoints (E8 formula).
    // Forward-sim helper used by the 1D solver and single-pass 2D compensator.
    // Uses the EXACT decompiled physics:
    //   - dt = Time.fixedDeltaTime (no clamping — matches game physics step)
    //   - gravity = Physics.gravity
    //   - wind application via Hittable.ApplyAirDamping formula (E8)
    //   - drag coefficient selected by rocket driver state
    //   - ball WindFactor / CrossWindFactor read from HittableSettings.Wind
    // Callers pass the per-shot factors as parameters so this method stays pure.
    private Vector3 SimulateBallLandingPoint(Vector3 shotOrigin, Vector3 launchDir, float launchSpeed,
                                              Vector3 windVector, float ballWF, float ballCWF, float airDragFactor,
                                              float targetGroundY)
    {
        float dt = Time.fixedDeltaTime;
        Vector3 gravity = Physics.gravity;
        Vector3 position = shotOrigin;
        Vector3 velocity = launchDir * launchSpeed;

        for (int i = 0; i < predictedPathMaxSteps; i++)
        {
            velocity += gravity * dt;

            Vector3 effectiveWind = Vector3.zero;
            if (windVector.sqrMagnitude > 0.0001f && velocity.sqrMagnitude > 0.0001f)
            {
                Vector3 windAlong = Vector3.Project(windVector, velocity);
                Vector3 windCross = windVector - windAlong;
                effectiveWind = windAlong * ballWF + windCross * ballCWF;
            }
            Vector3 relVel = velocity - effectiveWind;
            float dragDelta = Mathf.Max(0f, airDragFactor * relVel.sqrMagnitude * dt);
            velocity -= relVel * dragDelta;

            position += velocity * dt;

            // Return as soon as the ball crosses the target ground height on descent.
            if (position.y <= targetGroundY && velocity.y < 0f)
            {
                return position;
            }
            if (position.y < -200f)
            {
                break;
            }
        }
        return position;
    }

    private float CalculateRequiredPowerForPitch(float horizontalDistance, float heightDifference, float swingPitch)
    {
        if (horizontalDistance < 0.01f)
        {
            return 0.05f;
        }

        if (Mathf.Abs(swingPitch) < 0.5f)
        {
            return Mathf.Clamp(CalculateIdealPower(horizontalDistance, heightDifference), 0.05f, 2f);
        }

        float solvedVelocity;
        if (TrySolveRequiredSpeedWithDrag(horizontalDistance, heightDifference, swingPitch, out solvedVelocity))
        {
            return Mathf.Clamp(EstimatePowerFromLaunchSpeed(solvedVelocity), 0.05f, 2f);
        }

        float pitchRad = swingPitch * Mathf.Deg2Rad;
        float cos = Mathf.Cos(pitchRad);
        float denominator = 2f * cos * cos * (horizontalDistance * Mathf.Tan(pitchRad) - heightDifference);
        if (denominator <= 0.001f)
        {
            return Mathf.Clamp(CalculateIdealPower(horizontalDistance, heightDifference), 0.05f, 2f);
        }

        float requiredVelocitySquared = trajectoryGravity * horizontalDistance * horizontalDistance / denominator;
        if (requiredVelocitySquared <= 0.001f || float.IsNaN(requiredVelocitySquared) || float.IsInfinity(requiredVelocitySquared))
        {
            return Mathf.Clamp(CalculateIdealPower(horizontalDistance, heightDifference), 0.05f, 2f);
        }

        float requiredVelocity = Mathf.Sqrt(requiredVelocitySquared);
        return Mathf.Clamp(EstimatePowerFromLaunchSpeed(requiredVelocity), 0.05f, 2f);
    }

    private float CalculateIdealPower(float distance, float heightDifference)
    {
        float horizontalDistance = Mathf.Max(0.01f, distance);
        float referencePitch = 45f;
        float pitchRad = referencePitch * Mathf.Deg2Rad;
        float cos = Mathf.Cos(pitchRad);
        float denominator = 2f * cos * cos * (horizontalDistance * Mathf.Tan(pitchRad) - heightDifference);

        float requiredSpeed;
        if (denominator > 0.001f)
        {
            float requiredSpeedSquared = trajectoryGravity * horizontalDistance * horizontalDistance / denominator;
            requiredSpeed = requiredSpeedSquared <= 0.001f || float.IsNaN(requiredSpeedSquared) || float.IsInfinity(requiredSpeedSquared)
                ? Mathf.Sqrt(horizontalDistance * trajectoryGravity)
                : Mathf.Sqrt(requiredSpeedSquared);
        }
        else
        {
            requiredSpeed = Mathf.Sqrt(horizontalDistance * trajectoryGravity);
        }

        return Mathf.Clamp(EstimatePowerFromLaunchSpeed(Mathf.Max(0.1f, requiredSpeed)), 0.05f, 2f);
    }

    private void CalculateIdealSwingParameters(bool forceHoleRefresh)
    {
        if (playerGolfer == null)
        {
            return;
        }

        try
        {
            Vector3 playerPosition = playerGolfer.transform.position;
            Vector3 ballPosition = golfBall != null ? golfBall.transform.position : playerPosition;

            if (!FindHoleOnly(forceHoleRefresh))
            {
                return;
            }

            currentAimTargetPosition = GetAimTargetPosition(ballPosition);
            currentSwingOriginPosition = GetSwingOriginPosition();

            Vector3 shotOrigin = golfBall != null
                ? golfBall.transform.position
                : (currentSwingOriginPosition != Vector3.zero ? currentSwingOriginPosition : playerPosition);

            Vector3 toTarget = currentAimTargetPosition - shotOrigin;
            Vector3 horizontalToTarget = new Vector3(toTarget.x, 0f, toTarget.z);
            float horizontalDistance = horizontalToTarget.magnitude;
            float heightDifference = toTarget.y;

            float currentPower;
            float currentPitch;
            bool isChargingSwing;
            bool isSwinging;
            if (!TryGetCurrentSwingValues(out currentPower, out currentPitch, out isChargingSwing, out isSwinging))
            {
                currentPitch = idealSwingPitch;
            }

            idealSwingPitch = currentPitch;

            // E11: single-pass crosswind aim compensation.
            //
            // Previous E10 attempt iterated up to 6 times, accumulating the 1D
            // solver's quantization residual (0.5 m/s refine ≈ 2-3m range error)
            // and drifting aim 5-10m off the hole. Fixed by using ONE correction
            // pass: run the 1D solver against the raw hole target, simulate at
            // that speed to see the actual landing under wind, then offset the
            // aim by the resulting miss vector and re-run the 1D solver once.
            // No loop, no accumulation.
            RefreshBallWindFactors();
            RefreshBallPhysicsMaterial();
            Vector3 rawHoleTarget = currentAimTargetPosition;
            float physicsPower;
            if (TrySolveAimAndSpeedSinglePass(shotOrigin, rawHoleTarget, idealSwingPitch,
                                              out Vector3 compensatedAim, out float windAwareSpeed))
            {
                currentAimTargetPosition = compensatedAim;
                windCompensatedAimTarget = compensatedAim;
                toTarget = currentAimTargetPosition - shotOrigin;
                horizontalToTarget = new Vector3(toTarget.x, 0f, toTarget.z);
                horizontalDistance = horizontalToTarget.magnitude;
                heightDifference = toTarget.y;
                physicsPower = Mathf.Clamp(EstimatePowerFromLaunchSpeed(windAwareSpeed), 0.05f, 2f);
            }
            else
            {
                windCompensatedAimTarget = Vector3.zero;
                physicsPower = CalculateRequiredPowerForPitch(horizontalDistance, heightDifference, idealSwingPitch);
            }

            // User-facing bug fix: vanilla Mimi happily fires at 115% (the game's
            // MaxSwingOvercharge) when the hole is out of reach, which is wildly
            // inaccurate because overcharged shots have extra spread/drift. Clamp
            // to 100% by default — config key allow_overcharge can re-enable it.
            if (!allowOvercharge)
            {
                physicsPower = Mathf.Min(physicsPower, 1f);
            }
            idealSwingPower = physicsPower;
        }
        catch
        {
        }
    }

    private bool FindHoleOnly(bool force)
    {
        if (playerGolfer == null)
        {
            return false;
        }

        float currentTime = Time.time;
        if (!force && currentTime < nextHoleSearchTime)
        {
            return holePosition != Vector3.zero;
        }

        nextHoleSearchTime = currentTime + holeSearchInterval;

        try
        {
            holePosition = Vector3.zero;
            flagPosition = Vector3.zero;
            currentAimTargetPosition = Vector3.zero;
            windCompensatedAimTarget = Vector3.zero;
            return FindGolfHoleComponent();
        }
        catch
        {
            return false;
        }
    }

    private bool FindGolfHoleComponent()
    {
        if (playerGolfer == null)
        {
            return false;
        }

        try
        {
            Component[] allComponents = FindAllComponents();
            Vector3 referencePosition = golfBall != null ? golfBall.transform.position : playerGolfer.transform.position;
            float closestHoleDistanceSq = float.MaxValue;
            bool foundHole = false;

            for (int i = 0; i < allComponents.Length; i++)
            {
                Component comp = allComponents[i];
                if (comp == null || comp.GetType().Name != "GolfHole")
                {
                    continue;
                }

                Vector3 holeCandidate;
                Vector3 flagCandidate;
                if (!TryResolveHoleCandidate(comp, out holeCandidate, out flagCandidate))
                {
                    continue;
                }

                Vector3 flatReferencePosition = new Vector3(referencePosition.x, 0f, referencePosition.z);
                Vector3 flatHoleCandidate = new Vector3(holeCandidate.x, 0f, holeCandidate.z);
                float distanceSq = (flatHoleCandidate - flatReferencePosition).sqrMagnitude;
                if (distanceSq <= 0.0025f || distanceSq >= closestHoleDistanceSq)
                {
                    continue;
                }

                closestHoleDistanceSq = distanceSq;
                holePosition = holeCandidate;
                flagPosition = flagCandidate;
                foundHole = true;
            }

            // E17d: resolve the actual GREEN surface y at the cup location.
            //
            // E17c (RaycastAll at exact hole xz) failed on holes where the
            // cup is a literal hole in the terrain mesh: the single ray
            // threads through the cup opening and hits ONLY the flag pole
            // (which sits inside the cup), so hole_y gets pinned to the flag
            // top ~6m above the green. Fix: probe at a small offset ring
            // around the cup center. Any of the 4 ring points lands on the
            // solid green around the rim, which has the same y as the cup
            // opening's surface level. Take the lowest terrain hit across
            // all probes and exclude obvious flag-height hits.
            if (foundHole)
            {
                const float RING_OFFSET = 0.6f;   // 60cm — outside a standard cup
                const float PROBE_HEIGHT = 30f;
                const float PROBE_DEPTH = 60f;
                Vector3[] probeOffsets =
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(RING_OFFSET, 0f, 0f),
                    new Vector3(-RING_OFFSET, 0f, 0f),
                    new Vector3(0f, 0f, RING_OFFSET),
                    new Vector3(0f, 0f, -RING_OFFSET),
                };
                int groundMask = GetBallGroundableMask();
                float bestY = float.MaxValue;
                for (int p = 0; p < probeOffsets.Length; p++)
                {
                    Vector3 probeOrigin = new Vector3(
                        holePosition.x + probeOffsets[p].x,
                        holePosition.y + PROBE_HEIGHT,
                        holePosition.z + probeOffsets[p].z);
                    RaycastHit[] hits = Physics.RaycastAll(probeOrigin, Vector3.down, PROBE_DEPTH,
                                                          groundMask, QueryTriggerInteraction.Ignore);
                    if (hits == null) continue;
                    for (int i = 0; i < hits.Length; i++)
                    {
                        if (hits[i].point.y < bestY)
                        {
                            bestY = hits[i].point.y;
                        }
                    }
                }
                if (bestY < float.MaxValue)
                {
                    holePosition = new Vector3(holePosition.x, bestY, holePosition.z);
                }
            }

            return foundHole;
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveHoleCandidate(Component golfHoleComponent, out Vector3 resolvedHolePosition, out Vector3 resolvedFlagPosition)
    {
        resolvedHolePosition = Vector3.zero;
        resolvedFlagPosition = Vector3.zero;
        if (golfHoleComponent == null)
        {
            return false;
        }

        Vector3 componentPosition = golfHoleComponent.transform.position;
        bool hasHolePosition = false;
        bool hasFlagPosition = false;

        Transform exactHoleTransform = TryResolveTransformMember(golfHoleComponent,
            "hole",
            "Hole",
            "cup",
            "Cup",
            "holeTransform",
            "HoleTransform",
            "cupTransform",
            "CupTransform",
            "target",
            "Target");
        if (exactHoleTransform != null)
        {
            resolvedHolePosition = exactHoleTransform.position;
            hasHolePosition = IsFiniteVector3(resolvedHolePosition);
        }

        Vector3 memberVector;
        if (!hasHolePosition && TryResolveVector3Member(golfHoleComponent, out memberVector,
            "holePosition",
            "HolePosition",
            "cupPosition",
            "CupPosition",
            "targetPosition",
            "TargetPosition"))
        {
            resolvedHolePosition = memberVector;
            hasHolePosition = true;
        }

        Transform flagTransform = TryResolveTransformMember(golfHoleComponent,
            "flag",
            "Flag",
            "flagTransform",
            "FlagTransform");
        if (flagTransform != null)
        {
            resolvedFlagPosition = flagTransform.position;
            hasFlagPosition = IsFiniteVector3(resolvedFlagPosition);
        }

        if (!hasHolePosition)
        {
            resolvedHolePosition = hasFlagPosition
                ? new Vector3(resolvedFlagPosition.x, componentPosition.y, resolvedFlagPosition.z)
                : componentPosition;
            hasHolePosition = IsFiniteVector3(resolvedHolePosition);
        }

        if (!hasFlagPosition)
        {
            resolvedFlagPosition = resolvedHolePosition;
            hasFlagPosition = hasHolePosition;
        }

        return hasHolePosition && hasFlagPosition;
    }

    private Transform TryResolveTransformMember(object instance, params string[] memberNames)
    {
        for (int i = 0; i < memberNames.Length; i++)
        {
            object memberValue = ModReflectionHelper.GetMemberValue(instance, memberNames[i]);
            if (memberValue is Transform)
            {
                return (Transform)memberValue;
            }

            if (memberValue is Component)
            {
                return ((Component)memberValue).transform;
            }

            if (memberValue is GameObject)
            {
                return ((GameObject)memberValue).transform;
            }
        }

        return null;
    }

    private bool TryResolveVector3Member(object instance, out Vector3 resolvedValue, params string[] memberNames)
    {
        resolvedValue = Vector3.zero;
        for (int i = 0; i < memberNames.Length; i++)
        {
            object memberValue = ModReflectionHelper.GetMemberValue(instance, memberNames[i]);
            if (memberValue is Vector3)
            {
                resolvedValue = (Vector3)memberValue;
                return IsFiniteVector3(resolvedValue);
            }
        }

        return false;
    }

    private bool IsFiniteVector3(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
