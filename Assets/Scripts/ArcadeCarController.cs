using UnityEngine;
using UnityEngine.UI; // required for UI Text / Image

[RequireComponent(typeof(Rigidbody))]
public class ArcadeCarController : MonoBehaviour
{
    public enum DriveType { RearWheelDrive, FrontWheelDrive, AllWheelDrive }

    [Header("Drive / Wheels (0:FL,1:FR,2:RL,3:RR)")]
    public DriveType driveType = DriveType.RearWheelDrive;
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    public Transform[] wheelMeshes = new Transform[4]; // optional visuals

    [Header("Torque / Speed")]
    public float motorTorqueForward = 900f;
    public float motorTorqueReverse = 1600f;
    public float maxSpeed = 22f;         // m/s - this is the speed mapped to the end of the dial
    public float maxReverseSpeed = 10f;  // m/s

    [Header("Braking")]
    public float brakeTorque = 1800f;
    public float autoBrakeTorque = 600f; // applied when no throttle

    [Header("Steering")]
    public float maxSteerAngle = 30f;
    [Range(0f,1f)] public float steerHelper = 0.6f;

    [Header("Cornering Stability")]
    [Tooltip("0 = pure physical slide (whatever the wheel friction curves produce). Higher values gently pull the car's actual velocity direction back toward where it's pointed, same idea as the cop AI's grip assist, just lighter by default so the player keeps some arcade slide feel. Runs every frame, unlike steerHelper above which only acts while actively steering.")]
    [Range(0f, 1f)] public float gripAssist = 0.3f;
    [Tooltip("Caps how fast the car can actually rotate (degrees/second) regardless of how hard the wheels are steered - this is what stops a single aggressive input from spinning the car out completely. Set high enough that normal hard cornering never touches it (only genuine spin-outs should).")]
    public float maxYawRate = 220f;
    [Tooltip("How quickly yaw rate gets pulled back toward maxYawRate once exceeded - higher = snappier correction.")]
    public float yawDampingRate = 6f;

    [Header("Auto-Drive (Smash Bandits style, one-hand play)")]
    [Tooltip("Car accelerates by itself - the player only steers. This is the Smash Bandits control scheme: one thumb, no throttle control.")]
    public bool autoDrive = true;
    [Tooltip("How much of full throttle the auto-driver holds (1 = pinned).")]
    [Range(0f, 1f)] public float autoDriveThrottle = 1f;
    [Tooltip("How much the auto-throttle eases off while steering hard - gives corners that slight speed dip that makes tight turns controllable with one thumb. 0 = no slowdown.")]
    [Range(0f, 1f)] public float autoCornerSlowdown = 0.35f;
    [Tooltip("If true, a negative ThrottleInput (holding S / down, or a touch brake button) still overrides auto-drive to brake/reverse - so a player pinned against a wall can back out. Forward input is always ignored in auto-drive.")]
    public bool allowManualReverse = true;

    [Header("Skid Detection")]
    [Tooltip("Sideways slip (drifting/cornering) above this emits marks.")]
    public float skidSlipThreshold = 0.20f;
    [Tooltip("Forward slip (wheelspin on launch, brake lockup) above this also emits marks - checking sideways slip alone misses burnouts and brake-lock skids entirely.")]
    public float forwardSlipThreshold = 0.35f;
    public float minHitForce = 5f;
    public float skidHoldTime = 0.12f;
    public float emissionFadeSpeed = 200f;
    [Tooltip("Also emit marks based on how hard the car is actually turning (yaw rate x speed), independent of physical wheel slip. Needed because SteerHelper above deliberately damps real tire slip for better handling - which otherwise starves the slip-based check during ordinary hard, fast cornering.")]
    public bool useCorneringHeuristic = true;
    public float corneringYawRateThreshold = 35f;
    public float corneringMinSpeed = 4f;

    [Header("Effects - Rear (0 = RL, 1 = RR)")]
    public TrailRenderer[] rearTrails = new TrailRenderer[2];
    public ParticleSystem[] rearSmokes = new ParticleSystem[2];
    public float smokeEmissionRate = 60f;

