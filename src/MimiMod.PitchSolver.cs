using UnityEngine;

public partial class MimiMod
{
    // ── Analytic projectile solvers (D3 graft) ─────────────────────────────────
    //
    // Closed-form projectile math ported from our previous BallPredictor.
    // Mimi already has an iterative drag-aware solver (TrySolveRequiredSpeedWithDrag)
    // plus a closed-form fallback inside CalculateRequiredPowerForPitch, so these
    // helpers are *additive* — available for future features that need:
    //
    //   • Low-arc vs high-arc (lobbed) choice — Mimi's fallback always returns
    //     the lower-angle solution; our CalculatePitchFromSpeed can return either.
    //   • Inverse problem: "given a fixed launch speed, what pitch reaches the
    //     target?" (Mimi solves the forward problem: given pitch → speed).
    //   • Fast physical-minimum speed without binary search (analytic sqrt path).
    //
    // No drag, no wind — closed-form only. For wind-accurate prediction use
    // Mimi's forward-sim in MimiMod.Trajectory.cs (which now includes wind via D2).

    /// <summary>
    /// Given a fixed launch speed and a target position, analytically compute the
    /// launch pitch (degrees) that reaches the target. Returns float.NaN if the
    /// target is unreachable at that speed. Pass highArc=true for the lobbed
    /// solution (larger angle), false for the flat/direct solution.
    /// </summary>
    public static float CalculatePitchFromSpeed(Vector3 from, Vector3 to, float speed, bool highArc = false)
    {
        float dx = to.x - from.x;
        float dz = to.z - from.z;
        float d = Mathf.Sqrt(dx * dx + dz * dz); // horizontal distance
        float dh = to.y - from.y;                // height difference (+up)
        float g = -Physics.gravity.y;            // 9.81

        if (d < 0.5f)
        {
            return highArc ? 80f : 10f;
        }

        float v2 = speed * speed;
        float disc = v2 * v2 - g * (g * d * d + 2f * dh * v2);

        if (disc < 0f)
        {
            return float.NaN;
        }

        float sqrtD = Mathf.Sqrt(disc);
        float tanTheta = highArc
            ? (v2 + sqrtD) / (g * d)
            : (v2 - sqrtD) / (g * d);

        return Mathf.Atan(tanTheta) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Given a fixed launch pitch and target position, analytically compute the
    /// launch speed (m/s) required to land at the target. Returns float.NaN if
    /// the pitch is too low to clear the height difference.
    /// </summary>
    public static float CalculateSpeedFromPitch(Vector3 from, Vector3 to, float pitchDegrees)
    {
        float dx = to.x - from.x;
        float dz = to.z - from.z;
        float d = Mathf.Sqrt(dx * dx + dz * dz);
        float dh = to.y - from.y;
        float g = -Physics.gravity.y;

        if (d < 0.5f)
        {
            return 0f;
        }

        float pitch = pitchDegrees * Mathf.Deg2Rad;
        float cosP = Mathf.Cos(pitch);
        float tanP = Mathf.Tan(pitch);

        float denom = 2f * cosP * cosP * (d * tanP - dh);
        if (denom <= 0f)
        {
            return float.NaN;
        }

        float vSq = g * d * d / denom;
        if (vSq < 0f)
        {
            return float.NaN;
        }

        return Mathf.Sqrt(vSq);
    }

    /// <summary>
    /// Physical minimum launch speed (m/s) that can reach the target at *any*
    /// pitch up to maxPitch. For flat terrain this is sqrt(g*d) at 45°. Useful
    /// as an absolute lower bound for "fire at minimum power" features.
    ///
    /// Returns float.NaN if the target is unreachable even at knownMaxSpeed.
    /// </summary>
    public static float FindMinimumReachSpeedAnalytic(Vector3 from, Vector3 to, float maxPitch, float knownMaxSpeed, bool highArc)
    {
        float testPitch = CalculatePitchFromSpeed(from, to, knownMaxSpeed, highArc);
        if (float.IsNaN(testPitch) || testPitch > maxPitch + 5f)
        {
            return float.NaN;
        }

        float lo = 0f;
        float hi = knownMaxSpeed;
        for (int i = 0; i < 18; i++) // 18 iterations → ~4 decimal places
        {
            float mid = (lo + hi) * 0.5f;
            float pitch = CalculatePitchFromSpeed(from, to, mid, highArc);
            if (!float.IsNaN(pitch) && pitch <= maxPitch + 5f)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }
        return hi;
    }

    /// <summary>
    /// Yaw angle (degrees) from one point toward another on the horizontal plane.
    /// 0 = north (+Z), increasing clockwise.
    /// </summary>
    public static float CalculateYawTo(Vector3 from, Vector3 to)
    {
        float dx = to.x - from.x;
        float dz = to.z - from.z;
        return Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
    }
}
