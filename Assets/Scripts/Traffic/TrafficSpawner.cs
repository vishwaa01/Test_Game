using System.Collections.Generic;
using UnityEngine;

// ==================================================================
// TRAFFIC SPAWNER - pooled spawn/despawn around the player
// ------------------------------------------------------------------
// Keeps `targetCarCount` traffic cars alive in a ring around the
// player: spawns them offscreen (between min and max spawn radius),
// despawns them once they fall far enough behind, and recycles the
// same instances forever - zero Instantiate/Destroy churn during the
// chase, which matters a lot on mobile/web.
//
// Setup: empty GameObject with this component; assign the player,
// one or more TrafficCar prefabs (different car models all work),
// and make sure your scene has TrafficWaypoints - they're found
// automatically at Start.
// ==================================================================

public class TrafficSpawner : MonoBehaviour
{
    [Header("References")]
    public Transform player;
    [Tooltip("One or more TrafficCar prefabs - variety is picked at random per spawn.")]
    public TrafficCar[] carPrefabs;

    [Header("Counts & Distances")]
    public int targetCarCount = 10;
    [Tooltip("Layers that count as 'something's already here' when picking a spawn point - your traffic/player/cop vehicle layers. Do NOT include the road/ground layer, or every spawn point will register as blocked by the road surface underneath it.")]
    public LayerMask vehicleMask;
    [Tooltip("Never spawn closer to the player than this (keep it just outside camera view so cars never pop in on screen).")]
    public float spawnRadiusMin = 35f;
    public float spawnRadiusMax = 65f;
    [Tooltip("Cars farther from the player than this get recycled.")]
    public float despawnRadius = 80f;
    [Tooltip("Minimum clear space around a spawn point (no spawning inside other vehicles).")]
    public float spawnClearance = 5f;
    [Tooltip("Wrecked cars get recycled this long after being smashed, once outside the min spawn radius (so the wreck doesn't vanish in front of the player).")]
    public float wreckedLifetime = 10f;

    [Header("Timing")]
    public float spawnInterval = 0.4f;

    TrafficWaypoint[] allWaypoints;
    readonly List<TrafficCar> active = new List<TrafficCar>();
    readonly Queue<TrafficCar> pool = new Queue<TrafficCar>();
    readonly Dictionary<TrafficCar, float> wreckTime = new Dictionary<TrafficCar, float>();
    float spawnTimer;

    void Start()
    {
        allWaypoints = FindObjectsByType<TrafficWaypoint>(FindObjectsSortMode.None);

        if (allWaypoints.Length == 0)
            Debug.LogWarning($"{name}: TrafficSpawner found no TrafficWaypoints in the scene - no traffic will spawn.", this);
        if (player == null)
            Debug.LogWarning($"{name}: TrafficSpawner has no player assigned.", this);
        if (carPrefabs == null || carPrefabs.Length == 0)
            Debug.LogWarning($"{name}: TrafficSpawner has no car prefabs assigned.", this);
        else
        {
            int nullSlots = 0;
            foreach (var p in carPrefabs) if (p == null) nullSlots++;
            if (nullSlots == carPrefabs.Length)
                Debug.LogWarning($"{name}: carPrefabs has {carPrefabs.Length} slot(s) but every one is empty (None) - nothing can be instantiated.", this);
        }

        int unlinked = 0;
        foreach (var wp in allWaypoints)
            if (wp == null || wp.next == null || wp.next.Count == 0) unlinked++;
        if (allWaypoints.Length > 0 && unlinked == allWaypoints.Length)
            Debug.LogWarning($"{name}: all {allWaypoints.Length} waypoints have an empty 'next' list - none of them lead anywhere, so no spawn point will ever qualify. Did you run TrafficRoute's \"Auto-Link Children In Order\" or \"Generate Waypoints From Anchors\"?", this);

        // This is the single most common setup mistake, especially on a small
        // test rig: if the spawn ring doesn't overlap where the waypoints
        // actually are, TrySpawnOne will reject every candidate, forever,
        // with no other symptom than "nothing ever spawns."
        if (player != null && allWaypoints.Length > 0)
        {
            float minDist = float.MaxValue, maxDist = 0f;
            foreach (var wp in allWaypoints)
            {
                if (wp == null) continue;
                float d = Vector3.Distance(wp.transform.position, player.position);
                minDist = Mathf.Min(minDist, d);
                maxDist = Mathf.Max(maxDist, d);
            }

            if (maxDist < spawnRadiusMin)
                Debug.LogWarning($"{name}: every waypoint is within {maxDist:F1}m of the player, but spawnRadiusMin is {spawnRadiusMin}m - the whole road network is INSIDE the spawn ring, so nothing will ever qualify. Lower spawnRadiusMin (and probably spawnRadiusMax) to fit this scene - common on a small test rig.", this);
            else if (minDist > spawnRadiusMax)
                Debug.LogWarning($"{name}: every waypoint is farther than {minDist:F1}m from the player, but spawnRadiusMax is {spawnRadiusMax}m - the whole road network is OUTSIDE the spawn ring. Raise spawnRadiusMax or move the player closer to the waypoints.", this);
        }
    }