    [Header("Rigidbody")]
    [Tooltip("Unity's default Rigidbody mass (1) is far too light for the torque values above - it makes the car unstable and prone to spinning out. Real car-scale mass keeps the physics sane.")]
    public float vehicleMass = 1300f;
    [Tooltip("Linear damping (formerly 'Drag'). Keep this low - real deceleration should come from braking/friction, not artificial drag.")]
    public float linearDamping = 0.05f;
    [Tooltip("Angular damping (formerly 'Angular Drag'). Unity's default of 0.05 lets small torque imbalances spin the car up almost for free - this is a common cause of unwanted spinning/donuts.")]
    public float angularDamping = 3f;
    public Vector3 centerOfMassOffset = new Vector3(0f, -0.45f, 0f);
    public float downforce = 120f;

    [Header("Steer-start (nudges car when stationary + steering)")]
    public bool enableSteerStart = true;
    [Range(0f,1f)] public float steerStartThrottle = 0.45f;
    public float steerStartMaxSpeed = 0.6f;
    [Range(0f,1f)] public float steerStartMinInput = 0.15f;

    [Header("Speed UI - Digital")]
    public Text speedText;
    public bool showOneDecimal = false;
    public float speedConversion = 3.6f; // m/s -> km/h

    [Header("Speed UI - Needle & Radial Fill")]
    [Tooltip("RectTransform of the needle sprite (pivot should be at the base of the needle)")]
    public RectTransform needleTransform;
    [Tooltip("Image set to 'Filled' (radial) - this will fill with speed. Fill Method/Origin/Clockwise are set automatically from script to guarantee it matches the needle - Inspector values for those are overridden on Start.")]
    public Image speedFillImage;
    [Tooltip("Minimum rotation angle for needle (e.g. -120)")]
    public float needleMinAngle = -120f;
    [Tooltip("Maximum rotation angle for needle (e.g. 120)")]
    public float needleMaxAngle = 120f;
    [Tooltip("Compass-style angle, in the same units as needleMinAngle/needleMaxAngle, that the needle sprite points toward when its own rotation is 0. Default 90 assumes the needle art is drawn pointing straight up, which is the standard convention - change this if your needle art points a different direction at rest.")]
    public float needleRestAngle = 90f;
    [Tooltip("How quickly the needle & fill smoothly move (higher = faster)")]
    public float needleSmoothSpeed = 6f;

    // runtime
    Rigidbody rb;
    float inputSteer;
    float inputThrottle;
    float currentSpeed;

    // ---- External input (set by KeyboardCarInput, touch UI, network sync, or an AI brain like CopCarAI) ----
    // Nothing else in this class reads Input.* directly anymore - whatever sets these two
    // properties each frame IS the input source, whether that's a human or an AI.
    public float SteerInput { get; set; }
    public float ThrottleInput { get; set; }

    // ---- Read-only accessors for other systems (AI, UI, networking) ----
    public Rigidbody CarRigidbody => rb;
    public float CurrentSpeed => currentSpeed;
    public Vector3 Velocity => rb != null ? rb.linearVelocity : Vector3.zero;

    // skid timers and emission states per rear wheel
    private float[] skidTimers = new float[2];
    private float[] currentSmokeRate = new float[2];
    private float[] targetSmokeRate = new float[2];
    private bool[] skidWasActive = new bool[2];

    // smoothing state for UI - ONE shared value in 0..1 space. The needle
    // angle and the fill amount are both just simple functions of this same
    // number each frame, so they're structurally incapable of disagreeing
    // with each other (the old version smoothed each output independently
    // with two different curves - LerpAngle vs MoveTowards - sharing one
    // speed parameter, which let them drift apart during any acceleration).
    private float uiSpeedSmoothed = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = vehicleMass;
        rb.linearDamping = linearDamping;
        rb.angularDamping = angularDamping;
        rb.centerOfMass += centerOfMassOffset;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // ContinuousDynamic (not just Continuous) so collisions against OTHER moving
        // rigidbodies - cops ramming/PIT-ing the player - resolve reliably too, not
        // just collisions against static level geometry.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // initialize skid FX state
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

