using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ==================================================================
// COP CAR AI - GTA-style pursuit brain
// ------------------------------------------------------------------
// Drives a CopCarController (its own dedicated physics - see that
// script), NOT the player's ArcadeCarController. This is intentional:
// a chase AI needs guaranteed, predictable handling and its own
// input surface (including an independent hard-brake channel) that a
// human driver's controller was never designed to expose.
//
// Setup:
//  - Cop prefab needs: CopCarController (+ WheelColliders/meshes
//    wired up) + this script.
//  - Assign playerRecorder (or playerTransformOverride for a simpler
//    single-player setup with a direct reference).
//  - Bake a NavMesh that only covers your roads.
//  - obstacleMask = walls/props/scenery layers. Do NOT include the
//    player's vehicle layer in it, or the cop will "avoid" the very
//    thing it's chasing instead of ramming it.
// ==================================================================

[RequireComponent(typeof(CopCarController))]
public class CopCarAI : MonoBehaviour
{
    public enum CopRole { Pursuit, Intercept }

    [Header("Target")]
    public PlayerPathRecorder playerRecorder;
    [Tooltip("Optional. If set, used directly for exact player position/velocity instead of the recorded path. Leave empty for a networked setup where only path data is replicated.")]
    public Transform playerTransformOverride;

    [Header("Role & Personality")]
    [Tooltip("Pursuit: tails the player and closes in for a ram/PIT. Intercept: predicts further ahead and tries to cut the player off - good for roadblock units.")]
    public CopRole role = CopRole.Pursuit;
    [Range(0f, 1f)] public float difficulty = 0.5f;
    [Tooltip("Adds small per-cop variation in speed/aggression so a squad of cops doesn't all drive identically.")]
    public bool randomizePersonality = true;

    [Header("Speed")]
    public float baseTopSpeed = 26f;
    public float throttleGain = 0.6f;
    public float cornerSlowdownStrength = 1.2f;

    [Header("Distances")]
    public float farDistanceThreshold = 28f;
    public float modeBlendRange = 8f;
    public float followDistance = 8f;

    [Header("Prediction")]
    public float predictionTime = 1.1f;
    public float maxPredictionDistance = 18f;

    [Header("Rubber Banding")]
    public float rubberBandStartDistance = 35f;
    [Range(0f, 1f)] public float rubberBandMaxBoost = 0.3f;

    [Header("NavMesh Road Settings")]
    public float navPathRecalcInterval = 0.35f;
    public float navRecalcMoveThreshold = 3f;
    public float sampleSpacing = 0.75f;

    [Header("Road Snapping (Optional)")]
    public bool snapNavPointsToRoad = true;
    public string roadTag = "Road";
    public float roadSnapMaxDist = 4f;

    [Header("Obstacle Avoidance")]
    [Tooltip("Walls/props/scenery only - NOT the player's vehicle layer.")]
    public LayerMask obstacleMask;
    [Tooltip("Roughly half the car's width - this is a SphereCast, not a thin ray, so it can't slip past a wall's edge mid-turn.")]
    public float sensorRadius = 1.1f;
    public float minLookAhead = 4f;
    [Tooltip("Extra lookahead added per m/s of current speed, so faster cops react from farther out.")]
    public float lookAheadPerSpeed = 0.4f;
    public float sideSensorAngle = 30f;
    [Tooltip("How much clearer one side must be than the other before switching which way to swerve. Without this, a building corner sitting roughly dead ahead can make left/right clearance flip which side looks better every single frame - that's what causes the car to oscillate/spin in place instead of committing to a direction.")]
    public float sideSwitchMargin = 0.75f;
    [Range(0f, 1f)] public float avoidanceSteerStrength = 1f;

    [Header("Ramming / PIT")]
    public bool allowPIT = true;
    [Tooltip("Cop must be within roughly this many degrees of directly BEHIND the player to set up a PIT - real PIT attempts start from behind, then swing into the rear quarter.")]
    [Range(0f, 90f)] public float pitApproachAngleMax = 50f;
    public float pitTriggerDistance = 6f;
    public float pitTriggerMinPlayerSpeed = 6f;
    public float pitDuration = 1.1f;
    [Tooltip("Chance per second of committing to a PIT once conditions are met, scaled by this cop's aggression.")]
    public float pitChancePerSecond = 0.5f;
    public float ramSpeedBoost = 1.3f;

    [Header("Stuck Recovery")]
    public float stuckSpeedThreshold = 1.0f;
    public float stuckTimeToTriggerReverse = 1.0f;
    public float reverseDuration = 0.8f;
    [Tooltip("An emergency stop that stays pinned against an unmoving obstacle escalates into reverse-recovery after this long, instead of sitting braked at the wall forever.")]
    public float emergencyStopEscalateTime = 0.7f;

