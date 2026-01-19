using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody))]
public class NPCcarAI : MonoBehaviour
{
    // ----- Path / spline -----
    [Header("Path (waypoints)")]
    public Transform[] waypoints;
    public bool loopPath = true;

    [Header("Spline (visual & smoothing)")]
    [Range(2, 48)] public int splineResolution = 12;
    [Range(0f, 1f)] public float curveTension = 0.5f;
    public bool drawSplineGizmos = true;
    public Color splineColor = Color.yellow;

    // ----- Movement -----
    [Header("Motion")]
    public float moveSpeed = 6f;          // forward cruising speed (m/s)
    public float reverseSpeed = 3f;       // reversing speed (m/s)
    public float rotateSpeed = 6f;
    public float accelLerp = 8f;          // smoothing for speed
    public float stopVelocityThreshold = 0.25f;

    // ----- Detection -----
    [Header("Detection")]
    public string playerTag = "Player";
    public float detectionDistance = 10f;
    [Tooltip("Half-angle of forward detection cone (deg)")]
    public float detectionAngle = 35f;
    [Tooltip("Time to wait stopped before starting reverse (seconds)")]
    public float stopHoldTime = 2f;
    public LayerMask obstacleMask = ~0;
    public LayerMask detectionRayMask = ~0;

    // ----- Aside behavior -----
    [Header("Aside target & clearance")]
    public bool tryMoveAside = true;
    public float asideOffset = 2.2f;         // lateral offset to move into
    public float asideForward = 1.0f;        // forward/back offset used for aside target
    public float asideReachRadius = 0.6f;
    public float sideCheckForward = 1.2f;
    public float sideCheckDistance = 2.8f;
    public float sideClearanceRadius = 0.6f;

    // ----- Reverse curve tuning -----
    [Header("Reverse curve tuning")]
    [Tooltip("How far back to pull control point for the reverse curve (in meters)")]
    public float reverseBackDistance = 3.2f;
    [Tooltip("How much lateral bias control point gets (multiplier)")]
    [Range(0f, 2f)] public float reverseLateralBias = 1.0f;
    [Tooltip("Number of samples for the generated reverse curve")]
    [Range(4, 64)] public int reverseCurveSamples = 24;

    [Header("Avoidance")]
    public float avoidCheckDistance = 5f;
    public float avoidRayAngle = 25f;
    public float avoidLateralStrength = 1f;

    [Header("Debug")]
    public bool drawDebug = false;
    public bool drawDebugRays = false;

    // ----- Internal -----
    Rigidbody rb;
    float currentSpeed = 0f;

    private List<Vector3> splinePoints = new List<Vector3>();
    private int currentWaypoint = 0;

    enum State { Driving, BrakingWaiting, ReversingCurve, MovingAside, Waiting }
    private State state = State.Driving;

    // detection timers & player ref
    private Transform playerT;
    private float stopTimer = 0f;

    // aside / reverse path
    private Vector3 asideTarget;
    private int asideSide = 0; // -1 left, +1 right

