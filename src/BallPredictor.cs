using System.Collections.Generic;
using UnityEngine;

namespace AimAssist
{
    /// <summary>
    /// Physics-based golf ball trajectory simulation.
    ///
    /// Uses real game values (MaxPowerSwingHitSpeed, SwingNormalizedPower,
    /// WindManager wind) when accessible, falls back to config defaults
    /// otherwise. Simulation uses Unity's Physics.gravity so terrain
    /// raycasts match the actual ground.
    /// </summary>
    internal static class BallPredictor
    {
        // Seconds per simulation step. 0.05 = 50ms, accurate enough for golf distances.
        private const float SimDt       = 0.05f;
        private const int   MaxSteps    = 300;  // 15 seconds of flight max
        // How much wind contributes to ball velocity per second (0–1 scale)
        private const float WindCoeff   = 0.08f;

        // ── Public helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Calculate the current ball launch speed from game data.
        /// Returns the interpolated speed between min and max based on current charge.
        /// Falls back to Plugin.BallSpeedFallback if game values aren't available.
        /// </summary>
        public static float GetLaunchSpeed(object? golfer)
        {
            float normalized = golfer != null ? GameAccess.GetSwingNormalizedPower(golfer) : 1f;
            float maxSpeed   = golfer != null ? GameAccess.GetMaxSwingHitSpeed(golfer)     : Plugin.BallSpeedFallback.Value;
            float minSpeed   = golfer != null ? GameAccess.GetMinSwingHitSpeed(golfer)     : maxSpeed * 0.25f;

            if (maxSpeed <= 0f) maxSpeed = Plugin.BallSpeedFallback.Value;
            if (minSpeed <= 0f) minSpeed = maxSpeed * 0.25f;

            // Clamp in case of overcharge (>1.0 normalised)
            float clamped = Mathf.Clamp01(normalized);
            return Mathf.Lerp(minSpeed, maxSpeed, clamped);
        }

        /// <summary>
        /// Simulate the ball's flight path and return world-space positions.
        ///
        /// <paramref name="yawDegrees"/>   – player facing direction in degrees (0=north)
        /// <paramref name="pitchDegrees"/> – upward launch angle in degrees (0=flat)
        /// <paramref name="speed"/>        – launch speed in m/s
        /// <paramref name="wind"/>         – wind velocity vector from WindManager
        /// </summary>
        public static List<Vector3> SimulateTrajectory(
            Vector3 launchPos,
            float   yawDegrees,
            float   pitchDegrees,
            float   speed,
            Vector3 wind,
            int     maxSteps = MaxSteps)
        {
            float yaw   = yawDegrees   * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosP  = Mathf.Cos(pitch);
            float sinP  = Mathf.Sin(pitch);

            // 3D launch velocity
            Vector3 vel = new Vector3(
                Mathf.Sin(yaw) * cosP * speed,
                sinP * speed,
                Mathf.Cos(yaw) * cosP * speed
            );

            float   g    = -Physics.gravity.y; // 9.81
            Vector3 pos  = launchPos + Vector3.up * 0.3f; // slight offset so we don't self-hit
            var     path = new List<Vector3>(maxSteps + 2) { pos };

            for (int i = 0; i < maxSteps; i++)
            {
                // Wind nudges horizontal velocity slowly
                vel.x += wind.x * WindCoeff * SimDt;
                vel.z += wind.z * WindCoeff * SimDt;
                vel.y -= g * SimDt;

                Vector3 step    = vel * SimDt;
                Vector3 nextPos = pos + step;

                // Terrain / obstacle collision
                if (step.sqrMagnitude > 0.0001f &&
                    Physics.Raycast(pos, step.normalized, out var hit, step.magnitude + 0.4f))
                {
                    path.Add(hit.point);
                    break;
                }

                if (nextPos.y < -100f) break; // fell out of world
                pos = nextPos;
                path.Add(pos);
            }

            return path;
        }