    [Header("Cop Coordination (multi-cop)")]
    [Tooltip("Layer(s) the cop vehicles themselves are on. Lets the forward sensors see other cops so they brake/steer around each other instead of rear-ending into a pileup. Leave as Nothing if you haven't given cops their own layer - radius repulsion below still works without it.")]
    public LayerMask copVehicleMask;
    [Tooltip("Each cop steers away from other cops inside this radius around itself (works without any layer setup - cops find each other directly).")]
    public float copSeparationRadius = 6f;
    public float copSeparationStrength = 1.2f;
    [Tooltip("Spread cops onto different approach angles around the player (GTA box-in) instead of all funneling onto the same point.")]
    public bool enableSurround = true;
    [Tooltip("Surround slots kick in inside this range of the player.")]
    public float surroundEngageRange = 30f;
    [Tooltip("How far from the player each cop's surround slot sits during the approach.")]
    public float surroundDistance = 7f;

    [Header("K-Turn (target behind)")]
    [Tooltip("If the target is more than this many degrees off the nose while close, the cop reverses to swing around instead of orbiting forward at full lock forever.")]
    public float kTurnTriggerAngle = 110f;
    [Tooltip("Only K-turn within this range of the target - far away, the NavMesh route handles direction changes naturally.")]
    public float kTurnMaxDistance = 22f;
    [Tooltip("Give up on a K-turn after this long and try driving forward again (safety valve so it can't reverse forever into a corner).")]
    public float kTurnMaxDuration = 2.2f;

    [Header("Close Engagement (GTA-style)")]
    [Tooltip("Never drop below this speed while closing on a slow/stopped player - the cop should shove into contact like a GTA unit pinning you, not politely park nearby.")]
    public float minEngageSpeed = 7f;
    [Tooltip("How steeply approach speed scales down with distance to a slow/stopped player. Higher = charges in hotter.")]
    public float engageSpeedPerMeter = 1.4f;
    [Tooltip("Below this player speed, the cop switches from intercept-prediction to direct engagement behavior.")]
    public float slowPlayerSpeed = 3f;

    CopCarController car;
    NavMeshPath navPath;

    List<Vector3> navPoints = new List<Vector3>();
    int navIndex;
    float navTimer;
    Vector3 lastNavPlanTarget = new Vector3(float.PositiveInfinity, 0f, 0f);

    float personalitySpeedMul = 1f;
    float personalityAggroMul = 1f;

    float stuckTimer = 0f;
    float reverseTimer = 0f;
    float recoverySteerSign = 1f;

    bool isPitting = false;
    float pitTimer = 0f;
    float pitSide = 1f;
    Vector3 pitDir = Vector3.forward;

    float lastAvoidSide = 0f;

    bool kTurning = false;
    float kTurnTimer = 0f;

    float emergencyStopTimer = 0f;

    // Registry of all active cops - this is what powers the radius repulsion and
    // surround slots without needing any layer setup: cops find each other directly.
    static readonly List<CopCarAI> AllCops = new List<CopCarAI>();

    void OnEnable()  { if (!AllCops.Contains(this)) AllCops.Add(this); }
    void OnDisable() { AllCops.Remove(this); }

    // Distance from any point to the nearest active cop - used by WantedSystem
    // to decide "is the player currently clear of pursuit" without needing any
    // line-of-sight/vision modeling, which this AI's position-tracking design
    // doesn't naturally support anyway (it always knows where the player is).
    public static float DistanceToNearestCop(Vector3 point)
    {
        float best = float.MaxValue;
        for (int i = 0; i < AllCops.Count; i++)
        {
            if (AllCops[i] == null) continue;
            float d = Vector3.Distance(AllCops[i].transform.position, point);
            if (d < best) best = d;
        }
        return best;
    }

    public bool IsPitting => isPitting;

    void Awake()
    {
        car = GetComponent<CopCarController>();
        navPath = new NavMeshPath();

        if (randomizePersonality)
        {
            personalitySpeedMul = Random.Range(0.94f, 1.06f);
            personalityAggroMul = Random.Range(0.9f, 1.1f);
        }

#if UNITY_EDITOR
        if (obstacleMask.value == 0)
            Debug.LogWarning($"{name}: CopCarAI.obstacleMask is set to Nothing - obstacle avoidance is effectively disabled. Assign your wall/prop layer(s) in the Inspector.", this);
        if (playerRecorder == null && playerTransformOverride == null)
            Debug.LogWarning($"{name}: CopCarAI has no playerRecorder and no playerTransformOverride assigned - it has nothing to chase.", this);
#endif
    }

    // Call this from a spawner / difficulty-director script as the chase escalates.
    public void ApplyDifficulty(float d)
    {
        difficulty = Mathf.Clamp01(d);
    }

    // Call right after (re)positioning a POOLED cop, before assigning its new
    // role/targets. Clears every piece of transient AI state so a recycled
    // instance can't carry a mid-PIT-attempt, a mid-K-turn, or a stale NavMesh
    // route to its old location into a brand new spawn point - none of that
    // mattered for a single hand-placed cop, but it matters a lot once
    // instances get reused by CopSpawner.
    public void ResetForSpawn()
    {
        navPoints.Clear();
        navIndex = 0;
        navTimer = 0f;
        lastNavPlanTarget = new Vector3(float.PositiveInfinity, 0f, 0f);

        stuckTimer = 0f;
        reverseTimer = 0f;
        recoverySteerSign = 1f;
        emergencyStopTimer = 0f;

        isPitting = false;
        pitTimer = 0f;

        kTurning = false;
        kTurnTimer = 0f;

        lastAvoidSide = 0f;

        if (car != null) car.ResetForSpawn();
    }

