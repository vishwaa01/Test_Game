using UnityEngine;

// ==================================================================
// CAR SKID EFFECTS - shared drift/skid marks + tire smoke
// ------------------------------------------------------------------
// One FX component for ANY WheelCollider car - put it on the player
// (replacing the old built-in FX in ArcadeCarController) and on every
// cop. Wire the wheel colliders you want marks from (usually just the
// rear pair) with index-matched TrailRenderers / ParticleSystems.
//
// Detection improvements over the old built-in player logic:
//  - Forward slip now counts too. The old code only looked at
//    sideways slip, so burnouts, wheelspin launches, and brake-lock
//    skids never emitted anything - only mid-drift marks worked,
//    which is why trails seemed to "not emit properly."
//  - A cornering heuristic (yaw rate x speed) ALSO triggers marks,
//    independent of physical wheel slip entirely. This matters because
//    both car controllers in this project (SteerHelper on the player,
//    gripAssist on cops) are deliberately designed to damp out real
//    tire slip for better handling - so keying purely off slip means
//    the very systems making the cars feel good were also strangling
//    the signal the trail needs. A hard, fast turn has high yaw rate
//    whether or not the physics calls it "slipping."
//  - A hold timer so marks don't flicker on/off every frame right at
//    the threshold edge, and trails now Clear() on the rising edge
//    only, so restarting a trail never draws a stray line back to
//    wherever it was last left sitting (the other half of "not
//    emitting properly").
//
// TrailRenderer setup checklist (common reasons trails look broken):
//  - Trail objects should be children of the CAR BODY, not of the
//    spinning wheel meshes (a spinning parent corkscrews the trail).
//  - TrailRenderer.time >= ~2s or marks vanish almost immediately.
//  - Min Vertex Distance ~0.1, Autodestruct OFF.
//  - This script owns the 'emitting' flag at runtime - its state in
//    the Inspector doesn't matter.
// ==================================================================

[RequireComponent(typeof(Rigidbody))]
public class CarSkidEffects : MonoBehaviour
{
    [Header("Wheels (usually just the rear pair)")]
    public WheelCollider[] wheels = new WheelCollider[2];

    [Header("FX - index-matched to the wheels above")]
    public TrailRenderer[] trails = new TrailRenderer[2];
    public ParticleSystem[] smokes = new ParticleSystem[2];

    [Header("Detection - physical wheel slip")]
    [Tooltip("Sideways slip (drifting/cornering) above this emits marks.")]
    public float sidewaysSlipThreshold = 0.18f;
    [Tooltip("Forward slip (wheelspin on launch, brake lockup) above this emits marks. Forward slip runs numerically higher than sideways slip, hence the higher default.")]
    public float forwardSlipThreshold = 0.35f;
    [Tooltip("Ignore ground contacts lighter than this (filters out glancing/airborne touches).")]
    public float minGroundForce = 4f;

    [Header("Detection - cornering heuristic (cosmetic)")]
    [Tooltip("Also emit marks based on how hard the car is actually turning (yaw rate x speed), independent of wheel-physics slip. Turn this OFF if you want marks to reflect true tire slip only.")]
    public bool useCorneringHeuristic = true;
    [Tooltip("Degrees/second of yaw rotation above which a fast car counts as 'cornering hard' for mark purposes.")]
    public float corneringYawRateThreshold = 35f;
    public float corneringMinSpeed = 4f;

    [Tooltip("Keeps FX alive briefly after slip ends so marks don't flicker on/off every frame at the threshold edge.")]
    public float holdTime = 0.15f;

    [Header("Smoke")]
    public float maxSmokeRate = 60f;
    public float smokeFadeSpeed = 200f;

    Rigidbody rb;
    float[] timers;
    float[] currentRate;
    float[] targetRate;
    bool[] wasActive;

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        int n = wheels.Length;
        timers = new float[n];
        currentRate = new float[n];
        targetRate = new float[n];
        wasActive = new bool[n];

        for (int i = 0; i < n; i++)
        {
            if (i < trails.Length && trails[i] != null)
                trails[i].emitting = false;

            if (i < smokes.Length && smokes[i] != null)
            {
                var em = smokes[i].emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                smokes[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }

#if UNITY_EDITOR
            if (i < trails.Length && trails[i] != null && trails[i].time < 0.5f)
                Debug.LogWarning($"{name}: CarSkidEffects trail {i} has time={trails[i].time:F2}s - marks will vanish almost instantly. Set TrailRenderer.time to ~2-5s.", trails[i]);
#endif
        }
    }

    void FixedUpdate()
    {
        // Computed once per frame, not per-wheel - a single global "is this car
        // carving a hard corner right now" signal applied to every wheel in the
        // array, so both rear tires mark together during a turn as expected.
        float speed = rb.linearVelocity.magnitude;
        float yawRate = Mathf.Abs(rb.angularVelocity.y) * Mathf.Rad2Deg;
        bool corneringHard = useCorneringHeuristic && speed > corneringMinSpeed && yawRate > corneringYawRateThreshold;

        for (int i = 0; i < wheels.Length; i++)
        {
            WheelCollider wc = wheels[i];
            if (wc == null) continue;

            bool slipping = corneringHard;
            Vector3 contact = wc.transform.position - transform.up * wc.radius;

            if (wc.GetGroundHit(out WheelHit hit))
            {
                contact = hit.point;
                slipping = slipping
                           || (hit.force > minGroundForce
                               && (Mathf.Abs(hit.sidewaysSlip) > sidewaysSlipThreshold
                                   || Mathf.Abs(hit.forwardSlip) > forwardSlipThreshold));
            }

            timers[i] = slipping ? holdTime : Mathf.Max(0f, timers[i] - Time.fixedDeltaTime);
            bool active = timers[i] > 0f;

            if (i < trails.Length && trails[i] != null)
            {
                // Clear on the rising edge only, so restarting a trail never draws
                // a stray line back to wherever it was last left sitting.
                if (active && !wasActive[i]) trails[i].Clear();

                // tiny lift so the mark doesn't z-fight with the road surface
                trails[i].transform.position = contact + transform.up * 0.02f;
                trails[i].emitting = active;
            }

            if (i < smokes.Length && smokes[i] != null)
            {
                smokes[i].transform.position = contact;
                targetRate[i] = active ? maxSmokeRate : 0f;

                if (active && !smokes[i].isPlaying)
                    smokes[i].Play(true);

                currentRate[i] = Mathf.MoveTowards(currentRate[i], targetRate[i], smokeFadeSpeed * Time.fixedDeltaTime);
                var em = smokes[i].emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(currentRate[i]);

                if (currentRate[i] <= 0.001f && targetRate[i] == 0f && smokes[i].isPlaying)
                    smokes[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }

            wasActive[i] = active;
        }
    }
}