    void Update()
    {
        if (player == null || allWaypoints == null || allWaypoints.Length == 0) return;

        RecycleFarAndWrecked();

        spawnTimer -= Time.deltaTime;
        if (spawnTimer <= 0f && active.Count < targetCarCount)
        {
            spawnTimer = spawnInterval;
            TrySpawnOne();
        }
    }

    void RecycleFarAndWrecked()
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            TrafficCar car = active[i];
            if (car == null) { active.RemoveAt(i); continue; }

            float dist = Vector3.Distance(car.transform.position, player.position);

            bool tooFar = dist > despawnRadius;

            bool wreckExpired = false;
            if (car.IsWrecked)
            {
                if (!wreckTime.ContainsKey(car)) wreckTime[car] = Time.time;
                // only reclaim a wreck once it's aged AND is out of the player's
                // immediate view distance - wrecks vanishing on-screen looks awful
                wreckExpired = Time.time - wreckTime[car] > wreckedLifetime && dist > spawnRadiusMin;
            }

            if (tooFar || wreckExpired)
            {
                active.RemoveAt(i);
                wreckTime.Remove(car);
                car.gameObject.SetActive(false);
                pool.Enqueue(car);
            }
        }
    }

    int consecutiveFailures = 0;
    float lastDiagnosticTime = -999f;

    void TrySpawnOne()
    {
        int rejectedUnlinked = 0, rejectedRange = 0, rejectedBlocked = 0;

        // find a waypoint in the spawn ring with clear space around it
        for (int attempt = 0; attempt < 12; attempt++)
        {
            TrafficWaypoint wp = allWaypoints[Random.Range(0, allWaypoints.Length)];
            if (wp == null || wp.next == null || wp.next.Count == 0) { rejectedUnlinked++; continue; }

            float dist = Vector3.Distance(wp.transform.position, player.position);
            if (dist < spawnRadiusMin || dist > spawnRadiusMax) { rejectedRange++; continue; }

            if (Physics.CheckSphere(wp.transform.position + Vector3.up * 0.5f, spawnClearance,
                                    vehicleMask, QueryTriggerInteraction.Ignore))
            {
                // something's already there (a parked wreck, another car, the player) - try elsewhere
                rejectedBlocked++;
                continue;
            }

            TrafficCar car = GetFromPoolOrCreate();
            if (car == null)
            {
                Debug.LogWarning($"{name}: found a valid spawn point but couldn't get a car instance - check carPrefabs is assigned.", this);
                return;
            }

            car.transform.position = wp.transform.position + Vector3.up * 0.3f;
            car.gameObject.SetActive(true);
            car.Init(wp.PickNext());
            active.Add(car);
            consecutiveFailures = 0;
            return;
        }

        consecutiveFailures++;
        if (consecutiveFailures >= 15 && Time.time - lastDiagnosticTime > 5f)
        {
            lastDiagnosticTime = Time.time;
            Debug.LogWarning(
                $"{name}: no valid spawn point in {consecutiveFailures} tries across {allWaypoints.Length} waypoints " +
                $"(this attempt: {rejectedUnlinked} unlinked, {rejectedRange} outside the {spawnRadiusMin}-{spawnRadiusMax}m ring, {rejectedBlocked} blocked by vehicleMask). " +
                "If this keeps repeating, check whichever count is highest.", this);
        }
    }

    TrafficCar GetFromPoolOrCreate()
    {
        while (pool.Count > 0)
        {
            TrafficCar c = pool.Dequeue();
            if (c != null) return c;
        }

        if (carPrefabs == null || carPrefabs.Length == 0) return null;
        TrafficCar prefab = carPrefabs[Random.Range(0, carPrefabs.Length)];
        if (prefab == null) return null;

        TrafficCar made = Instantiate(prefab);
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