    void FixedUpdate()
    {
        // --- Real collision recovery takes priority over everything else ---
        if (reverseTimer > 0f)
        {
            reverseTimer -= Time.fixedDeltaTime;
            car.SteerInput = -recoverySteerSign;
            car.ThrottleInput = -1f;
            car.BrakeInput = 0f;
            return;
        }

        if (!HasPlayerData(out Vector3 playerPos, out Vector3 playerVel))
        {
            car.SteerInput = 0f;
            car.ThrottleInput = 0f;
            car.BrakeInput = 0f;
            return;
        }

        float distToPlayer = Vector3.Distance(transform.position, playerPos);

        // pitTarget needs a default here - the compiler can't tell that "pitting == true"
        // guarantees the out parameter below was actually assigned, since the call is
        // skipped entirely on the earlier && short-circuits (allowPIT false / wrong role).
        Vector3 pitTarget = Vector3.zero;
        bool pitting = allowPIT && role == CopRole.Pursuit
                       && TryStartOrContinuePIT(playerPos, playerVel, distToPlayer, out pitTarget);

        Vector3 chaseTarget = pitting ? pitTarget : GetChaseTarget(playerPos, playerVel, distToPlayer);
        Vector3 desiredDir = Flatten(chaseTarget - transform.position);

        if (desiredDir.sqrMagnitude < 0.0001f)
        {
            car.SteerInput = 0f;
            car.ThrottleInput = 0f;
            car.BrakeInput = 0f;
            return;
        }
        desiredDir.Normalize();

        // Cop-cop radius repulsion - steer away from squadmates crowding this cop.
        // Applied BEFORE wall avoidance so that walls always get the final say.
        // Skipped while committing to a PIT - nothing is allowed to nudge it off its strike.
        if (!pitting)
            desiredDir = ApplyCopSeparation(desiredDir);

        desiredDir = ApplyObstacleAvoidance(desiredDir, out float avoidanceUrgency, out bool emergencyStop);

        float turnAngle = Vector3.SignedAngle(transform.forward, desiredDir, Vector3.up);

        // --- K-TURN: target is far around behind us. Driving forward at full lock can
        // never win here - the turn circle just orbits the target forever (the donut
        // skid marks). Do what a real driver does instead: reverse while swinging the
        // nose around, then continue forward once roughly facing the target. ---
        if (HandleKTurn(turnAngle, distToPlayer)) return;

        float steer = Mathf.Clamp(turnAngle / Mathf.Max(1f, car.maxSteerAngle), -1f, 1f);

        float desiredSpeed = GetDesiredSpeed(distToPlayer, turnAngle, avoidanceUrgency, pitting, playerVel.magnitude);
        float speedError = desiredSpeed - car.CurrentSpeed;
        float throttle = Mathf.Clamp(speedError * throttleGain, -1f, 1f);

        // Resolve the FINAL throttle/brake before checking for "stuck" - a deliberate
        // emergency stop for an obstacle is correct behavior, not being stuck, and must
        // not feed the pre-override throttle into that check or it misreads "I correctly
        // braked" as "I'm trying to move and failing," which used to trigger a pointless
        // reverse right in the middle of a working avoidance maneuver.
        float finalThrottle = emergencyStop ? 0f : throttle;
        float finalBrake = emergencyStop ? 1f : 0f;

        UpdateStuckRecovery(finalThrottle, steer, emergencyStop);

        // An emergency stop held against something that never moves (a wall, a
        // wedged squadmate) must escalate into reverse-recovery, not hold forever -
        // exempting deliberate stops from stuck detection fixed one bug but created
        // that standing-at-the-wall deadlock. This closes it.
        UpdateEmergencyEscalation(emergencyStop, steer);

        car.SteerInput = steer;
        car.ThrottleInput = finalThrottle;
        car.BrakeInput = finalBrake;
    }

