using UnityEngine;

// ==================================================================
// COP CAR CONTROLLER - dedicated pursuit-vehicle physics
// ------------------------------------------------------------------
// Deliberately NOT the same handling model as ArcadeCarController.
// A chase AI needs to be able to hold a fast, stable, predictable line
// while something else drives it, so this trades a bit of the player's
// looser arcade slide for tighter grip, a sharper/faster steering
// response, and - importantly - a hard brake channel (BrakeInput) that
// is completely independent from the throttle logic. That lets the AI
// slam the brakes on instantly for obstacle avoidance without fighting
// whatever the "desired speed" logic is currently asking for.
//
// Still drives on real WheelColliders - wheel spin, suspension travel
// and skid marks all work the same way as the player car. Only the
// tuning and the input surface differ.
// ==================================================================

[RequireComponent(typeof(Rigidbody))]
public class CopCarController : MonoBehaviour
{
    [Header("Wheels (0:FL,1:FR,2:RL,3:RR)")]
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    public Transform[] wheelMeshes = new Transform[4];

    [Header("Torque / Speed")]
    public float motorTorqueForward = 1400f;
    public float motorTorqueReverse = 900f;
    [Tooltip("Deliberately a bit above a typical player top speed so cops can actually keep pace.")]
    public float topSpeed = 30f;
    public float maxReverseSpeed = 8f;

    [Header("Braking")]
    [Tooltip("Torque applied when BrakeInput is used - this is the AI's independent emergency-stop channel.")]
    public float hardBrakeTorque = 3200f;
    public float autoBrakeTorque = 500f;

    [Header("Steering")]
    [Tooltip("Sharper lock than the player car - GTA-style cops corner tight.")]
    public float maxSteerAngle = 36f;
    [Tooltip("How fast the actual wheel angle chases the requested steer angle (higher = snappier).")]
    [Range(0.5f, 30f)] public float steerResponse = 10f;

    [Header("Grip / Stability")]
    [Tooltip("0 = slides like the player car, 1 = glued to the direction it's facing. Real cop AI rarely spins out on its own - keep this fairly high.")]
    [Range(0f, 1f)] public float gripAssist = 0.75f;

    [Header("Skid Detection")]
    [Tooltip("Sideways slip (drifting/cornering) above this emits marks.")]
    public float skidSlipThreshold = 0.20f;
    [Tooltip("Forward slip (wheelspin, brake lockup) above this also emits marks.")]
    public float forwardSlipThreshold = 0.35f;
    public float minHitForce = 5f;
    public float skidHoldTime = 0.12f;
    public float emissionFadeSpeed = 200f;
    [Tooltip("Also emit marks based on how hard the car is actually turning (yaw rate x speed), independent of physical wheel slip. Needed because gripAssist above deliberately damps real tire slip to keep AI driving stable - which otherwise starves the slip-based check during a hard, fast pursuit turn.")]
    public bool useCorneringHeuristic = true;
    public float corneringYawRateThreshold = 35f;
    public float corneringMinSpeed = 4f;

    [Header("Effects - Rear (0 = RL, 1 = RR)")]
    public TrailRenderer[] rearTrails = new TrailRenderer[2];
    public ParticleSystem[] rearSmokes = new ParticleSystem[2];
    public float smokeEmissionRate = 60f;

    [Header("Rigidbody")]
    [Tooltip("A bit heavier than the player car by default - a cop that feels 'solid' on contact sells the ramming/PIT better, and Unity's default mass of 1 is far too light for these torque values regardless.")]
    public float vehicleMass = 1500f;
    public float linearDamping = 0.05f;
    [Tooltip("Higher than the player car - cops should feel more resistant to being spun out by a stray hit, and this pairs with gripAssist to keep AI-driven handling predictable.")]
    public float angularDamping = 4f;
    public Vector3 centerOfMassOffset = new Vector3(0f, -0.5f, 0f);
    public float downforce = 140f;

    // ---- Input surface (written by CopCarAI) ----
    public float SteerInput { get; set; }     // -1..1
    public float ThrottleInput { get; set; }  // -1..1, negative = reverse
    public float BrakeInput { get; set; }     // 0..1, independent hard-stop channel, separate from throttle

    Rigidbody rb;
    float currentSteerAngle;
    float currentSpeed;

    public Rigidbody CarRigidbody => rb;
    public float CurrentSpeed => currentSpeed;
    public Vector3 Velocity => rb != null ? rb.linearVelocity : Vector3.zero;

    private float[] skidTimers = new float[2];
    private float[] currentSmokeRate = new float[2];
    private float[] targetSmokeRate = new float[2];
    private bool[] skidWasActive = new bool[2];