        /// <summary>
        /// Analytically calculate the launch pitch (degrees) needed to reach
        /// <paramref name="to"/> from <paramref name="from"/> at <paramref name="speed"/>.
        ///
        /// Returns <c>float.NaN</c> if the target is out of range at that speed.
        /// <paramref name="highArc"/> = true gives the lobbed shot; false = flat/direct.
        /// </summary>
        public static float CalculatePitch(Vector3 from, Vector3 to, float speed, bool highArc = false)
        {
            float dx  = to.x - from.x;
            float dz  = to.z - from.z;
            float d   = Mathf.Sqrt(dx * dx + dz * dz); // horizontal distance
            float dh  = to.y - from.y;                  // height difference (+up)
            float g   = -Physics.gravity.y;              // 9.81

            if (d < 0.5f) return highArc ? 80f : 10f;   // basically on top of hole

            float v2   = speed * speed;
            float disc = v2 * v2 - g * (g * d * d + 2f * dh * v2);

            if (disc < 0f) return float.NaN; // out of range

            float sqrtD    = Mathf.Sqrt(disc);
            float tanTheta = highArc
                ? (v2 + sqrtD) / (g * d)
                : (v2 - sqrtD) / (g * d);

            return Mathf.Atan(tanTheta) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Yaw (degrees) from <paramref name="from"/> pointing toward <paramref name="to"/>
        /// on the horizontal plane. 0 = north (+Z), clockwise.
        /// </summary>
        public static float CalculateYaw(Vector3 from, Vector3 to)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            return Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The predicted landing position (last point in the simulated path).
        /// Returns Vector3.zero if the path is empty.
        /// </summary>
        public static Vector3 LandingSpot(List<Vector3> path) =>
            path.Count > 0 ? path[path.Count - 1] : Vector3.zero;

        /// <summary>
        /// Binary-search for the minimum launch speed (m/s) at which
        /// <paramref name="to"/> is reachable from <paramref name="from"/>
        /// while keeping the pitch at or below <paramref name="maxPitch"/> degrees.
        ///
        /// Returns <c>float.NaN</c> if the target is out of range even at
        /// <paramref name="knownMaxSpeed"/>.
        /// </summary>
        public static float FindMinimumReachSpeed(
            Vector3 from, Vector3 to,
            float maxPitch, float knownMaxSpeed, bool highArc)
        {
            // Quick exit: out of range at max speed
            float testPitch = CalculatePitch(from, to, knownMaxSpeed, highArc);
            if (float.IsNaN(testPitch) || testPitch > maxPitch + 5f)
                return float.NaN;

            // Binary search: find the lowest speed that still lands at the target
            // within maxPitch. As speed drops, required pitch rises toward maxPitch.
            float lo = 0f, hi = knownMaxSpeed;
            for (int i = 0; i < 18; i++)          // 18 iterations → ~4 decimal places
            {
                float mid   = (lo + hi) * 0.5f;
                float pitch = CalculatePitch(from, to, mid, highArc);
                if (!float.IsNaN(pitch) && pitch <= maxPitch + 5f)
                    hi = mid;   // still reachable — try slower
                else
                    lo = mid;   // not reachable — need faster
            }
            return hi;
        }

        /// <summary>
        /// Analytically calculate the launch speed (m/s) needed to reach
        /// <paramref name="to"/> from <paramref name="from"/> at pitch angle
        /// <paramref name="pitchDegrees"/>.
        ///
        /// Returns <c>float.NaN</c> if the target is unreachable at that angle
        /// (e.g. pitched below the height difference, or aimed flat at an elevated hole).
        /// </summary>
        public static float CalculateRequiredSpeed(Vector3 from, Vector3 to, float pitchDegrees)
        {
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float d  = Mathf.Sqrt(dx * dx + dz * dz); // horizontal distance
            float dh = to.y - from.y;                  // height difference (+up)
            float g  = -Physics.gravity.y;              // 9.81

            if (d < 0.5f) return 0f; // right on top of the hole

            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosP  = Mathf.Cos(pitch);
            float tanP  = Mathf.Tan(pitch);

            // From projectile equation: dh = d*tanP - g*d²/(2*v²*cosP²)
            // Solving for v²:  v² = g*d² / (2*cosP²*(d*tanP - dh))
            float denom = 2f * cosP * cosP * (d * tanP - dh);
            if (denom <= 0f) return float.NaN; // pitch too low to clear height

            float vSq = g * d * d / denom;
            if (vSq < 0f) return float.NaN;
            return Mathf.Sqrt(vSq);
        }

        /// <summary>
        /// Horizontal distance from the landing spot to the hole.
        /// Useful for "how far off am I?" display.
        /// </summary>
        public static float LandingError(List<Vector3> path, Vector3 holePos)
        {
            if (path.Count == 0) return float.MaxValue;
            var land = path[path.Count - 1];
            float dx = land.x - holePos.x;
            float dz = land.z - holePos.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