    // A real, physical backstop: if prediction ever fails and we actually hit
    // something on the obstacle layer anyway (shoved sideways, missed a fast
    // cross-street wall, whatever), read the true contact normal and reverse
    // away from THAT side, instead of grinding into it.
    // Also handles cop-vs-cop contact: previously cops weren't on the obstacle
    // mask, so two colliding cops triggered NO recovery at all and just pushed
    // into each other until both stalled. Now the cop that's facing INTO the
    // other one backs off briefly (only that one - if both reversed they'd
    // just start a new deadlock dance in the opposite direction).
    void OnCollisionEnter(Collision collision)
    {
        bool isObstacle = ((1 << collision.gameObject.layer) & obstacleMask) != 0;
        CopCarAI otherCop = collision.collider.GetComponentInParent<CopCarAI>();

        if (!isObstacle && otherCop == null) return;

        if (otherCop != null)
        {
            // Cop-cop bump: only the one driving into the other yields.
            Vector3 toOther = Flatten(otherCop.transform.position - transform.position).normalized;
            if (Vector3.Dot(transform.forward, toOther) < 0.2f) return; // they hit us; hold course

            reverseTimer = reverseDuration * 0.6f; // short - just break contact, then resume
        }
        else
        {
            reverseTimer = reverseDuration;
        }

        if (collision.contacts.Length > 0)
        {
            float side = Vector3.Dot(transform.right, collision.contacts[0].normal);
            recoverySteerSign = side >= 0f ? 1f : -1f;
        }
    }

    // ---------------------------------------------------------------
    // PLAYER DATA
    // ---------------------------------------------------------------
    bool HasPlayerData(out Vector3 pos, out Vector3 vel)
    {
        pos = Vector3.zero; vel = Vector3.zero;

        if (playerTransformOverride != null)
        {
            pos = playerTransformOverride.position;
            Rigidbody prb = playerTransformOverride.GetComponent<Rigidbody>();
            vel = prb != null ? prb.linearVelocity : Vector3.zero;
            return true;
        }

        if (playerRecorder == null || playerRecorder.Path.Count < 2) return false;

        pos = playerRecorder.Path[playerRecorder.Path.Count - 1];
        vel = playerRecorder.EstimatedVelocity;
        return true;
    }

