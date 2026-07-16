using System.Collections.Generic;
using UnityEngine;

// ==================================================================
// COP SPAWNER - dispatch driven by WantedSystem
// ------------------------------------------------------------------
// Same pooled spawn/despawn pattern as TrafficSpawner: keeps however
// many cops the current star level calls for alive in a ring around
// the player, recycling instances instead of Instantiate/Destroy
// churn. Reuses the existing TrafficWaypoint network purely as a
// source of known-good on-road points to spawn at - cops don't lane-
// follow them, CopCarAI does its own NavMesh routing.
//
// Setup: empty GameObject with this component; assign player,
// playerRecorder, and one or more CopCarAI prefabs. A WantedSystem
// must exist in the scene (Stars reads as 0 and nothing spawns if
// there isn't one - a console warning fires once to make that obvious).
// ==================================================================

public class CopSpawner : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    [Tooltip("Assigned into every spawned cop's CopCarAI.playerRecorder.")]
    public PlayerPathRecorder playerRecorder;
    [Tooltip("One or more cop prefabs (their root GameObject needs CopCarController + CopCarAI) - variety picked at random per spawn.")]
    public CopCarAI[] copPrefabs;

    [Header("Dispatch (by star level, index 0 = 1 star)")]
    [Tooltip("How many cops should be active at each star level.")]
    public int[] copsPerStar = { 1, 2, 3, 4, 6 };
    [Tooltip("Chance a newly spawned cop is Intercept instead of Pursuit, scaled by star level - more roadblock behavior as heat rises.")]
    public AnimationCurve interceptChanceByStar = AnimationCurve.Linear(1f, 0.1f, 5f, 0.5f);
    public float spawnInterval = 1.2f;

    [Header("Spawn Ring")]
    public float spawnRadiusMin = 35f;
    public float spawnRadiusMax = 65f;
    public float despawnRadius = 100f;
    [Tooltip("Layers that count as 'something's already here' when picking a spawn point - vehicle layers only, NOT road/ground.")]
    public LayerMask vehicleMask;
    public float spawnClearance = 4f;

    TrafficWaypoint[] roadPoints;
    readonly List<CopCarAI> active = new List<CopCarAI>();
    readonly Queue<CopCarAI> pool = new Queue<CopCarAI>();
    float spawnTimer;

    void Start()
    {
        roadPoints = FindObjectsByType<TrafficWaypoint>(FindObjectsSortMode.None);

        if (roadPoints.Length == 0)
            Debug.LogWarning($"{name}: no TrafficWaypoints found - CopSpawner reuses that network for spawn points, so it needs at least one in the scene.", this);
        if (player == null)
            Debug.LogWarning($"{name}: no player assigned.", this);
        if (copPrefabs == null || copPrefabs.Length == 0)
            Debug.LogWarning($"{name}: no cop prefabs assigned.", this);
        if (WantedSystem.Instance == null)
            Debug.LogWarning($"{name}: no WantedSystem found in the scene - stars will always read 0, so no cops will ever be dispatched.", this);
    }

    void Update()
    {
        if (player == null || roadPoints == null || roadPoints.Length == 0) return;
        if (copPrefabs == null || copPrefabs.Length == 0) return;

        int stars = WantedSystem.Instance != null ? WantedSystem.Instance.Stars : 0;
        int target = TargetCountForStars(stars);

        RecycleFarOrOffDuty(target, stars);

        spawnTimer -= Time.deltaTime;
        if (stars > 0 && active.Count < target && spawnTimer <= 0f)
        {
            spawnTimer = spawnInterval;
            TrySpawnOne(stars);
        }
    }

    int TargetCountForStars(int stars)
    {
        if (stars <= 0 || copsPerStar == null || copsPerStar.Length == 0) return 0;
        return copsPerStar[Mathf.Clamp(stars - 1, 0, copsPerStar.Length - 1)];
    }

    void RecycleFarOrOffDuty(int target, int stars)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            CopCarAI cop = active[i];
            if (cop == null) { active.RemoveAt(i); continue; }

            float dist = Vector3.Distance(cop.transform.position, player.position);
            bool tooFar = dist > despawnRadius;
            // Wanted cleared entirely: let cops peel off once they're not
            // right on top of the player, rather than popping out mid-screen.
            bool offDuty = stars == 0 && dist > spawnRadiusMin;
            // Star level dropped but some cops should remain: only trim the
            // excess once they're far enough to not vanish visibly.
            bool excess = active.Count > target && dist > spawnRadiusMin;

            if (tooFar || offDuty || excess)
            {
                active.RemoveAt(i);
                cop.gameObject.SetActive(false);
                pool.Enqueue(cop);
            }
        }
    }

    void TrySpawnOne(int stars)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            TrafficWaypoint wp = roadPoints[Random.Range(0, roadPoints.Length)];
            if (wp == null) continue;

            float dist = Vector3.Distance(wp.transform.position, player.position);
            if (dist < spawnRadiusMin || dist > spawnRadiusMax) continue;

            if (Physics.CheckSphere(wp.transform.position + Vector3.up * 0.5f, spawnClearance,
                                    vehicleMask, QueryTriggerInteraction.Ignore))
                continue;

            CopCarAI cop = GetFromPoolOrCreate();
            if (cop == null) return;

            cop.transform.position = wp.transform.position + Vector3.up * 0.3f;
            Vector3 faceDir = (player.position - wp.transform.position);
            faceDir.y = 0f;
            cop.transform.rotation = faceDir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(faceDir.normalized) : Quaternion.identity;

            cop.gameObject.SetActive(true);
            cop.ResetForSpawn();

            cop.playerRecorder = playerRecorder;
            cop.playerTransformOverride = player;
            cop.role = Random.value < interceptChanceByStar.Evaluate(stars) ? CopCarAI.CopRole.Intercept : CopCarAI.CopRole.Pursuit;

            float difficultyT = copsPerStar.Length > 1 ? Mathf.InverseLerp(1, copsPerStar.Length, stars) : 1f;
            cop.ApplyDifficulty(difficultyT);

            active.Add(cop);
            return;
        }
    }

    CopCarAI GetFromPoolOrCreate()
    {
        while (pool.Count > 0)
        {
            CopCarAI c = pool.Dequeue();
            if (c != null) return c;
        }

        CopCarAI prefab = copPrefabs[Random.Range(0, copPrefabs.Length)];
        if (prefab == null) return null;

        CopCarAI made = Instantiate(prefab);
        made.gameObject.SetActive(false);
        return made;
    }

    void OnDrawGizmosSelected()
    {
        if (player == null) return;
        Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.8f);
        Gizmos.DrawWireSphere(player.position, spawnRadiusMin);
        Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
        Gizmos.DrawWireSphere(player.position, spawnRadiusMax);
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(player.position, despawnRadius);
    }
}