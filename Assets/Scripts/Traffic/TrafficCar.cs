using UnityEngine;

// ==================================================================
// TRAFFIC CAR - civilian NPC driver (the Smash Bandits traffic)
// ------------------------------------------------------------------
// A cheap-but-smart lane follower. Deliberately NO WheelColliders -
// a dozen of these must cost almost nothing on mobile/web, so it's a
// dynamic Rigidbody with a plain box collider, driven by setting
// velocity directly while "in control", and handing over to raw
// physics the moment it gets smashed (which is the whole point of
// traffic in this genre).
//
// Behavior stack, highest priority first:
//   WRECKED       - hit hard: pure physics, hazards on, no driving
//   PANIC         - something fast is about to hit us: swerve + brake
//   PULLING OVER  - cop bearing down from behind: edge over and stop
//   RED LIGHT     - hold at the stop line until our approach is green
//   CAR AHEAD     - keep gap / queue behind slower traffic
//   CRUISE        - follow the lane at cruise speed
//
// Setup: prefab with Rigidbody (constraints handled here) + a Box
// Collider + this script. Spawned/recycled by TrafficSpawner - or
// place one manually and call Init(startWaypoint) yourself.
// ==================================================================

[RequireComponent(typeof(Rigidbody))]
public class TrafficCar : MonoBehaviour
{
    public enum NpcState { Driving, PullingOver, Panic, Wrecked }

    [Header("Driving")]
    public float cruiseSpeed = 9f;
    public float acceleration = 8f;
    public float brakingDecel = 18f;
    public float turnSpeed = 5f;
    [Tooltip("Distance to a waypoint at which the car advances to the next one.")]
    public float waypointReachDistance = 2.2f;
    [Tooltip("Random per-car speed/gap variance so traffic doesn't look cloned.")]
    public bool randomizePersonality = true;

    [Header("Gap Keeping (vehicle ahead)")]
    [Tooltip("Everything the forward sensor should stop for: other traffic, the player, cops.")]
    public LayerMask vehicleMask;
    public float sensorRadius = 0.8f;
    [Tooltip("Full-stop gap to the vehicle ahead.")]
    public float stopGap = 2.5f;
    [Tooltip("Extra sensing distance added per m/s of current speed.")]
    public float lookAheadPerSpeed = 0.9f;

    [Header("Red Lights")]
    [Tooltip("How far before the stop-line waypoint the car starts holding for a red.")]
    public float lightStopDistance = 4f;

    [Header("Cop Pull-Over")]
    [Tooltip("Tag used by cop cars - cheap way to spot them without layer coupling.")]
    public string copTag = "Cop";
    public float copDetectRadius = 15f;
    [Tooltip("Cop must be moving at least this fast to trigger a pull-over (a parked cop isn't 'responding').")]
    public float copMinSpeed = 8f;
    [Tooltip("How far to the roadside edge the car offsets while pulled over.")]
    public float pullOverOffset = 2.2f;
    [Tooltip("Resume driving this long after the last fast cop left the radius.")]
    public float pullOverLinger = 1.5f;

    [Header("Panic (near miss)")]
    public float panicRadius = 9f;
    [Tooltip("Closing speed toward us above which we panic-swerve.")]
    public float panicClosingSpeed = 9f;
    public float panicSwerveOffset = 2.5f;
    public float panicDuration = 1.6f;

    [Header("Impacts")]
    [Tooltip("Collision impulse above this = permanently wrecked (physics takes over until despawn).")]
    public float wreckImpulse = 6000f;
    [Tooltip("Collision impulse above this (but below wreck) = brief panic, then resume driving.")]
    public float bumpImpulse = 1200f;

    [Header("FX Hooks (optional)")]
    public GameObject brakeLights;
    public GameObject hazardLights;
    public AudioSource hornAudio;

    [Header("Rigidbody")]
    public float vehicleMass = 900f;

    // ---- runtime ----
    Rigidbody rb;
    TrafficWaypoint currentTarget;
    NpcState state = NpcState.Driving;

    float currentSpeed;
    float personalitySpeedMul = 1f;
    float personalityGapMul = 1f;

    float panicTimer;
    Vector3 panicLateral;
    float copClearTimer;
    float scanTimer;
    bool copNearby;
    Vector3 threatLateral;
    bool warnedDeadEnd;