    // Called by CopCarAI.ResetForSpawn right after a pooled cop is
    // repositioned - clears physics and skid-FX state so a recycled instance
    // doesn't carry stale velocity or an active skid mark into its new spot.
    public void ResetForSpawn()
    {
        SteerInput = 0f;
        ThrottleInput = 0f;
        BrakeInput = 0f;

        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        for (int i = 0; i < 2; i++)
        {
            skidTimers[i] = 0f;
            currentSmokeRate[i] = 0f;
            targetSmokeRate[i] = 0f;
            skidWasActive[i] = false;

            if (rearTrails != null && rearTrails.Length > i && rearTrails[i] != null)
                rearTrails[i].emitting = false;

            if (rearSmokes != null && rearSmokes.Length > i && rearSmokes[i] != null)
                rearSmokes[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = vehicleMass;
        rb.linearDamping = linearDamping;
        rb.angularDamping = angularDamping;
        rb.centerOfMass += centerOfMassOffset;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // ContinuousDynamic so ramming/PIT contact against the player's (also moving)
        // rigidbody resolves reliably, not just contact against static level geometry.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        for (int i = 0; i < 2; i++)
        {
            skidTimers[i] = 0f;
            currentSmokeRate[i] = 0f;
            targetSmokeRate[i] = 0f;
            skidWasActive[i] = false;

            if (rearTrails != null && rearTrails.Length > i && rearTrails[i] != null)
                rearTrails[i].emitting = false;

            if (rearSmokes != null && rearSmokes.Length > i && rearSmokes[i] != null)
            {
                var em = rearSmokes[i].emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                rearSmokes[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }

#if UNITY_EDITOR
            if (rearTrails != null && rearTrails.Length > i && rearTrails[i] != null && rearTrails[i].time < 0.5f)
                Debug.LogWarning($"{name}: rearTrails[{i}] has time={rearTrails[i].time:F2}s - marks will vanish almost instantly. Set TrailRenderer.time to ~2-5s.", rearTrails[i]);
#endif
        }
    }

    void Update()
    {
        UpdateWheelMeshes();
    }

    void FixedUpdate()
    {
        currentSpeed = rb.linearVelocity.magnitude;

        ApplySteer();
        ApplyDrive();
        ApplyGripAssist();

        rb.AddForce(-transform.up * downforce * currentSpeed);

        UpdateSkidEffects();
        UpdateSmokeEmissionSmoothing();
    }

    void ApplySteer()
    {
        float speedFactor = Mathf.Clamp01(1f - currentSpeed / (topSpeed + 5f));
        // Cops keep more steering authority at speed than the player does (0.65 floor vs 0.5) - tighter high-speed cornering.
        float targetAngle = maxSteerAngle * Mathf.Clamp(SteerInput, -1f, 1f) * (0.65f + 0.35f * speedFactor);
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetAngle, steerResponse * maxSteerAngle * Time.fixedDeltaTime);

        if (wheelColliders != null && wheelColliders.Length >= 2)
        {
            wheelColliders[0].steerAngle = currentSteerAngle;
            wheelColliders[1].steerAngle = currentSteerAngle;
        }
    }

    void ApplyDrive()
    {
        if (wheelColliders == null || wheelColliders.Length < 4) return;

        float velForward = Vector3.Dot(rb.linearVelocity, transform.forward);
        float throttle = Mathf.Clamp(ThrottleInput, -1f, 1f);

        float appliedMotor = 0f;
        if (throttle > 0f && velForward < topSpeed)
            appliedMotor = throttle * motorTorqueForward;
        else if (throttle < 0f && (velForward > -maxReverseSpeed || Mathf.Abs(velForward) < 0.7f))
            appliedMotor = throttle * motorTorqueReverse;

        for (int i = 0; i < 4; i++)
            wheelColliders[i].motorTorque = appliedMotor;

        float appliedBrake = hardBrakeTorque * Mathf.Clamp01(BrakeInput);

        // Light auto-brake on a truly neutral input, same idea as the player car, but it
        // never overrides an explicit BrakeInput request from the AI.
        if (Mathf.Abs(throttle) < 0.01f && BrakeInput < 0.01f)
        {
            float speedFactorBrake = Mathf.Clamp01(currentSpeed / (topSpeed * 0.5f));
            appliedBrake = Mathf.Lerp(0f, autoBrakeTorque, speedFactorBrake);
        }

        for (int i = 0; i < wheelColliders.Length; i++)
            wheelColliders[i].brakeTorque = appliedBrake;
    }

    void ApplyGripAssist()
    {
        if (gripAssist <= 0f || currentSpeed < 0.3f) return;

        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        localVel.x = Mathf.Lerp(localVel.x, 0f, gripAssist * Time.fixedDeltaTime * 6f);
        rb.linearVelocity = transform.TransformDirection(localVel);
    }

    Vector3 GetWheelContactPoint(int wheelIndex)
    {
        if (wheelColliders == null || wheelIndex < 0 || wheelIndex >= wheelColliders.Length)
            return transform.position;

        WheelCollider wc = wheelColliders[wheelIndex];
        if (wc.GetGroundHit(out WheelHit hit))
            return hit.point;

        if (wheelMeshes != null && wheelMeshes.Length > wheelIndex && wheelMeshes[wheelIndex] != null)
            return wheelMeshes[wheelIndex].position;

        return wc.transform.position - transform.up * wc.radius;
    }

    bool RawWheelSkid(int wheelIdx, out WheelHit hit)
    {
        hit = new WheelHit();
        if (wheelColliders == null || wheelIdx < 0 || wheelIdx >= wheelColliders.Length) return false;
        WheelCollider wc = wheelColliders[wheelIdx];
        if (wc.GetGroundHit(out hit))
        {
            return hit.force > minHitForce
                   && (Mathf.Abs(hit.sidewaysSlip) > skidSlipThreshold
                       || Mathf.Abs(hit.forwardSlip) > forwardSlipThreshold);
        }
        return false;
    }

    void UpdateSkidEffects()
    {
        // Global cornering signal, independent of physical wheel slip - gripAssist
        // deliberately damps real slip to keep AI driving stable and predictable,
        // which otherwise starves the slip-based check during a hard pursuit turn.
        float yawRate = Mathf.Abs(rb.angularVelocity.y) * Mathf.Rad2Deg;
        bool corneringHard = useCorneringHeuristic
                              && currentSpeed > corneringMinSpeed
                              && yawRate > corneringYawRateThreshold;

        bool rawRL = RawWheelSkid(2, out WheelHit hitRL) || corneringHard;
        bool rawRR = RawWheelSkid(3, out WheelHit hitRR) || corneringHard;

        skidTimers[0] = rawRL ? skidHoldTime : Mathf.Max(0f, skidTimers[0] - Time.fixedDeltaTime);
        skidTimers[1] = rawRR ? skidHoldTime : Mathf.Max(0f, skidTimers[1] - Time.fixedDeltaTime);

        bool skidActiveRL = skidTimers[0] > 0f;
        bool skidActiveRR = skidTimers[1] > 0f;
        bool[] activeNow = { skidActiveRL, skidActiveRR };

        for (int i = 0; i < 2; i++)
        {
            int wheelIndex = 2 + i;
            Vector3 contact = GetWheelContactPoint(wheelIndex);

            if (rearTrails != null && i < rearTrails.Length && rearTrails[i] != null)
            {
                if (activeNow[i] && !skidWasActive[i]) rearTrails[i].Clear();

                rearTrails[i].transform.position = contact;
                rearTrails[i].emitting = activeNow[i];
            }

            if (rearSmokes != null && i < rearSmokes.Length && rearSmokes[i] != null)
            {
                rearSmokes[i].transform.position = contact;
                targetSmokeRate[i] = activeNow[i] ? smokeEmissionRate : 0f;
                if (targetSmokeRate[i] > 0f && !rearSmokes[i].isPlaying) rearSmokes[i].Play(true);
            }

            skidWasActive[i] = activeNow[i];
        }
    }

    void UpdateSmokeEmissionSmoothing()
    {
        for (int i = 0; i < 2; i++)
        {
            currentSmokeRate[i] = Mathf.MoveTowards(currentSmokeRate[i], targetSmokeRate[i], emissionFadeSpeed * Time.fixedDeltaTime);

            if (rearSmokes != null && i < rearSmokes.Length && rearSmokes[i] != null)
            {
                var em = rearSmokes[i].emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(currentSmokeRate[i]);

                if (currentSmokeRate[i] <= 0.001f && targetSmokeRate[i] == 0f && rearSmokes[i].isPlaying)
                {
                    rearSmokes[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }
    }

    void UpdateWheelMeshes()
    {
        if (wheelMeshes == null || wheelColliders == null) return;
        for (int i = 0; i < Mathf.Min(wheelMeshes.Length, wheelColliders.Length); i++)
        {
            if (wheelMeshes[i] == null || wheelColliders[i] == null) continue;
            wheelColliders[i].GetWorldPose(out Vector3 pos, out Quaternion rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }
}