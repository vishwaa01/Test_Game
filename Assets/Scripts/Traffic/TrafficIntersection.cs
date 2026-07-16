using UnityEngine;

// ==================================================================
// TRAFFIC INTERSECTION - "signals-ish" phase controller
// ------------------------------------------------------------------
// Gives each approach (incoming road) a green window in a fixed
// timed cycle, with an all-red clearance buffer between phases so
// crossing streams never overlap. Completely stateless - the current
// phase is computed from Time.time, so it costs nothing per car, per
// frame, and every car always agrees on the light without any
// registration or messaging.
//
// Setup: place on an empty GameObject at the intersection center.
// On each road's stop-line TrafficWaypoint, set `intersection` to
// this and give each road its own approachIndex (0..approachCount-1).
// ==================================================================

public class TrafficIntersection : MonoBehaviour
{
    [Tooltip("How many incoming roads take turns (a standard crossroads with opposing pairs sharing a phase = 2; giving all four roads their own turn = 4).")]
    public int approachCount = 2;
    [Tooltip("Seconds of green each approach gets per cycle.")]
    public float greenDuration = 6f;
    [Tooltip("All-red clearance seconds between phases, so cars already committed can clear the box before the cross-street goes.")]
    public float allRedBuffer = 1.5f;
    [Tooltip("Random-feel offset so every intersection in the city isn't synchronized to the same clock. Set differently per intersection, or leave 0 and vary manually.")]
    public float phaseOffset = 0f;

    public bool CanGo(int approach)
    {
        if (approachCount <= 1) return true;

        float phaseLength = greenDuration + allRedBuffer;
        float t = (Time.time + phaseOffset) % (phaseLength * approachCount);
        int currentPhase = (int)(t / phaseLength);
        bool inGreenWindow = (t % phaseLength) < greenDuration;

        return inGreenWindow && ((approach % approachCount) == currentPhase);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.9f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * 0.5f, new Vector3(3f, 1f, 3f));

        // In Play mode, show the live phase as a ring of small spheres - the
        // green one is the approach index currently allowed to go.
        if (!Application.isPlaying || approachCount <= 1) return;
        for (int i = 0; i < approachCount; i++)
        {
            float ang = (360f / approachCount) * i * Mathf.Deg2Rad;
            Vector3 p = transform.position + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 2.2f + Vector3.up * 1.2f;
            Gizmos.color = CanGo(i) ? Color.green : Color.red;
            Gizmos.DrawSphere(p, 0.3f);
        }
    }
}
