using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ==============================================
// COP ROAD-ONLY CHASE AI
// - Uses NavMesh baked ONLY on roads
// - When far: finds clean curved road path via NavMesh
// - When near: follows player's driven road path
// - Never cuts through buildings
// ==============================================

[RequireComponent(typeof(Rigidbody))]
public class CopPathFollower : MonoBehaviour
{
    [Header("References")]
    public PlayerPathRecorder playerRecorder;   // Player path source

    [Header("Movement")]
    public float followSpeed = 26f;              // Forward speed (m/s)
    public float turnSpeed = 10f;                // Steering responsiveness
    public float pointReachThreshold = 1.0f;     // Distance to advance path

    [Header("Distances")]
    public float farDistanceThreshold = 28f;     // Far = NavMesh mode
    public float followDistance = 8f;            // Meters behind player path

    [Header("NavMesh Road Settings")]
    public float navPathRecalcInterval = 0.35f;  // Repath frequency
    public float sampleSpacing = 0.75f;          // Path smoothness

    [Header("Road Snapping (Optional)")]
    public bool snapNavPointsToRoad = true;
    public string roadTag = "Road";
    public float roadSnapMaxDist = 4f;

    [Header("Obstacle Avoidance")]
    public LayerMask obstacleMask;
    public float avoidanceRayLength = 4f;
    public float avoidanceStrength = 2.5f;

    Rigidbody rb;
    NavMeshPath navPath;

    List<Vector3> navPoints = new List<Vector3>();
    int navIndex;
    float navTimer;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        navPath = new NavMeshPath();
    }

    void FixedUpdate()
    {
        if (playerRecorder == null || playerRecorder.Path.Count < 2)
            return;

        Vector3 playerPos = playerRecorder.Path[playerRecorder.Path.Count - 1];
        float distToPlayer = Vector3.Distance(transform.position, playerPos);

        // ==========================================
        // FAR → NAVMESH ROAD CHASE
        // ==========================================
        if (distToPlayer > farDistanceThreshold)
        {
            navTimer += Time.fixedDeltaTime;
            if (navTimer >= navPathRecalcInterval || navPoints.Count == 0)
            {
                navTimer = 0f;
                BuildNavRoadPath(playerPos);
            }
            FollowNavPath();
        }
        // ==========================================
        // NEAR → FOLLOW PLAYER'S REAL ROAD TRAIL
        // ==========================================
        else
        {
            FollowRecordedPath();
        }
    }

    // =====================================================
    // FOLLOW PLAYER'S RECORDED ROAD PATH (NO SHORTCUTS)
    // =====================================================
    void FollowRecordedPath()
    {
        var path = playerRecorder.Path;
        if (path.Count < 2) return;

        float acc = 0f;
        for (int i = path.Count - 1; i > 0; i--)
        {
            acc += Vector3.Distance(path[i], path[i - 1]);
            if (acc >= followDistance)
            {
                MoveTowards(path[i]);
                return;
            }
        }

        MoveTowards(path[0]);
    }

    // =====================================================
    // BUILD NAVMESH PATH STRICTLY ON ROAD
    // =====================================================
    void BuildNavRoadPath(Vector3 target)
    {
        navPoints.Clear();
        navIndex = 0;

        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit startHit, 5f, NavMesh.AllAreas)) return;
        if (!NavMesh.SamplePosition(target, out NavMeshHit endHit, 5f, NavMesh.AllAreas)) return;

        if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, navPath)) return;
        if (navPath.status != NavMeshPathStatus.PathComplete) return;

        for (int i = 0; i < navPath.corners.Length - 1; i++)
        {
            Vector3 a = navPath.corners[i];
            Vector3 b = navPath.corners[i + 1];
            float dist = Vector3.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / sampleSpacing));

            for (int s = 0; s <= steps; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, s / (float)steps);
                if (snapNavPointsToRoad)
                    p = SnapToRoad(p);
                navPoints.Add(p);
            }
        }
    }

    // =====================================================
    // FOLLOW GENERATED NAVMESH ROAD PATH
    // =====================================================
    void FollowNavPath()
    {
        if (navPoints.Count == 0 || navIndex >= navPoints.Count)
            return;

        Vector3 target = navPoints[navIndex];
        float d = Vector3.Distance(transform.position, target);

        if (d < pointReachThreshold)
        {
            navIndex++;
            return;
        }

        MoveTowards(target);
    }

    // =====================================================
    // CORE MOVEMENT + SIMPLE SIDE AVOIDANCE
    // =====================================================
    void MoveTowards(Vector3 target)
    {
        Vector3 dir = target - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;
        dir.Normalize();

        // obstacle probe
        if (Physics.Raycast(transform.position + Vector3.up * 0.5f,
            transform.forward, avoidanceRayLength, obstacleMask))
        {
            dir += transform.right * avoidanceStrength;
        }

        Quaternion rot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.Slerp(transform.rotation, rot, turnSpeed * Time.fixedDeltaTime);

        rb.MovePosition(rb.position + transform.forward * followSpeed * Time.fixedDeltaTime);
    }

    // =====================================================
    // SNAP NAV POINT TO ROAD COLLIDER
    // =====================================================
    Vector3 SnapToRoad(Vector3 p)
    {
        Collider[] cols = Physics.OverlapSphere(p, roadSnapMaxDist);
        float best = Mathf.Infinity;
        Vector3 bestPoint = p;

        foreach (var c in cols)
        {
            if (!c || !c.CompareTag(roadTag)) continue;
            Vector3 cp = c.ClosestPoint(p);
            float d = (cp - p).sqrMagnitude;
            if (d < best)
            {
                best = d;
                bestPoint = cp;
            }
        }
        return bestPoint + Vector3.up * 0.05f;
    }

    // =====================================================
    // GIZMOS (DEBUG VISUALIZATION)
    // =====================================================
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (playerRecorder != null && playerRecorder.Path != null)
        {
            Gizmos.color = Color.cyan;
            for (int i = 1; i < playerRecorder.Path.Count; i++)
                Gizmos.DrawLine(playerRecorder.Path[i - 1], playerRecorder.Path[i]);
        }

        Gizmos.color = Color.green;
        for (int i = 1; i < navPoints.Count; i++)
            Gizmos.DrawLine(navPoints[i - 1], navPoints[i]);

        if (navPoints.Count > navIndex)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(navPoints[navIndex], 0.35f);
        }
    }
}