    // ---------------------------------------------------------------
    // PIT MANEUVER
    // ---------------------------------------------------------------
    bool TryStartOrContinuePIT(Vector3 playerPos, Vector3 playerVel, float distToPlayer, out Vector3 pitTarget)
    {
        pitTarget = Vector3.zero;

        if (isPitting)
        {
            pitTimer -= Time.fixedDeltaTime;

            // Use the CACHED attack direction from when the PIT was committed to, not
            // whatever playerVel/transform.forward happen to be right now. Recomputing
            // this every frame was the actual bug behind the car spinning in a stable
            // circle: if the player slowed or stopped mid-maneuver, this fell back to
            // the cop's OWN forward vector - which the steering was simultaneously
            // trying to point at this same target, a closed feedback loop. A real PIT
            // commits to one line of attack and follows through on it.
            Vector3 lateral = Vector3.Cross(Vector3.up, pitDir) * pitSide;
            // Aim at the player's rear quarter panel, not their center - a clean
            // rear-corner hit is what actually spins a car out, a dead-center
            // rear hit just pushes them straight.
            pitTarget = playerPos - pitDir * 1.5f + lateral * 1.4f;

            if (pitTimer <= 0f) isPitting = false;
            return true;
        }

        if (distToPlayer > pitTriggerDistance || playerVel.magnitude < pitTriggerMinPlayerSpeed)
            return false;

        Vector3 toPlayer = playerPos - transform.position;
        float approachAngle = Vector3.Angle(playerVel, toPlayer);
        if (approachAngle > pitApproachAngleMax) return false;

        // Small per-physics-step chance rather than an instant trigger the moment
        // conditions are met - scaled by aggression, so tougher cops commit sooner.
        if (Random.value < pitChancePerSecond * personalityAggroMul * Time.fixedDeltaTime)
        {
            isPitting = true;
            pitTimer = pitDuration;
            pitSide = Vector3.Dot(transform.right, toPlayer.normalized) >= 0f ? 1f : -1f;
            pitDir = playerVel.normalized; // commit to this line of attack for the whole maneuver

            Vector3 lateral = Vector3.Cross(Vector3.up, pitDir) * pitSide;
            pitTarget = playerPos - pitDir * 1.5f + lateral * 1.4f;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------
    // TARGET SELECTION (role + far/near blend + prediction)
    // ---------------------------------------------------------------
    Vector3 GetChaseTarget(Vector3 playerPos, Vector3 playerVel, float distToPlayer)
    {
        if (role == CopRole.Intercept)
        {
            Vector3 farAhead = playerPos + Vector3.ClampMagnitude(playerVel * (predictionTime * 1.8f), maxPredictionDistance);
            return GetNavMeshTarget(farAhead, playerPos);
        }

        // Pursuit, close range: stop being polite about the road and just go straight for them.
        if (distToPlayer < followDistance * 0.8f)
            return playerPos;

        // Pursuit, mid/far range: blend between "replay the player's exact trail"
        // (near, guaranteed drivable) and "shortest road route to their predicted
        // position" (far, catches up faster) - no hard snap between the two.
        Vector3 predicted = playerPos + Vector3.ClampMagnitude(playerVel * predictionTime, maxPredictionDistance);

        // Surround/box-in: inside engage range with squadmates around, each cop
        // approaches its own slot on a ring around the player instead of every
        // cop funneling onto the same point (which is what caused the pileups).
        // Slots only shape the APPROACH - the close-range branch above still
        // sends everyone in for actual contact once they're on top of the player.
        if (enableSurround && distToPlayer < surroundEngageRange && AllCops.Count > 1)
            predicted = GetSurroundTarget(playerPos, playerVel);

        float blend = Mathf.InverseLerp(farDistanceThreshold - modeBlendRange, farDistanceThreshold, distToPlayer);
        Vector3 trail = GetRecordedTrailTarget();
        Vector3 navTarget = GetNavMeshTarget(predicted, playerPos);
        return Vector3.Lerp(trail, navTarget, blend);
    }

    // ---------------------------------------------------------------
    // SURROUND SLOTS - stable ring of approach points around the player,
    // one per active cop. Slot assignment is by instance ID order, so each
    // cop keeps the same slot between frames (no slot-swapping jitter) and
    // slots redistribute automatically as cops spawn/despawn.
    // ---------------------------------------------------------------
    Vector3 GetSurroundTarget(Vector3 playerPos, Vector3 playerVel)
    {
        int myIndex = 0;
        int count = 0;
        int myId = GetInstanceID();
        for (int i = 0; i < AllCops.Count; i++)
        {
            if (AllCops[i] == null) continue;
            count++;
            if (AllCops[i].GetInstanceID() < myId) myIndex++;
        }
        if (count < 2) return playerPos;

        // Ring is oriented off the player's heading so slot 0 sits directly
        // BEHIND them - with a stable fallback direction when they're stopped
        // (any fixed basis works; the slots cover the full circle regardless).
        Vector3 heading = playerVel.sqrMagnitude > 0.25f ? playerVel.normalized : Vector3.forward;
        Vector3 behind = -heading;

        float slotAngle = (360f / count) * myIndex;
        Vector3 slotDir = Quaternion.Euler(0f, slotAngle, 0f) * behind;

        return playerPos + slotDir * surroundDistance;
    }

    Vector3 GetRecordedTrailTarget()
    {
        if (playerRecorder == null || playerRecorder.Path.Count < 2) return transform.position;

        var path = playerRecorder.Path;
        float acc = 0f;
        for (int i = path.Count - 1; i > 0; i--)
        {
            acc += Vector3.Distance(path[i], path[i - 1]);
            if (acc >= followDistance) return path[i];
        }
        return path[0];
    }

    Vector3 GetNavMeshTarget(Vector3 target, Vector3 fallbackTarget)
    {
        bool needsRecalc = navPoints.Count == 0
            || navTimer >= navPathRecalcInterval
            || Vector3.Distance(target, lastNavPlanTarget) > navRecalcMoveThreshold;

        navTimer += Time.fixedDeltaTime;

        if (needsRecalc)
        {
            navTimer = 0f;
            lastNavPlanTarget = target;
            BuildNavRoadPath(target, fallbackTarget);
        }

        if (navPoints.Count == 0) return target;

        if (Vector3.Distance(transform.position, navPoints[navIndex]) < 1.0f && navIndex < navPoints.Count - 1)
            navIndex++;

        return navPoints[Mathf.Clamp(navIndex, 0, navPoints.Count - 1)];
    }

    void BuildNavRoadPath(Vector3 primaryTarget, Vector3 fallbackTarget)
    {
        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit startHit, 6f, NavMesh.AllAreas)) return;

        // The predicted point is a straight-line extrapolation - it's easy for it to land
        // just off the road (right after a turn, near an intersection, etc). If it can't be
        // sampled onto the NavMesh, fall back to the player's actual last known on-road spot
        // instead of leaving navPoints stale and having the cop aim in a straight line at an
        // unreachable point (which is what was driving it through the corner buildings).
        if (!NavMesh.SamplePosition(primaryTarget, out NavMeshHit endHit, 8f, NavMesh.AllAreas)
            && !NavMesh.SamplePosition(fallbackTarget, out endHit, 8f, NavMesh.AllAreas))
        {
            return; // nothing usable nearby on the mesh - keep whatever path we already had
        }

        if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, navPath)) return;

        // A partial path still makes real progress along actual roads, which is far better
        // than discarding it and falling back to a straight line through buildings. Only a
        // genuinely invalid path (e.g. fully disconnected NavMesh islands) gets rejected.
        if (navPath.status == NavMeshPathStatus.PathInvalid) return;

        var newPoints = new List<Vector3>();
        for (int i = 0; i < navPath.corners.Length - 1; i++)
        {
            Vector3 a = navPath.corners[i];
            Vector3 b = navPath.corners[i + 1];
            int steps = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(a, b) / sampleSpacing));
            for (int s = 0; s <= steps; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, s / (float)steps);
                if (snapNavPointsToRoad) p = SnapToRoad(p);
                newPoints.Add(p);
            }
        }

        if (newPoints.Count == 0) return;

        navPoints = newPoints;
        // Preserve progress: resume from whichever new point is closest to where
        // we actually are, instead of snapping back to index 0 on every replan.
        navIndex = FindNearestIndex(navPoints, transform.position);
    }

    int FindNearestIndex(List<Vector3> points, Vector3 pos)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            float d = (points[i] - pos).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

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
        return best < Mathf.Infinity ? bestPoint + Vector3.up * 0.05f : p;
    }

    // ---------------------------------------------------------------
    // OBSTACLE AVOIDANCE - wide, speed-scaled sensors + a decisive emergency
    // stop, instead of thin fixed-length rays blended gently into the steer.
    // ---------------------------------------------------------------
    Vector3 ApplyObstacleAvoidance(Vector3 desiredDir, out float urgency, out bool emergencyStop)
    {
        urgency = 0f;
        emergencyStop = false;

        float lookAhead = minLookAhead + car.CurrentSpeed * lookAheadPerSpeed;
        Vector3 origin = transform.position + Vector3.up * 0.6f;

        // Cast along actual velocity, not just facing direction - catches cases
        // where momentum is carrying the car somewhere the nose isn't pointed.
        Vector3 moveDir = car.CurrentSpeed > 1f ? car.Velocity.normalized : transform.forward;

        // Sensors see walls AND other cops (if copVehicleMask is set) - so a cop
        // closing on a squadmate's rear brakes/steers around it like any obstacle
        // instead of plowing into a pileup.
        int senseMask = obstacleMask.value | copVehicleMask.value;

        bool centerHit = Physics.SphereCast(origin, sensorRadius, moveDir, out RaycastHit hitC, lookAhead, senseMask);
        bool leftHit = Physics.SphereCast(origin, sensorRadius * 0.6f, Quaternion.Euler(0f, -sideSensorAngle, 0f) * transform.forward, out RaycastHit hitL, lookAhead * 0.7f, senseMask);
        bool rightHit = Physics.SphereCast(origin, sensorRadius * 0.6f, Quaternion.Euler(0f, sideSensorAngle, 0f) * transform.forward, out RaycastHit hitR, lookAhead * 0.7f, senseMask);

        if (!centerHit && !leftHit && !rightHit)
        {
            lastAvoidSide = 0f; // clear - next time an obstacle appears, pick fresh
            return desiredDir;
        }

        float centerClear = centerHit ? hitC.distance : lookAhead;
        float leftClear = leftHit ? hitL.distance : lookAhead * 0.7f;
        float rightClear = rightHit ? hitR.distance : lookAhead * 0.7f;

        urgency = 1f - Mathf.Clamp01(centerClear / lookAhead);

        // Close enough, straight-on enough, that steering alone won't save it - brake, don't just slow down.
        if (centerHit && hitC.distance < sensorRadius * 3f)
            emergencyStop = true;

        // Pick which side to swerve toward - but stick with the last choice unless the
        // OTHER side is clearly better by a real margin. Without this, a building corner
        // sitting roughly dead ahead makes left/right clearance flip every frame from tiny
        // sensor noise, which reads as the car rotating in place instead of committing.
        float clearDiff = leftClear - rightClear;
        float side;
        if (lastAvoidSide != 0f && Mathf.Abs(clearDiff) < sideSwitchMargin)
            side = lastAvoidSide;
        else
            side = clearDiff >= 0f ? -1f : 1f;
        lastAvoidSide = side;

        Vector3 avoidDir = (Quaternion.Euler(0f, side * 60f, 0f) * transform.forward).normalized;

        // Hard override once urgency is high, instead of a gentle blend a fast car can just power through.
        float steerBlend = urgency > 0.6f ? 1f : avoidanceSteerStrength * urgency;
        return Vector3.Slerp(desiredDir, avoidDir, steerBlend).normalized;
    }

    // ---------------------------------------------------------------
    // K-TURN - reverse-and-swing when the target is behind us at close
    // range. Forward-only steering geometrically cannot reach a point
    // inside its own turn circle; it just orbits it (the donut marks).
    // Returns true if it drove the car this step.
    // ---------------------------------------------------------------
    bool HandleKTurn(float turnAngle, float distToPlayer)
    {
        if (kTurning)
        {
            kTurnTimer += Time.fixedDeltaTime;

            // Done once we're roughly facing the target - or bail on timeout.
            if (Mathf.Abs(turnAngle) < 45f || kTurnTimer > kTurnMaxDuration)
            {
                kTurning = false;
                return false;
            }

            // Reversing flips steering geometry: backing up with wheels turned
            // right swings the NOSE left. So steer opposite the target side to
            // rotate toward it while reversing.
            car.SteerInput = -Mathf.Sign(turnAngle);
            car.ThrottleInput = -0.8f;
            car.BrakeInput = 0f;
            return true;
        }

        bool targetBehind = Mathf.Abs(turnAngle) > kTurnTriggerAngle;
        bool closeEnough = distToPlayer < kTurnMaxDistance;
        bool slowEnough = car.CurrentSpeed < 7f; // at real speed a forward arc is still the faster way around

        if (targetBehind && closeEnough && slowEnough)
        {
            kTurning = true;
            kTurnTimer = 0f;
            car.SteerInput = -Mathf.Sign(turnAngle);
            car.ThrottleInput = -0.8f;
            car.BrakeInput = 0f;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------
    // SPEED
    // ---------------------------------------------------------------
    float GetDesiredSpeed(float distToPlayer, float turnAngleDeg, float avoidanceUrgency, bool pitting, float playerSpeed)
    {
        float topSpeed = baseTopSpeed * personalitySpeedMul * Mathf.Lerp(0.85f, 1.15f, difficulty);
        if (pitting) topSpeed *= ramSpeedBoost;

        // Corner slowdown - skipped entirely while committing to a PIT, it's already decided to hit them.
        float turnFactor = pitting ? 1f : Mathf.Clamp01(1f - (Mathf.Abs(turnAngleDeg) / 90f) * cornerSlowdownStrength);

        // Rubber-banding - fall too far behind and cops get a bit faster to close the gap.
        float rubberFactor = 1f;
        if (distToPlayer > rubberBandStartDistance)
            rubberFactor = 1f + Mathf.Clamp01((distToPlayer - rubberBandStartDistance) / rubberBandStartDistance) * rubberBandMaxBoost * personalityAggroMul;

        float avoidanceFactor = Mathf.Lerp(1f, 0.4f, avoidanceUrgency);

        float speed = topSpeed * turnFactor * rubberFactor * avoidanceFactor;

        // GTA-style close engagement on a slow/stopped player: scale speed down
        // with distance so we arrive under control instead of flying past into
        // an orbit - but never below minEngageSpeed, so the cop still shoves
        // into contact and keeps pinning, rather than politely parking nearby.
        // Deliberately NOT applied while the player is at speed - ramming a
        // moving target needs full closing speed, not an arrival slowdown.
        if (playerSpeed < slowPlayerSpeed && distToPlayer < farDistanceThreshold)
        {
            float arrivalSpeed = Mathf.Max(minEngageSpeed, distToPlayer * engageSpeedPerMeter);
            speed = Mathf.Min(speed <= 0.01f ? arrivalSpeed : speed, arrivalSpeed);
            // Turn slowdown must not fully zero the approach either, or the cop
            // dithers at the edge of its turn circle instead of closing in.
            speed = Mathf.Max(speed, minEngageSpeed * 0.6f);
        }

        return speed;
    }

    // ---------------------------------------------------------------
    // COP-COP RADIUS REPULSION - each cop pushes its desired direction
    // away from squadmates inside copSeparationRadius, weighted by how
    // close they are. Registry-based, so it needs zero layer setup.
    // ---------------------------------------------------------------
    Vector3 ApplyCopSeparation(Vector3 desiredDir)
    {
        if (copSeparationStrength <= 0f || AllCops.Count < 2) return desiredDir;

        Vector3 sep = Vector3.zero;
        for (int i = 0; i < AllCops.Count; i++)
        {
            CopCarAI other = AllCops[i];
            if (other == this || other == null) continue;

            Vector3 away = Flatten(transform.position - other.transform.position);
            float dist = away.magnitude;
            if (dist < 0.01f || dist > copSeparationRadius) continue;

            // Linear falloff: right on top of each other = full push, at the
            // edge of the radius = nothing.
            sep += (away / dist) * (1f - dist / copSeparationRadius);
        }

        if (sep == Vector3.zero) return desiredDir;

        return (desiredDir + sep * copSeparationStrength).normalized;
    }

    // ---------------------------------------------------------------
    // EMERGENCY-STOP ESCALATION - a deliberate obstacle stop is correct
    // for a moment, but held indefinitely against something that never
    // moves it becomes the standing-at-the-wall deadlock. After a short
    // grace period, hand over to the same reverse-recovery used for
    // real collisions.
    // ---------------------------------------------------------------
    void UpdateEmergencyEscalation(bool emergencyStop, float steer)
    {
        if (emergencyStop && car.CurrentSpeed < 1.5f)
        {
            emergencyStopTimer += Time.fixedDeltaTime;
            if (emergencyStopTimer >= emergencyStopEscalateTime)
            {
                reverseTimer = reverseDuration;
                recoverySteerSign = steer >= 0f ? 1f : -1f;
                emergencyStopTimer = 0f;
            }
        }
        else
        {
            emergencyStopTimer = 0f;
        }
    }

    // ---------------------------------------------------------------
    // STUCK RECOVERY (secondary safety net) - if we're commanding real
    // throttle but barely moving for a surface/geometry reason the sensors
    // didn't catch, back out rather than sitting there revving forever.
    // ---------------------------------------------------------------
    void UpdateStuckRecovery(float throttle, float steer, bool isDeliberateStop)
    {
        // A deliberate obstacle stop is working as intended - it is not "stuck."
        bool tryingToMove = !isDeliberateStop && Mathf.Abs(throttle) > 0.3f;

        if (tryingToMove && car.CurrentSpeed < stuckSpeedThreshold)
            stuckTimer += Time.fixedDeltaTime;
        else
            stuckTimer = 0f;

        if (stuckTimer > stuckTimeToTriggerReverse)
        {
            reverseTimer = reverseDuration;
            recoverySteerSign = steer >= 0f ? 1f : -1f;
            stuckTimer = 0f;
        }
    }

    Vector3 Flatten(Vector3 v) { v.y = 0f; return v; }

    // ---------------------------------------------------------------
    // GIZMOS (DEBUG VISUALIZATION)
    // ---------------------------------------------------------------
    void OnDrawGizmos()
    {
        if (!Application.isPlaying) return;

        if (playerRecorder != null && playerRecorder.Path != null)
        {
            Gizmos.color = Color.gray;
            for (int i = 1; i < playerRecorder.Path.Count; i++)
                Gizmos.DrawLine(playerRecorder.Path[i - 1], playerRecorder.Path[i]);
        }

        Gizmos.color = isPitting ? Color.magenta : (role == CopRole.Intercept ? Color.yellow : Color.cyan);
        for (int i = 1; i < navPoints.Count; i++)
            Gizmos.DrawLine(navPoints[i - 1], navPoints[i]);

        if (navPoints.Count > navIndex)
            Gizmos.DrawSphere(navPoints[navIndex], 0.35f);
    }

    // Radius / range visualization - drawn only for the SELECTED cop so six of
    // them don't turn the Scene view into sphere soup. Works in edit mode too,
    // for tuning before pressing Play.
    //
    // Legend: yellow = cop-cop separation, red = obstacle sensors, orange =
    // close-range direct-pursuit threshold, blue = far/near blend threshold,
    // dark red = PIT trigger range, green/magenta = surround engage/slot ring.
    void OnDrawGizmosSelected()
    {
        // Cop-cop separation radius (yellow) - squadmates inside this get steered away from
        Gizmos.color = new Color(1f, 0.85f, 0.1f);
        Gizmos.DrawWireSphere(transform.position, copSeparationRadius);

        // Close-range direct-pursuit threshold (orange) - inside this, the cop
        // stops road-locking and just drives straight at the player.
        Gizmos.color = new Color(1f, 0.5f, 0f);
        Gizmos.DrawWireSphere(transform.position, followDistance * 0.8f);

        // Far/near blend threshold (blue) - beyond this, chase target blends
        // toward the NavMesh route; inside it, blends toward the recorded trail.
        Gizmos.color = new Color(0.3f, 0.5f, 1f);
        Gizmos.DrawWireSphere(transform.position, farDistanceThreshold);

        // PIT trigger range (dark red) - only meaningful for Pursuit cops.
        if (role == CopRole.Pursuit && allowPIT)
        {
            Gizmos.color = new Color(0.6f, 0f, 0f);
            Gizmos.DrawWireSphere(transform.position, pitTriggerDistance);
        }

        // Forward obstacle sensors (red) - center + side whiskers, with the
        // SphereCast thickness shown at the far end. Uses live speed-scaled
        // length in Play mode, minimum length in edit mode.
        float look = (Application.isPlaying && car != null)
            ? minLookAhead + car.CurrentSpeed * lookAheadPerSpeed
            : minLookAhead;
        Vector3 origin = transform.position + Vector3.up * 0.6f;

        Gizmos.color = Color.red;
        Vector3 fwdEnd = origin + transform.forward * look;
        Gizmos.DrawLine(origin, fwdEnd);
        Gizmos.DrawWireSphere(fwdEnd, sensorRadius);

        Vector3 leftDir = Quaternion.Euler(0f, -sideSensorAngle, 0f) * transform.forward;
        Vector3 rightDir = Quaternion.Euler(0f, sideSensorAngle, 0f) * transform.forward;
        Vector3 leftEnd = origin + leftDir * look * 0.7f;
        Vector3 rightEnd = origin + rightDir * look * 0.7f;
        Gizmos.DrawLine(origin, leftEnd);
        Gizmos.DrawWireSphere(leftEnd, sensorRadius * 0.6f);
        Gizmos.DrawLine(origin, rightEnd);
        Gizmos.DrawWireSphere(rightEnd, sensorRadius * 0.6f);

        // Surround ranges around the player (green = engage range, magenta = slot ring)
        Transform playerT = playerTransformOverride != null ? playerTransformOverride
                          : (playerRecorder != null ? playerRecorder.transform : null);
        if (enableSurround && playerT != null)
        {
            Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.8f);
            Gizmos.DrawWireSphere(playerT.position, surroundEngageRange);

            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(playerT.position, surroundDistance);

            // This cop's actual slot point (only meaningful in Play mode - slot
            // assignment depends on the live registry of active cops)
            if (Application.isPlaying && AllCops.Count > 1)
            {
                Vector3 vel = Vector3.zero;
                if (HasPlayerData(out Vector3 pPos, out Vector3 pVel)) { vel = pVel; }
                Vector3 slot = GetSurroundTarget(playerT.position, vel);
                Gizmos.DrawSphere(slot, 0.5f);
                Gizmos.DrawLine(transform.position, slot);
            }
        }
    }
}