    // reverse path points (generated when needed)
    private List<Vector3> reversePath = null;
    private int reverseIndex = 0;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        playerT = FindPlayer();
        RebuildSpline();
    }

    void OnValidate()
    {
        splineResolution = Mathf.Max(2, splineResolution);
        reverseCurveSamples = Mathf.Clamp(reverseCurveSamples, 4, 64);
        curveTension = Mathf.Clamp01(curveTension);
        sideClearanceRadius = Mathf.Max(0.01f, sideClearanceRadius);
        RebuildSpline();
    }

    void Update()
    {
        if (playerT == null) playerT = FindPlayer();
#if UNITY_EDITOR
        RebuildSpline();
#endif
    }

    void FixedUpdate()
    {
        if (waypoints == null || waypoints.Length == 0)
        {
            ApplyHold();
            return;
        }

        if (playerT == null) playerT = FindPlayer();

        bool playerDetected = (playerT != null) && IsEntityInFront(playerT);
        bool obstacleAhead = IsObstacleBlockingFront();
        bool frontThreat = playerDetected || obstacleAhead;

        switch (state)
        {
            case State.Driving:
                if (frontThreat)
                {
                    state = State.BrakingWaiting;
                    stopTimer = 0f;
                    if (drawDebug) Debug.Log($"{name}: threat detected -> braking & waiting");
                }
                else
                {
                    float avoidOffset = ComputeAvoidanceOffset();
                    DriveAlongSpline(avoidOffset);
                }
                break;

            case State.BrakingWaiting:
                // immediate hold/brake
                ApplyHold();

                // if threat disappears while braking, resume driving
                if (!frontThreat)
                {
                    state = State.Driving;
                    stopTimer = 0f;
                    if (drawDebug) Debug.Log($"{name}: threat cleared -> resume driving");
                    break;
                }

                // count hold time only when nearly stopped
                if (rb.linearVelocity.magnitude <= stopVelocityThreshold)
                    stopTimer += Time.fixedDeltaTime;
                else
                    stopTimer = 0f;

                if (stopTimer >= stopHoldTime)
                {
                    // prepare reverse curve
                    if (tryMoveAside && TryFindAsideDirection(out int side))
                    {
                        asideSide = side;
                        asideTarget = ComputeAsideTarget(asideSide);

                        // generate reverse curve path (smooth backwards arc) from current pos to asideTarget
                        reversePath = GenerateReverseCurve(transform.position, transform.forward, asideTarget, reverseBackDistance, reverseLateralBias, reverseCurveSamples);

                        // sanity check: ensure path doesn't immediately collide with player/obstacle
                        if (IsPathSafe(reversePath))
                        {
                            reverseIndex = 0;
                            state = State.ReversingCurve;
                            if (drawDebug) Debug.Log($"{name}: starting reversing along curve to side {asideSide}");
                        }
                        else
                        {
                            // try other side
                            if (TryFindAsideDirection(out int other) && other != asideSide)
                            {
                                asideSide = other;
                                asideTarget = ComputeAsideTarget(asideSide);
                                reversePath = GenerateReverseCurve(transform.position, transform.forward, asideTarget, reverseBackDistance, reverseLateralBias, reverseCurveSamples);
                                if (IsPathSafe(reversePath))
                                {
                                    reverseIndex = 0;
                                    state = State.ReversingCurve;
                                    if (drawDebug) Debug.Log($"{name}: reversing other side {asideSide}");
                                    break;
                                }
                            }

                            // if both blocked - wait and retry
                            state = State.Waiting;
                            if (drawDebug) Debug.Log($"{name}: reverse path blocked -> Waiting");
                        }
                    }
                    else
                    {
                        // no aside possible -> just wait (or could try small reverse)
                        state = State.Waiting;
                        if (drawDebug) Debug.Log($"{name}: no aside available -> Waiting");
                    }
                }
                break;

            case State.ReversingCurve:
                // follow reversePath points backwards (from index 0 up to last)
                if (reversePath == null || reversePath.Count == 0)
                {
                    state = State.Waiting;
                    break;
                }

                // follow path point-by-point
                ReverseFollowStep();

                // re-evaluate: if front becomes blocked behind or player overlaps, stop
                // (ReverseFollowStep handles collision checks for next movement)

                break;

            case State.MovingAside:
                // move to asideTarget then hold until clear, then resume driving
                if (IsPositionOccupied(asideTarget))
                {
                    ApplyHold();
                    if (drawDebug) Debug.Log($"{name}: aside target occupied -> holding");
                    break;
                }

                float avoid = ComputeAvoidanceOffset();
                MoveToAside(avoid);

                if (Vector3.Distance(transform.position, asideTarget) <= asideReachRadius)
                {
                    ApplyHold();
                    // wait for front to be clear before resuming
                    if (!IsEntityInFront(playerT) && !IsObstacleBlockingFront())
                    {
                        state = State.Driving;
                        if (drawDebug) Debug.Log($"{name}: aside complete & clear -> resume driving");
                    }
                }
                break;

            case State.Waiting:
                // holding until things clear
                ApplyHold();

                // if front is clear, attempt to resume or try to plan reverse again
                if (!frontThreat)
                {
                    state = State.Driving;
                    if (drawDebug) Debug.Log($"{name}: waiting ended -> resume driving");
                }
                else
                {
                    // try to find aside again
                    if (TryFindAsideDirection(out int d))
                    {
                        asideSide = d;
                        asideTarget = ComputeAsideTarget(asideSide);
                        reversePath = GenerateReverseCurve(transform.position, transform.forward, asideTarget, reverseBackDistance, reverseLateralBias, reverseCurveSamples);
                        if (IsPathSafe(reversePath))
                        {
                            reverseIndex = 0;
                            state = State.ReversingCurve;
                            if (drawDebug) Debug.Log($"{name}: found free aside -> reversingCurve");
                        }
                    }
                }
                break;
        }
    }

    // ---------------- spline driving ----------------
    void RebuildSpline()
    {
        splinePoints.Clear();
        if (waypoints == null || waypoints.Length < 2) return;

        int wpCount = waypoints.Length;
        for (int i = 0; i < wpCount; i++)
        {
            Transform p0 = waypoints[LoopIndex(i - 1)];
            Transform p1 = waypoints[i];
            Transform p2 = waypoints[LoopIndex(i + 1)];
            Transform p3 = waypoints[LoopIndex(i + 2)];

            if (!loopPath)
            {
                p0 = waypoints[Mathf.Max(0, i - 1)];
                p1 = waypoints[i];
                p2 = waypoints[Mathf.Min(wpCount - 1, i + 1)];
                p3 = waypoints[Mathf.Min(wpCount - 1, i + 2)];
            }

            for (int s = 0; s < splineResolution; s++)
            {
                float t = (float)s / (float)splineResolution;
                Vector3 pos = CatmullRom(p0.position, p1.position, p2.position, p3.position, t, curveTension);
                splinePoints.Add(pos);
            }
        }

        if (!loopPath)
            splinePoints.Add(waypoints[waypoints.Length - 1].position);
    }

    int LoopIndex(int i)
    {
        if (waypoints == null || waypoints.Length == 0) return 0;
        if (loopPath)
        {
            int n = waypoints.Length;
            return ((i % n) + n) % n;
        }
        else return Mathf.Clamp(i, 0, waypoints.Length - 1);
    }

    Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t, float tension)
    {
        float tt = t * t;
        float ttt = tt * t;
        Vector3 m1 = (1f - tension) * 0.5f * (p2 - p0);
        Vector3 m2 = (1f - tension) * 0.5f * (p3 - p1);
        Vector3 a = 2f * p1 - 2f * p2 + m1 + m2;
        Vector3 b = -3f * p1 + 3f * p2 - 2f * m1 - m2;
        Vector3 c = m1;
        Vector3 d = p1;
        return a * ttt + b * tt + c * t + d;
    }

    int FindClosestSplineIndexAhead()
    {
        if (splinePoints == null || splinePoints.Count == 0) return 0;
        float best = float.MaxValue; int idx = 0;
        for (int i = 0; i < splinePoints.Count; i++)
        {
            float d = Vector3.SqrMagnitude(splinePoints[i] - transform.position);
            if (d < best) { best = d; idx = i; }
        }
        return (idx + 3) % splinePoints.Count;
    }

    void DriveAlongSpline(float avoidOffset)
    {
        if (splinePoints == null || splinePoints.Count == 0)
        {
            DriveToWaypoint();
            return;
        }

        int idx = FindClosestSplineIndexAhead();
        Vector3 target = splinePoints[idx];
        Vector3 flat = Vector3.ProjectOnPlane(target - transform.position, Vector3.up);
        Vector3 forwardDir = (flat.magnitude > 0.001f) ? flat.normalized : transform.forward;
        Vector3 lateral = transform.right * Mathf.Clamp(avoidOffset, -1f, 1f) * 0.4f;
        Vector3 moveDir = (forwardDir + lateral).normalized;

        MoveInDirection(moveDir, moveSpeed);

        // advance waypoint if near its transform
        if (waypoints != null && waypoints.Length > 0)
        {
            if (Vector3.Distance(transform.position, waypoints[currentWaypoint].position) <= 0.8f)
            {
                currentWaypoint++;
                if (currentWaypoint >= waypoints.Length)
                {
                    if (loopPath) currentWaypoint = 0;
                    else currentWaypoint = waypoints.Length - 1;
                }
            }
        }
    }

    void DriveToWaypoint()
    {
        if (waypoints == null || waypoints.Length == 0) { ApplyHold(); return; }
        Vector3 to = waypoints[currentWaypoint].position - transform.position;
        Vector3 flat = Vector3.ProjectOnPlane(to, Vector3.up);
        if (flat.magnitude < 0.6f)
        {
            currentWaypoint++;
            if (currentWaypoint >= waypoints.Length)
            {
                if (loopPath) currentWaypoint = 0;
                else currentWaypoint = waypoints.Length - 1;
            }
            return;
        }
        MoveInDirection(flat.normalized, moveSpeed);
    }

    // ---------------- reverse curve generation & following ----------------
    List<Vector3> GenerateReverseCurve(Vector3 startPos, Vector3 startForward, Vector3 targetPos, float backDistance, float lateralBias, int samples)
    {
        // Build a cubic Bezier curve with:
        // P0 = startPos
        // P1 = startPos - startForward * backDistance + startRight * (lateralBias * sign)
        // P2 = targetPos + targetForward * (backDistance * 0.4f) + targetRight * (lateralBias*0.4f)
        // P3 = targetPos
        List<Vector3> path = new List<Vector3>();
        Vector3 right = transform.right;
        Vector3 startRight = right;
        Vector3 p0 = startPos;
        Vector3 p3 = targetPos;

        Vector3 p1 = p0 - startForward.normalized * backDistance + startRight * (lateralBias * Mathf.Sign((targetPos - startPos).x));
        // for p2 try to use vector from target to start (approx tangent)
        Vector3 targetForward = (startPos - targetPos).normalized;
        Vector3 targetRight = Quaternion.LookRotation(targetForward).eulerAngles == Vector3.zero ? transform.right : Vector3.Cross(Vector3.up, targetForward).normalized;
        Vector3 p2 = p3 + targetForward * (backDistance * 0.6f) + targetRight * (lateralBias * 0.4f);

        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / (float)samples;
            Vector3 pos = CubicBezier(p0, p1, p2, p3, t);
            path.Add(pos);
        }
        return path;
    }

    Vector3 CubicBezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
    {
        float u = 1f - t;
        return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
    }

    // Follow one step along reversePath: we move toward reversePath[reverseIndex],
    // orient car so it faces opposite the movement direction (like reversing).
    void ReverseFollowStep()
    {
        if (reversePath == null || reversePath.Count == 0) { state = State.Waiting; return; }

        // if index beyond end -> finished reversing; move to aside state
        if (reverseIndex >= reversePath.Count - 1)
        {
            // after reversing to final path point, switch to MovingAside (which will approach asideTarget slowly)
            state = State.MovingAside;
            if (drawDebug) Debug.Log($"{name}: Finished reverse curve, switching to MovingAside");
            return;
        }

        Vector3 target = reversePath[reverseIndex + 1]; // next sample point (we progress along increasing index)
        Vector3 dir = (target - rb.position);
        float dist = dir.magnitude;
        Vector3 moveDir = dist > 0.0001f ? dir.normalized : Vector3.zero;

        // check forward area (car's front) to avoid colliding player while reversing
        // while reversing, we want to ensure the rear path is clear - check a bit ahead in movement dir
        Vector3 checkPoint = rb.position + moveDir * 0.2f + Vector3.up * 0.5f;
        if (Physics.CheckSphere(checkPoint, sideClearanceRadius, obstacleMask))
        {
            // blocked -> stop and go to Waiting to avoid collision
            ApplyHold();
            state = State.Waiting;
            if (drawDebug) Debug.Log($"{name}: reverse path blocked -> Waiting");
            return;
        }

        // step toward target using reverseSpeed
        float step = reverseSpeed * Time.fixedDeltaTime;
        Vector3 newPos;
        if (dist <= step)
        {
            newPos = target;
            reverseIndex++;
        }
        else
        {
            newPos = rb.position + moveDir * step;
        }

        // rotation: when reversing, the visible 'rear' becomes forward: we want car to face opposite to movement direction
        if (moveDir.sqrMagnitude > 0.0001f)
        {
            Quaternion desired = Quaternion.LookRotation(-moveDir, Vector3.up); // face opposite movement (i.e., car faces backward)
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, desired, Mathf.Clamp01(rotateSpeed * Time.fixedDeltaTime)));
        }

        // Apply movement but make sure not to move into colliders
        if (!Physics.CheckSphere(newPos + Vector3.up * 0.5f, sideClearanceRadius, obstacleMask))
        {
            rb.MovePosition(newPos);
        }
        else
        {
            // blocked at newPos -> hold and switch to Waiting
            ApplyHold();
            state = State.Waiting;
            if (drawDebug) Debug.Log($"{name}: reverse step would collide -> Waiting");
        }
    }

    // Quick safety check ensuring no sample point sits inside a blocking collider (player/other)
    bool IsPathSafe(List<Vector3> path)
    {
        if (path == null || path.Count == 0) return false;
        foreach (var p in path)
        {
            if (Physics.CheckSphere(p + Vector3.up * 0.5f, sideClearanceRadius, obstacleMask))
                return false;
            // ensure not overlapping player specifically
            if (playerT != null)
            {
                if (Vector3.Distance(playerT.position, p) <= sideClearanceRadius + 0.3f) return false;
            }
        }
        return true;
    }

    // ---------------- aside / move ----------------
    Vector3 ComputeAsideTarget(int sideSign)
    {
        // pick a point slightly behind and laterally offset
        Vector3 basePoint = transform.position - transform.forward * asideForward;
        Vector3 aside = basePoint + transform.right * (sideSign * asideOffset);
        aside.y = transform.position.y;
        return aside;
    }

    void MoveToAside(float avoidOffset)
    {
        Vector3 toTarget = asideTarget - transform.position;
        Vector3 flat = Vector3.ProjectOnPlane(toTarget, Vector3.up);
        float dist = flat.magnitude;
        Vector3 moveDir = (flat.magnitude > 0.001f) ? flat.normalized : transform.forward;
        Vector3 lateralAvoid = transform.right * avoidOffset * 0.6f;
        moveDir = (moveDir + lateralAvoid).normalized;
        MoveInDirection(moveDir, Mathf.Max(1.5f, moveSpeed * 0.5f));
    }

    // ---------------- movement primitive ----------------
    void MoveInDirection(Vector3 dir, float targetSpd)
    {
        currentSpeed = Mathf.Lerp(currentSpeed, targetSpd, Mathf.Clamp01(accelLerp * Time.fixedDeltaTime));
        Vector3 delta = dir * currentSpeed * Time.fixedDeltaTime;
        Vector3 newPos = rb.position + delta;

        // safe overlap test
        if (Physics.CheckSphere(newPos + Vector3.up * 0.5f, sideClearanceRadius, obstacleMask))
        {
            // blocked -> don't move this frame
            currentSpeed = 0f;
            if (drawDebug) Debug.Log($"{name}: Move blocked -> holding");
            return;
        }
        rb.MovePosition(newPos);

        if (dir.sqrMagnitude > 0.0001f)
        {
            Quaternion desired = Quaternion.LookRotation(dir, Vector3.up);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, desired, Mathf.Clamp01(rotateSpeed * Time.fixedDeltaTime)));
        }
    }

    void ApplyHold()
    {
        currentSpeed = Mathf.Lerp(currentSpeed, 0f, Mathf.Clamp01(accelLerp * Time.fixedDeltaTime));
    }

    // ---------------- detection & avoidance ----------------
    // RaycastAll LOS check ignoring self
    bool IsEntityInFront(Transform entity)
    {
        if (entity == null) return false;
        Vector3 to = entity.position - transform.position;
        float dist = to.magnitude;
        if (dist > detectionDistance) return false;
        float ang = Vector3.Angle(transform.forward, to);
        if (ang > detectionAngle) return false;

        Vector3 origin = transform.position + Vector3.up * 0.6f;
        Vector3 dir = to.normalized;

        RaycastHit[] hits = Physics.RaycastAll(origin, dir, detectionDistance, detectionRayMask);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.transform == null) continue;
            if (h.transform.root == transform.root) continue;
            if (h.transform.root == entity.root) return true; // visible
            if (drawDebugRays) Debug.DrawRay(origin, dir * h.distance, Color.red);
            return false; // blocked by something else
        }
        // no hits -> treat visible
        return true;
    }

    bool IsObstacleBlockingFront()
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        Vector3 dir = transform.forward;
        RaycastHit[] hits = Physics.SphereCastAll(origin, sideClearanceRadius, dir, detectionDistance, obstacleMask);
        foreach (var h in hits)
        {
            if (h.transform == null) continue;
            if (h.transform.root == transform.root) continue;
            if (playerT != null && h.transform.root == playerT.root) continue;
            if (drawDebug) Debug.DrawRay(origin, dir * h.distance, Color.red);
            return true;
        }
        return false;
    }

    bool TryFindAsideDirection(out int dir)
    {
        if (CheckSideClear(1)) { dir = 1; return true; }
        if (CheckSideClear(-1)) { dir = -1; return true; }
        dir = 0; return false;
    }

    bool CheckSideClear(int sideSign)
    {
        Vector3 origin = transform.position + transform.forward * sideCheckForward + Vector3.up * 0.5f;
        Vector3 checkPoint = origin + transform.right * (sideSign * sideCheckDistance);
        Collider[] cols = Physics.OverlapSphere(checkPoint, sideClearanceRadius, obstacleMask);
        int found = 0;
        foreach (var c in cols)
        {
            if (c.transform.IsChildOf(transform)) continue;
            found++;
        }
        if (drawDebug) Debug.DrawLine(transform.position, checkPoint, found == 0 ? Color.green : Color.red);
        if (drawDebug) Debug.Log($"{name}: Side {sideSign} check found {found} colliders");
        return found == 0;
    }

    // single authoritative Occupied test
    bool IsPositionOccupied(Vector3 pos)
    {
        Collider[] cols = Physics.OverlapSphere(pos + Vector3.up * 0.5f, sideClearanceRadius, obstacleMask);
        foreach (var c in cols)
        {
            if (c.transform.IsChildOf(transform)) continue;
            if (playerT != null && c.transform.root == playerT.root) return true;
            return true;
        }
        return false;
    }

    // Check generated path sample points are safe
    // (already implemented above as IsPathSafe, reused where needed)
    // ---------------- avoidance offset ----------------
    float ComputeAvoidanceOffset()
    {
        float offset = 0f;
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        Vector3 dirLeft = Quaternion.Euler(0, -avoidRayAngle, 0) * transform.forward;
        Vector3 dirRight = Quaternion.Euler(0, avoidRayAngle, 0) * transform.forward;

        if (Physics.Raycast(origin, dirLeft, out RaycastHit hitL, avoidCheckDistance, obstacleMask))
        {
            float s = 1f - (hitL.distance / avoidCheckDistance);
            offset += s;
            if (drawDebug) Debug.DrawRay(origin, dirLeft * hitL.distance, Color.red);
        }
        else if (drawDebug) Debug.DrawRay(origin, dirLeft * avoidCheckDistance, Color.green);

        if (Physics.Raycast(origin, dirRight, out RaycastHit hitR, avoidCheckDistance, obstacleMask))
        {
            float s = 1f - (hitR.distance / avoidCheckDistance);
            offset -= s;
            if (drawDebug) Debug.DrawRay(origin, dirRight * hitR.distance, Color.red);
        }
        else if (drawDebug) Debug.DrawRay(origin, dirRight * avoidCheckDistance, Color.green);

        return Mathf.Clamp(offset * avoidLateralStrength, -1f, 1f);
    }

    // ---------------- utilities ----------------
    Transform FindPlayer()
    {
        GameObject go = GameObject.FindGameObjectWithTag(playerTag);
        return go ? go.transform : null;
    }

    // Cubic Bezier helper already added above
    // ---------------- Gizmos ----------------
    void OnDrawGizmosSelected()
    {
        if (!drawSplineGizmos) return;
        if (waypoints == null || waypoints.Length < 2) return;
        RebuildSpline();
        Gizmos.color = splineColor;
        for (int i = 0; i < splinePoints.Count - 1; i++)
            Gizmos.DrawLine(splinePoints[i], splinePoints[i + 1]);

        Gizmos.color = Color.cyan;
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;
            Gizmos.DrawWireSphere(waypoints[i].position, 0.25f);
            int nxt = (i + 1) % waypoints.Length;
            if (!loopPath && i == waypoints.Length - 1) break;
            if (waypoints[nxt] != null) Gizmos.DrawLine(waypoints[i].position, waypoints[nxt].position);
        }

        // draw side checks
        Gizmos.color = Color.magenta;
        Vector3 origin = transform.position + transform.forward * sideCheckForward + Vector3.up * 0.5f;
        Gizmos.DrawWireSphere(origin + transform.right * sideCheckDistance, sideClearanceRadius);
        Gizmos.DrawWireSphere(origin - transform.right * sideCheckDistance, sideClearanceRadius);

        // draw reverse path preview if exists
        if (reversePath != null && reversePath.Count > 1)
        {
            Gizmos.color = Color.red;
            for (int i = 0; i < reversePath.Count - 1; i++)
                Gizmos.DrawLine(reversePath[i], reversePath[i + 1]);
        }

        // draw aside target
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(asideTarget, asideReachRadius);
    }
}
