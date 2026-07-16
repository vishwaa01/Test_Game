using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class PlayerPathRecorder : MonoBehaviour
{
    [Header("Recording")]
    [Tooltip("Time between samples in seconds")]
    public float sampleInterval = 0.12f;
    [Tooltip("Maximum number of recorded points to keep (older points are dropped)")]
    public int maxPoints = 200; // at 0.12s -> ~24s of path
    [Tooltip("Minimum distance the car must move before a new point is recorded, even if the timer elapsed. Without this, an idle car keeps stuffing duplicate points into the list, which eventually evicts real path history once maxPoints is reached.")]
    public float minMoveDistance = 0.15f;

    [Header("Road Snap")]
    [Tooltip("If true, points will try to snap to nearest collider with tag roadTag")]
    public bool snapToRoad = true;
    public string roadTag = "Road";
    [Tooltip("Layers considered when snapping to the road. Set this to your Road layer ONLY - otherwise the downward raycast can hit the car's own collider before it ever reaches the road beneath it.")]
    public LayerMask roadLayerMask = ~0;
    [Tooltip("Max distance to search for road collider when snapping")]
    public float roadSearchRadius = 6f;

    // Public read-only access to the path (most recent point at end)
    public List<Vector3> Path { get; private set; } = new List<Vector3>();

    // Velocity estimate derived from the last two recorded points, using the
    // REAL elapsed time between them (not an assumed fixed sampleInterval,
    // since minMoveDistance means points aren't always evenly spaced) - and
    // reporting zero once it's gone stale, i.e. the player has actually
    // stopped. Without the staleness check, a stopped player's velocity
    // stays frozen at whatever it was the instant before stopping, and
    // anything doing predictive-intercept off of it (like CopCarAI) ends up
    // chasing a phantom point out ahead of where the player used to be
    // heading, forever, instead of driving straight at the parked car.
    public Vector3 EstimatedVelocity
    {
        get
        {
            if (Path.Count < 2) return Vector3.zero;

            float dt = lastPointTime - previousPointTime;
            if (dt <= 0.0001f) return Vector3.zero;

            if (Time.time - lastPointTime > sampleInterval * 2f) return Vector3.zero;

            Vector3 a = Path[Path.Count - 2];
            Vector3 b = Path[Path.Count - 1];
            return (b - a) / dt;
        }
    }

    float sampleTimer = 0f;
    float lastPointTime = -1f;
    float previousPointTime = -1f;

    void Start()
    {
        // seed initial point
        AddPoint(transform.position);
    }

    void Update()
    {
        sampleTimer += Time.deltaTime;
        if (sampleTimer >= sampleInterval)
        {
            sampleTimer = 0f;

            // Only actually record if the car moved - otherwise an idle car
            // would flood the list with duplicate points and, once maxPoints
            // is hit, push out the real path history that led to this spot.
            if (Path.Count == 0 || Vector3.Distance(transform.position, Path[Path.Count - 1]) >= minMoveDistance)
                AddPoint(transform.position);
        }
    }

    void AddPoint(Vector3 worldPos)
    {
        Vector3 p = worldPos;
        if (snapToRoad)
        {
            // try raycast down first, restricted to the road layer so it can't
            // hit this car's own collider on the way down
            RaycastHit hit;
            Vector3 above = p + Vector3.up * 2f;
            bool gotRoadHit = Physics.Raycast(above, Vector3.down, out hit, 4f, roadLayerMask)
                               && hit.collider != null
                               && hit.collider.gameObject.CompareTag(roadTag);

            if (gotRoadHit)
            {
                p = hit.point + Vector3.up * 0.02f;
            }
            else
            {
                // fall back to a nearby search whenever the raycast didn't land
                // on a valid road collider (missed entirely, or hit something else)
                Collider[] cols = Physics.OverlapSphere(p, roadSearchRadius, roadLayerMask);
                float best = Mathf.Infinity;
                Vector3 bestPoint = p;
                foreach (var c in cols)
                {
                    if (c == null) continue;
                    if (!c.gameObject.CompareTag(roadTag)) continue;
                    Vector3 cp = c.ClosestPoint(p);
                    float d = (cp - p).sqrMagnitude;
                    if (d < best)
                    {
                        best = d; bestPoint = cp;
                    }
                }
                if (best < Mathf.Infinity)
                {
                    p = bestPoint + Vector3.up * 0.02f;
                }
            }
        }

        Path.Add(p);
        if (Path.Count > maxPoints)
            Path.RemoveAt(0);

        previousPointTime = lastPointTime;
        lastPointTime = Time.time;
    }

    // Utility: returns a copy of current path (safe to iterate)
    public Vector3[] GetPathSnapshot()
    {
        return Path.ToArray();
    }
}