    public NpcState State => state;
    public bool IsWrecked => state == NpcState.Wrecked;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = vehicleMass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        // Driven flat by the controller; physics gets rotation freedom only when wrecked.
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        if (randomizePersonality)
        {
            personalitySpeedMul = Random.Range(0.85f, 1.1f);
            personalityGapMul = Random.Range(0.9f, 1.4f); // timid drivers leave bigger gaps
        }
    }

    // Called by TrafficSpawner on spawn/recycle (or manually for placed cars).
    public void Init(TrafficWaypoint startTarget)
    {
        currentTarget = startTarget;
        state = NpcState.Driving;
        currentSpeed = 0f;
        panicTimer = 0f;
        copClearTimer = 0f;
        warnedDeadEnd = false;

        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        if (hazardLights != null) hazardLights.SetActive(false);
        if (brakeLights != null) brakeLights.SetActive(false);

        if (currentTarget != null)
        {
            Vector3 look = currentTarget.transform.position - transform.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(look);
        }
    }

    void FixedUpdate()
    {
        if (state == NpcState.Wrecked || currentTarget == null)
            return; // pure physics / nothing to do

        // Cheap periodic world scan (cops + incoming threats), not every physics step.
        scanTimer -= Time.fixedDeltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = 0.25f;
            ScanSurroundings();
        }

        if (state == NpcState.Panic)
        {
            panicTimer -= Time.fixedDeltaTime;
            if (panicTimer <= 0f) state = NpcState.Driving;
        }

        // ---- pick the steering target ----
        Vector3 target = currentTarget.transform.position;
        if (state == NpcState.PullingOver)
            target += transform.right * pullOverOffset;       // hug the roadside
        else if (state == NpcState.Panic)
            target += panicLateral;                            // swerve away from the threat

        // ---- decide desired speed (most restrictive rule wins) ----
        float desired = cruiseSpeed * personalitySpeedMul;

        if (currentTarget.speedLimit > 0f)
            desired = Mathf.Min(desired, currentTarget.speedLimit);

        float distToTarget = Vector3.Distance(transform.position, target);

        bool atRed = currentTarget.intersection != null
                     && !currentTarget.intersection.CanGo(currentTarget.approachIndex);

        // red light: hold at the stop line until our approach is green
        if (atRed && distToTarget < lightStopDistance)
        {
            desired = 0f;
        }

        // vehicle ahead: keep gap / queue
        desired = Mathf.Min(desired, GapLimitedSpeed());

        // pulled over: creep to the edge, then hold
        if (state == NpcState.PullingOver)
            desired = distToTarget > 1.2f ? Mathf.Min(desired, 3f) : 0f;

        // panicking: brake hard while swerving (the swerve is in the target)
        if (state == NpcState.Panic)
            desired = Mathf.Min(desired, 2f);

        // ---- steer & move ----
        Vector3 dir = target - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            Quaternion look = Quaternion.LookRotation(dir.normalized);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, turnSpeed * Time.fixedDeltaTime);
        }

        float rate = desired > currentSpeed ? acceleration : brakingDecel;
        currentSpeed = Mathf.MoveTowards(currentSpeed, desired, rate * Time.fixedDeltaTime);

        // Drive horizontal velocity; leave Y to gravity so slopes/curbs behave.
        Vector3 vel = transform.forward * currentSpeed;
        vel.y = rb.linearVelocity.y;
        rb.linearVelocity = vel;

        if (brakeLights != null)
            brakeLights.SetActive(desired < currentSpeed - 0.5f || desired <= 0.1f);

        // ---- advance along the lane ----
        // NEVER advance past a stop line while its light is red - braking from
        // cruise inside lightStopDistance ends with the car resting INSIDE
        // waypointReachDistance of the stop line, and advancing there would
        // switch the target to a waypoint with no intersection binding,
        // silently dismissing the red. (This was why cars ignored signals.)
        if (!atRed
            && state != NpcState.PullingOver
            && Vector3.Distance(transform.position, currentTarget.transform.position) < waypointReachDistance)
        {
            TrafficWaypoint next = currentTarget.PickNext();
            if (next == null)
            {
                // Dead end: this waypoint's 'next' list is empty, most often a
                // stop-line just past an intersection that was never linked
                // onward into the continuing lane. Without this warning the
                // car just goes idle forever with zero indication why - which
                // reads exactly like "the light's green but it won't move."
                if (!warnedDeadEnd)
                {
                    warnedDeadEnd = true;
                    Debug.LogWarning($"{name}: reached '{currentTarget.name}' which has no outgoing link - this car will sit here until that waypoint's 'next' list is connected onward.", currentTarget);
                }
                // Stay on the same target rather than nulling it out - as soon
                // as the link is fixed in the Inspector, it resumes instantly,
                // no respawn needed.
            }
            else
            {
                currentTarget = next;
                warnedDeadEnd = false;
            }
        }
    }

    // ---------------------------------------------------------------
    // WORLD AWARENESS (runs 4x/sec)
    // ---------------------------------------------------------------
    void ScanSurroundings()
    {
        copNearby = false;
        Collider[] hits = Physics.OverlapSphere(transform.position, Mathf.Max(copDetectRadius, panicRadius));

        foreach (var h in hits)
        {
            Rigidbody orb = h.attachedRigidbody;
            if (orb == null || orb == rb) continue;

            Vector3 toMe = transform.position - orb.position;
            float dist = toMe.magnitude;
            float speed = orb.linearVelocity.magnitude;

            // --- cop responding nearby -> pull over ---
            if (dist < copDetectRadius && speed > copMinSpeed && orb.CompareTag(copTag))
                copNearby = true;

            // --- anything fast on a collision course -> panic ---
            if (dist < panicRadius && state != NpcState.Panic)
            {
                float closing = Vector3.Dot(orb.linearVelocity, toMe.normalized);
                if (closing > panicClosingSpeed)
                {
                    // swerve to whichever side gets us OUT of the threat's path
                    Vector3 threatDir = orb.linearVelocity.normalized;
                    Vector3 side = Vector3.Cross(Vector3.up, threatDir);
                    float sign = Vector3.Dot(toMe, side) >= 0f ? 1f : -1f;
                    panicLateral = side * sign * panicSwerveOffset;

                    EnterPanic();
                }
            }
        }

        // pull-over state transitions (with a linger so we don't resume mid-convoy)
        if (copNearby)
        {
            copClearTimer = pullOverLinger;
            if (state == NpcState.Driving) state = NpcState.PullingOver;
        }
        else if (state == NpcState.PullingOver)
        {
            copClearTimer -= 0.25f;
            if (copClearTimer <= 0f) state = NpcState.Driving;
        }
    }

    float GapLimitedSpeed()
    {
        float lookAhead = stopGap + currentSpeed * lookAheadPerSpeed * personalityGapMul;
        Vector3 origin = transform.position + Vector3.up * 0.5f;

        if (Physics.SphereCast(origin, sensorRadius, transform.forward, out RaycastHit hit, lookAhead, vehicleMask))
        {
            if (hit.distance <= stopGap) return 0f;
            // linear ramp: full speed at the edge of sensing, zero at stopGap
            float t = (hit.distance - stopGap) / Mathf.Max(0.1f, lookAhead - stopGap);
            return cruiseSpeed * personalitySpeedMul * Mathf.Clamp01(t);
        }
        return float.MaxValue;
    }

    void EnterPanic()
    {
        state = NpcState.Panic;
        panicTimer = panicDuration;
        if (hornAudio != null && !hornAudio.isPlaying) hornAudio.Play();
    }

    void Wreck()
    {
        state = NpcState.Wrecked;
        rb.constraints = RigidbodyConstraints.None; // full ragdoll physics from here
        if (hazardLights != null) hazardLights.SetActive(true);
        if (brakeLights != null) brakeLights.SetActive(false);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (state == NpcState.Wrecked) return;

        float impulse = collision.impulse.magnitude;

        if (impulse >= wreckImpulse)
        {
            Wreck();
        }
        else if (impulse >= bumpImpulse)
        {
            // shoved but driveable: panic away from the hit, then resume
            if (collision.contacts.Length > 0)
            {
                Vector3 n = collision.contacts[0].normal; // points away from the other object, toward us
                n.y = 0f;
                panicLateral = n.normalized * panicSwerveOffset;
            }
            EnterPanic();
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.1f);
        Gizmos.DrawWireSphere(transform.position, copDetectRadius);
        Gizmos.color = new Color(1f, 0.4f, 0.1f);
        Gizmos.DrawWireSphere(transform.position, panicRadius);

        if (currentTarget != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(transform.position, currentTarget.transform.position);
        }
    }
}