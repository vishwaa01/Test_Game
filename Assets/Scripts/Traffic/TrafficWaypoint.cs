using System.Collections.Generic;
using UnityEngine;

// ==================================================================
// TRAFFIC WAYPOINT - one lane node in the traffic network
// ------------------------------------------------------------------
// Chain these along each LANE (not the road center - one chain per
// driving direction, offset to its side of the road). At forks and
// intersections, give a waypoint MULTIPLE next entries - cars pick
// one at random, which is what makes traffic spread through the city
// instead of all following one loop.
//
// For a signalled intersection: on the LAST waypoint before the
// intersection box (the "stop line"), set `intersection` and give
// each incoming road a different `approachIndex` (0,1,2...). Cars
// heading to that waypoint will queue there until their approach is
// green.
//
// Tip: use a TrafficRoute parent + its context menu to auto-link
// children in order instead of wiring `next` by hand.
// ==================================================================

public class TrafficWaypoint : MonoBehaviour
{
    [Tooltip("Where a car can go from here. Multiple entries = cars pick one at random (branching).")]
    public List<TrafficWaypoint> next = new List<TrafficWaypoint>();

    [Tooltip("Speed limit for cars heading TO this waypoint, in m/s. 0 = use the car's own cruise speed. Useful for slowing traffic into corners.")]
    public float speedLimit = 0f;

    [Header("Signalled Intersection (optional)")]
    [Tooltip("If set, this waypoint is a STOP LINE for that intersection - cars heading here wait until their approach is green.")]
    public TrafficIntersection intersection;
    [Tooltip("Which approach of the intersection this stop line belongs to (0,1,2...). All lanes coming from the same road share one index.")]
    public int approachIndex = 0;

    public TrafficWaypoint PickNext()
    {
        if (next == null || next.Count == 0) return null;
        // Prune destroyed entries defensively rather than throwing mid-chase.
        for (int guard = 0; guard < 4; guard++)
        {
            TrafficWaypoint w = next[Random.Range(0, next.Count)];
            if (w != null) return w;
        }
        return null;
    }

    void OnDrawGizmos()
    {
        bool isStopLine = intersection != null;
        Gizmos.color = isStopLine ? new Color(1f, 0.3f, 0.2f) : Color.cyan;
        Gizmos.DrawSphere(transform.position, 0.3f);

        if (next == null) return;
        Gizmos.color = new Color(0f, 1f, 1f, 0.7f);
        foreach (var n in next)
        {
            if (n == null) continue;
            Vector3 a = transform.position;
            Vector3 b = n.transform.position;
            Gizmos.DrawLine(a, b);

            // small arrowhead so lane DIRECTION is readable at a glance
            Vector3 dir = (b - a).normalized;
            Vector3 mid = Vector3.Lerp(a, b, 0.6f);
            Vector3 side = Vector3.Cross(Vector3.up, dir) * 0.35f;
            Gizmos.DrawLine(mid, mid - dir * 0.7f + side);
            Gizmos.DrawLine(mid, mid - dir * 0.7f - side);
        }
    }
}