        // init UI smoother value
        uiSpeedSmoothed = 0f;

        // Force the two settings that are hard requirements for the math below to
        // be valid at all - NOT touching the Image's own transform/rotation, so
        // its position and pivot behave exactly as authored.
        if (speedFillImage != null)
        {
            speedFillImage.type = Image.Type.Filled;
            // Radial90/180 physically cap the visible sweep at 90/180deg - a wide
            // gauge range (e.g. 240deg) can only be represented at all with 360.
            speedFillImage.fillMethod = Image.FillMethod.Radial360;
            // This MUST match the needle's actual sweep direction as speed
            // increases, or the fill sweeps the wrong way entirely - not an
            // artistic choice, so it's derived rather than left to the Inspector.
            speedFillImage.fillClockwise = needleMaxAngle < needleMinAngle;
            // fillOrigin is intentionally left as whatever's already set in the
            // Inspector - UpdateSpeedUI reads it back each frame and computes
            // the exact fillAmount needed against it, so any origin works
            // correctly without ever rotating anything.
        }
    }

    void Update()
    {
        // Input no longer comes from here directly - it's set externally via
        // SteerInput / ThrottleInput (see KeyboardCarInput for the default local
        // keyboard source, or drive these from touch UI / network state / an AI
        // brain like CopCarAI instead - same physics, any input source).
        inputSteer = Mathf.Clamp(SteerInput, -1f, 1f);

        if (autoDrive)
        {
            float external = Mathf.Clamp(ThrottleInput, -1f, 1f);
            if (allowManualReverse && external < -0.01f)
            {
                // Brake/reverse override so a player pinned against a wall can back
                // out - the ONLY case where player throttle matters in auto-drive.
                inputThrottle = external;
            }
            else
            {
                // Smash Bandits style: always driving, easing off slightly in hard
                // turns so single-thumb steering stays controllable.
                inputThrottle = autoDriveThrottle * (1f - autoCornerSlowdown * Mathf.Abs(inputSteer));
            }
        }
        else
        {
            inputThrottle = Mathf.Clamp(ThrottleInput, -1f, 1f);
        }

        UpdateWheelMeshes();

        // update digital speed text here for smooth display
        UpdateSpeedUI();
    }

    void FixedUpdate()
    {
        currentSpeed = rb.linearVelocity.magnitude;

        ApplySteer();
        ApplyMotorAndBrakes();

        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        SteerHelper();
        ApplyCorneringStability();
        UpdateSkidEffects();
        UpdateSmokeEmissionSmoothing();
    }

    void ApplySteer()
    {
        float speedFactor = Mathf.Clamp01(1f - (currentSpeed / (maxSpeed + 0.1f)));
        float steer = maxSteerAngle * inputSteer * (0.5f + 0.5f * speedFactor);

        if (wheelColliders != null && wheelColliders.Length >= 2)
        {
            wheelColliders[0].steerAngle = steer;
            wheelColliders[1].steerAngle = steer;
        }
    }

    void ApplyMotorAndBrakes()
    {
        if (wheelColliders == null || wheelColliders.Length < 4) return;

        float velForward = Vector3.Dot(rb.linearVelocity, transform.forward);
        float throttle = inputThrottle; // -1..1

        bool steerStartActive = enableSteerStart
                                && Mathf.Abs(throttle) < 0.01f
                                && rb.linearVelocity.magnitude < steerStartMaxSpeed
                                && Mathf.Abs(inputSteer) >= steerStartMinInput;

        float appliedMotor = 0f;
        if (steerStartActive)
        {
            appliedMotor = motorTorqueForward * steerStartThrottle * Mathf.Clamp01(Mathf.Abs(inputSteer));
        }
        else
        {
            if (throttle > 0f)
            {
                if (velForward < maxSpeed) appliedMotor = throttle * motorTorqueForward;
            }
            else if (throttle < 0f)
            {
                if (velForward > -maxReverseSpeed || Mathf.Abs(velForward) < 0.7f)
                    appliedMotor = throttle * motorTorqueReverse;
            }
        }

        switch (driveType)
        {
            case DriveType.RearWheelDrive:
                wheelColliders[2].motorTorque = appliedMotor;
                wheelColliders[3].motorTorque = appliedMotor;
                wheelColliders[0].motorTorque = 0f;
                wheelColliders[1].motorTorque = 0f;
                break;
            case DriveType.FrontWheelDrive:
                wheelColliders[0].motorTorque = appliedMotor;
                wheelColliders[1].motorTorque = appliedMotor;
                wheelColliders[2].motorTorque = 0f;
                wheelColliders[3].motorTorque = 0f;
                break;
            case DriveType.AllWheelDrive:
                for (int i = 0; i < 4; i++) wheelColliders[i].motorTorque = appliedMotor;
                break;
        }

        bool playerBraking = false;
        if (!steerStartActive && Mathf.Abs(throttle) > 0.01f)
        {
            if (Mathf.Sign(throttle) * velForward < -0.1f && Mathf.Abs(velForward) > 0.4f)
                playerBraking = true;
        }

        float appliedBrake = playerBraking ? brakeTorque : 0f;

        if (!steerStartActive && Mathf.Abs(throttle) < 0.01f)
        {
            float speedFactorBrake = Mathf.Clamp01(rb.linearVelocity.magnitude / (maxSpeed * 0.5f));
            appliedBrake = Mathf.Lerp(0f, autoBrakeTorque, speedFactorBrake);
        }

        for (int i = 0; i < wheelColliders.Length; i++)
            wheelColliders[i].brakeTorque = appliedBrake;
    }

    void SteerHelper()
    {
        if (Mathf.Abs(inputSteer) > 0.01f && rb.linearVelocity.magnitude > 0.1f)
        {
            Vector3 vel = rb.linearVelocity;
            Vector3 localVel = transform.InverseTransformDirection(vel);
            localVel.x *= 1f - steerHelper * (rb.linearVelocity.magnitude / (maxSpeed + 0.1f));
            rb.linearVelocity = transform.TransformDirection(localVel);
        }
    }

    void ApplyCorneringStability()
    {
        // Grip assist: gently pulls the car's actual velocity direction back
        // toward where it's pointed - runs every frame (unlike SteerHelper
        // above, which only acts while actively steering), so it also helps
        // the moment the player lets go of steering mid-slide instead of
        // leaving the car drifting freely until a new steer input arrives.
        if (gripAssist > 0f && rb.linearVelocity.magnitude > 0.3f)
        {
            Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
            localVel.x = Mathf.Lerp(localVel.x, 0f, gripAssist * Time.fixedDeltaTime * 6f);
            rb.linearVelocity = transform.TransformDirection(localVel);
        }

        // Yaw rate cap: stops a single hard input (or a hard hit) from
        // spinning the car out completely, regardless of what the wheel
        // friction curves alone would produce. Only engages once actual spin
        // exceeds maxYawRate, so ordinary hard cornering never feels capped.
        float yawRateDeg = rb.angularVelocity.y * Mathf.Rad2Deg;
        if (Mathf.Abs(yawRateDeg) > maxYawRate)
        {
            float target = Mathf.Sign(yawRateDeg) * maxYawRate;
            float newYawDeg = Mathf.Lerp(yawRateDeg, target, yawDampingRate * Time.fixedDeltaTime);
            Vector3 av = rb.angularVelocity;
            av.y = newYawDeg * Mathf.Deg2Rad;
            rb.angularVelocity = av;
        }
    }

    Vector3 GetWheelContactPoint(int wheelIndex)
    {
        if (wheelColliders == null || wheelIndex < 0 || wheelIndex >= wheelColliders.Length)
            return transform.position;

        WheelCollider wc = wheelColliders[wheelIndex];
        WheelHit hit;
        if (wc.GetGroundHit(out hit))
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
            // Forward slip counts too - checking sideways slip alone misses
            // wheelspin launches and brake-lock skids entirely.
            return hit.force > minHitForce
                   && (Mathf.Abs(hit.sidewaysSlip) > skidSlipThreshold
                       || Mathf.Abs(hit.forwardSlip) > forwardSlipThreshold);
        }
        return false;
    }

    void UpdateSkidEffects()
    {
        // Global cornering signal, independent of physical wheel slip. SteerHelper
        // above deliberately damps real slip for better handling, which otherwise
        // starves the physics-based check during an ordinary hard, fast turn.
        float yawRate = Mathf.Abs(rb.angularVelocity.y) * Mathf.Rad2Deg;
        bool corneringHard = useCorneringHeuristic
                              && rb.linearVelocity.magnitude > corneringMinSpeed
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
                // Clear on the rising edge only, so restarting a trail never draws
                // a stray line back to wherever it was last left sitting.
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
            Vector3 pos;
            Quaternion rot;
            wheelColliders[i].GetWorldPose(out pos, out rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    // Maps Unity's 4 fixed radial fill origins to their absolute angle in the
    // same standard-angle convention used for needleRestAngle/needleMinAngle/
    // needleMaxAngle (0=right, 90=up, 180=left, 270/-90=down).
    static float FillOriginToAngle(Image.Origin360 origin)
    {
        switch (origin)
        {
            case Image.Origin360.Bottom: return -90f;
            case Image.Origin360.Right: return 0f;
            case Image.Origin360.Top: return 90f;
            case Image.Origin360.Left: return 180f;
            default: return -90f;
        }
    }

    // UI update (called from Update)
    void UpdateSpeedUI()
    {
        if (rb == null) return;

        // use magnitude so speed always positive; clamp to maxSpeed for dial mapping
        float vehicleSpeed = Mathf.Clamp01(rb.linearVelocity.magnitude / Mathf.Max(0.0001f, maxSpeed));
        float displaySpeed = rb.linearVelocity.magnitude * speedConversion;

        // digital text
        if (speedText != null)
        {
            if (showOneDecimal) speedText.text = $"{displaySpeed:F1}";
            else speedText.text = $"{Mathf.RoundToInt(displaySpeed)}";
        }

        // needle & fill mapping - both derive from ONE smoothed 0..1 value below,
        // so they are always showing the exact same underlying number and can
        // never visually disagree with each other.
        uiSpeedSmoothed = Mathf.MoveTowards(uiSpeedSmoothed, vehicleSpeed, needleSmoothSpeed * Time.deltaTime);

        float needleAngle = Mathf.Lerp(needleMinAngle, needleMaxAngle, uiSpeedSmoothed);

        // apply rotation to needle (around Z)
        if (needleTransform != null)
        {
            needleTransform.localEulerAngles = new Vector3(0f, 0f, needleAngle);
        }

        // apply fill to image (Image.type must be Filled)
        if (speedFillImage != null)
        {
            // Compute fillAmount directly from the needle's actual absolute angle,
            // measured against whatever fillOrigin is set in the Inspector - this
            // needs no rotation of the image at all, since fillOrigin is just a
            // math reference point, not something that has to visually line up
            // with anything.
            float absNeedleAngle = needleRestAngle + needleAngle;
            float originAngle = FillOriginToAngle((Image.Origin360)speedFillImage.fillOrigin);

            float delta = speedFillImage.fillClockwise
                ? (originAngle - absNeedleAngle)
                : (absNeedleAngle - originAngle);

            speedFillImage.fillAmount = Mathf.Repeat(delta / 360f, 1f);
        }
    }

#if UNITY_EDITOR
    // Press K in Play mode to log rear slips/force (useful for tuning skidSlipThreshold / forwardSlipThreshold)
    void OnGUI()
    {
        if (Event.current != null && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.K)
        {
            for (int i = 2; i <= 3; i++)
            {
                WheelHit h;
                if (wheelColliders[i].GetGroundHit(out h))
                    Debug.Log($"Wheel {i} sideways={h.sidewaysSlip:F3} forward={h.forwardSlip:F3} force={h.force:F1}");
                else
                    Debug.Log($"Wheel {i} not grounded");
            }
        }
    }
#endif
}