using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    [Header("References")]
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    public Transform[] wheelMeshes = new Transform[4];

    [Header("Effects")]
    public ParticleSystem[] tireSmoke = new ParticleSystem[4];
    public TrailRenderer[] skidTrails = new TrailRenderer[4];
    public float skidThreshold = 0.25f; // lower to detect skids earlier on mobile
    public float smokeEmissionRate = 60f; // particles per second when fully skidding

    [Header("Drive")]
    public bool rearWheelDrive = true;
    public bool frontWheelDrive = false;
    public float maxMotorTorque = 4500f; // increased so car feels responsive on mobile
    public float maxBrakeTorque = 5000f;
    // multiplier applied when reversing to make reverse input more responsive
    public float reverseTorqueMultiplier = 1.6f;

    [Header("Steering")]
    public float maxSteerAngle = 38f;
    public float steerSpeed = 12f; // used as responsiveness factor (higher = snappier)
    public float steeringSensitivity = 1.2f; // multiplier for joystick world-direction mapping
    public float steeringReductionAtTopSpeed = 0.45f; // how much steering is reduced at top speed
    public float steeringResponseCurve = 1.05f; // >1 = more aggressive near edges

    [Header("Stability & Feel")]
    public Vector3 centerOfMassOffset = new Vector3(0, -0.6f, 0);
    public float antiRoll = 5000f;
    public float downforce = 60f;
    public float topSpeedKph = 220f;

    [Header("Tuning")]
    public float motorTorqueCurve = 1.0f;

    [Header("Traction Control")]
    public bool enableTractionControl = true;
    public float slipLimit = 0.25f;
    [Range(0f, 1f)] public float tractionControlStrength = 0.6f; // stronger correction

    [Header("Stability Control")]
    public bool enableStabilityControl = true;
    public float stabilityStrength = 0.9f; // higher = more correction torque

    [Header("Mobile / Joystick")]
    public bool useVirtualJoystick = true;
    [HideInInspector] public Vector2 joystickInput = Vector2.zero;
    public bool useJoystickWorldDirection = true;
    public Transform cameraTransform;
    public float joystickDeadzone = 0.12f;
    public float autoBrakeForce = 2500f; // peak braking when joystick released
    public float brakeLerpSpeed = 12f; // how quickly brake torque is applied/removed

    // Internal
    Rigidbody rb;
    float currentSteer = 0f;
    float currentBrakeTorque = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass += centerOfMassOffset;

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Ensure particle emission modules start disabled to avoid continuous smoke
        for (int i = 0; i < tireSmoke.Length; i++)
        {
            if (tireSmoke[i] != null)
            {
                var em = tireSmoke[i].emission;
                em.enabled = false;
            }
        }
    }

    void FixedUpdate()
    {
        // Read speed early for steering scaling
        float speedKph = rb.linearVelocity.magnitude * 3.6f;

        // ---- INPUTS ----
        float motorInput = 0f;
        float steerInput = 0f;

        if (useVirtualJoystick)
        {
            Vector2 js = joystickInput;
            if (js.magnitude < joystickDeadzone) js = Vector2.zero;

            if (useJoystickWorldDirection && js != Vector2.zero && cameraTransform != null)
            {
                Vector3 camForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                Vector3 camRight = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
                Vector3 desiredDir = (camForward * js.y + camRight * js.x).normalized;

                float angleToDesired = Vector3.SignedAngle(transform.forward, desiredDir, Vector3.up);

                // Convert angle into steer input, apply sensitivity and reduce steering at high speed
                float baseSteer = Mathf.Clamp(angleToDesired / maxSteerAngle, -1f, 1f) * steeringSensitivity;
                float speedReduce = Mathf.Lerp(1f, steeringReductionAtTopSpeed, Mathf.Clamp01(speedKph / topSpeedKph));
                steerInput = Mathf.Clamp(baseSteer * speedReduce, -1f, 1f);

                // make steering response curve a bit sharper near extremes for snappy control
                steerInput = Mathf.Sign(steerInput) * Mathf.Pow(Mathf.Abs(steerInput), steeringResponseCurve);

                float forwardDot = Mathf.Clamp01(Vector3.Dot(transform.forward, desiredDir));
                // give more throttle authority even if small angle to desired direction
                motorInput = js.magnitude * (forwardDot > 0.05f ? 1f : 0.75f);

                // if joystick points mostly backwards, invert motorInput sign to allow reversing
                if (Vector2.Dot(js.normalized, Vector2.up) < -0.3f)
                {
                    motorInput = -js.magnitude;
                }
            }
            else
            {
                // local joystick: y = throttle, x = steer
                motorInput = joystickInput.y;
                steerInput = joystickInput.x;
                steerInput = Mathf.Sign(steerInput) * Mathf.Pow(Mathf.Abs(steerInput), steeringResponseCurve);
            }
        }
        else
        {
            motorInput = Input.GetAxis("Vertical");
            steerInput = Input.GetAxis("Horizontal");
            steerInput = Mathf.Sign(steerInput) * Mathf.Pow(Mathf.Abs(steerInput), steeringResponseCurve);
        }

        // Compute joystick active state
        bool joystickActive = useVirtualJoystick ? joystickInput.magnitude >= joystickDeadzone : !Mathf.Approximately(motorInput, 0f);

        // ---- STEERING (make immediate using MoveTowards for low-latency feel) ----
        float targetSteer = steerInput * maxSteerAngle;
        // MoveTowards gives consistent responsiveness (degrees per second feel)
        currentSteer = Mathf.MoveTowards(currentSteer, targetSteer, steerSpeed * Time.fixedDeltaTime * 12f);

        if (wheelColliders.Length >= 2)
        {
            wheelColliders[0].steerAngle = currentSteer;
            wheelColliders[1].steerAngle = currentSteer;
        }

        // ---- BRAKES: ensure brakes are released quickly when player commands movement ----
        HandleBrakeRelease(motorInput, joystickActive);

        // ---- TORQUE SCALING BY SPEED (immediate acceleration feel) ----
        float speedFactor = Mathf.Clamp01(1f - (speedKph / topSpeedKph));

        // Use a more aggressive torque curve so throttle feels powerful
        float torqueScalar = 0.25f + 0.75f * speedFactor; // never drops below 25% of max
        float effectiveMotor = maxMotorTorque * Mathf.Clamp(motorInput, -1f, 1f) * torqueScalar;

        // boost reverse torque so player feels responsive when reversing
        if (motorInput < 0f)
            effectiveMotor *= reverseTorqueMultiplier;

        // small mass compensation (keeps heavy cars lively)
        effectiveMotor *= Mathf.Clamp01(2000f / Mathf.Max(800f, rb.mass)) * 1.05f;

        // ---- APPLY DRIVE & STABILITY ----
        ApplyDrive(effectiveMotor);
        DoAntiRoll();
        if (enableStabilityControl)
            ApplyStabilityControl();

        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        UpdateWheelMeshes();
        HandleSkids();
    }

    // quick brake-release helper: when player requests movement (forwards or reverse) release brakes fast
    void HandleBrakeRelease(float motorInput, bool joystickActive)
    {
        float desiredBrake = 0f;

        if (!joystickActive)
        {
            // default auto-stop behaviour
            if (rb.linearVelocity.magnitude > 0.05f)
                desiredBrake = autoBrakeForce;
            else
                desiredBrake = 0f;

            // while fully inactive, remove motor torque to prevent creeping
            for (int i = 0; i < wheelColliders.Length; i++)
                wheelColliders[i].motorTorque = 0f;
        }
        else
        {
            // if player pushes forward OR reverse, release brakes quickly
            if (Mathf.Abs(motorInput) > 0.12f)
            {
                // very fast release to avoid input lag
                currentBrakeTorque = Mathf.Lerp(currentBrakeTorque, 0f, Time.fixedDeltaTime * brakeLerpSpeed * 12f);
                desiredBrake = currentBrakeTorque;
            }
            else
            {
                // small stabilization brake when joystick horizontal only
                if (rb.linearVelocity.magnitude > 1f)
                    desiredBrake = 50f;
                else
                    desiredBrake = 0f;

                // if player requests reverse while moving forward, do not apply full braking — allow quicker reversing
                if (motorInput < -0.1f && Vector3.Dot(transform.forward, rb.linearVelocity) > 0.5f)
                    desiredBrake = Mathf.Min(desiredBrake, maxBrakeTorque * 0.25f);
            }
        }

        // Smoothly lerp currentBrakeTorque to desired to avoid jolt
        currentBrakeTorque = Mathf.Lerp(currentBrakeTorque, desiredBrake, Time.fixedDeltaTime * brakeLerpSpeed);

        for (int i = 0; i < wheelColliders.Length; i++)
            wheelColliders[i].brakeTorque = currentBrakeTorque;
    }

    void ApplyDrive(float torque)
    {
        if (rearWheelDrive && wheelColliders.Length >= 4)
        {
            // When reversing while the car still has forward velocity, apply torque but avoid fighting brakes too much
            wheelColliders[2].motorTorque = AdjustForTraction(wheelColliders[2], torque);
            wheelColliders[3].motorTorque = AdjustForTraction(wheelColliders[3], torque);
        }

        if (frontWheelDrive && wheelColliders.Length >= 2)
        {
            wheelColliders[0].motorTorque = AdjustForTraction(wheelColliders[0], torque);
            wheelColliders[1].motorTorque = AdjustForTraction(wheelColliders[1], torque);
        }

        if (!rearWheelDrive && !frontWheelDrive)
        {
            for (int i = 0; i < wheelColliders.Length; i++)
                wheelColliders[i].motorTorque = AdjustForTraction(wheelColliders[i], torque);
        }
    }

    float AdjustForTraction(WheelCollider wheel, float inputTorque)
    {
        if (!enableTractionControl || wheel == null) return inputTorque;

        WheelHit hit;
        if (wheel.GetGroundHit(out hit))
        {
            float sideways = Mathf.Abs(hit.sidewaysSlip);
            float forward = Mathf.Abs(hit.forwardSlip);

            // Combine forward and sideways slip to determine reduction strength (progressive)
            float slipAmount = Mathf.Clamp01((sideways + forward * 0.5f) / (slipLimit * 2f));
            float reduction = Mathf.Clamp01(tractionControlStrength * slipAmount);

            return inputTorque * (1f - reduction);
        }
        return inputTorque;
    }

    void DoAntiRoll()
    {
        if (wheelColliders.Length < 4) return;
        ApplyAntiRollPair(0, 1);
        ApplyAntiRollPair(2, 3);
    }

    void ApplyAntiRollPair(int leftIndex, int rightIndex)
    {
        WheelHit hit;
        float travelL = 1f;
        float travelR = 1f;

        bool groundedL = wheelColliders[leftIndex].GetGroundHit(out hit);
        if (groundedL)
            travelL = (-wheelColliders[leftIndex].transform.InverseTransformPoint(hit.point).y - wheelColliders[leftIndex].radius) / wheelColliders[leftIndex].suspensionDistance;

        bool groundedR = wheelColliders[rightIndex].GetGroundHit(out hit);
        if (groundedR)
            travelR = (-wheelColliders[rightIndex].transform.InverseTransformPoint(hit.point).y - wheelColliders[rightIndex].radius) / wheelColliders[rightIndex].suspensionDistance;

        float antiRollForce = (travelL - travelR) * antiRoll;

        if (groundedL)
            rb.AddForceAtPosition(wheelColliders[leftIndex].transform.up * -antiRollForce, wheelColliders[leftIndex].transform.position);
        if (groundedR)
            rb.AddForceAtPosition(wheelColliders[rightIndex].transform.up * antiRollForce, wheelColliders[rightIndex].transform.position);
    }

    void ApplyStabilityControl()
    {
        if (rb.linearVelocity.magnitude < 1f) return;

        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        float slipAngle = Mathf.Atan2(localVel.x, localVel.z) * Mathf.Rad2Deg;

        // Apply a corrective yaw torque proportional to slip angle and speed
        float speedFactor = Mathf.Clamp01(rb.linearVelocity.magnitude / 20f);
        float corrective = -slipAngle * stabilityStrength * speedFactor;
        corrective = Mathf.Clamp(corrective, -150f, 150f);

        rb.AddTorque(Vector3.up * corrective);
    }

    void UpdateWheelMeshes()
    {
        for (int i = 0; i < wheelColliders.Length && i < wheelMeshes.Length; i++)
        {
            if (wheelMeshes[i] == null || wheelColliders[i] == null) continue;

            Vector3 pos;
            Quaternion rot;
            wheelColliders[i].GetWorldPose(out pos, out rot);

            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    void HandleSkids()
    {
        for (int i = 0; i < wheelColliders.Length; i++)
        {
            if (wheelColliders[i] == null) continue;
            WheelHit hit;
            if (wheelColliders[i].GetGroundHit(out hit))
            {
                float sidewaysSlip = Mathf.Abs(hit.sidewaysSlip);
                float combinedSlip = sidewaysSlip + Mathf.Abs(hit.forwardSlip) * 0.5f;
                bool isSkidding = combinedSlip > skidThreshold;

                // Update skid trail position to contact point to ensure mark shows on ground
                if (isSkidding && skidTrails.Length > i && skidTrails[i] != null)
                {
                    skidTrails[i].transform.position = hit.point + Vector3.up * 0.02f;
                    if (!skidTrails[i].emitting)
                    {
                        skidTrails[i].Clear();
                        skidTrails[i].emitting = true;
                    }
                }
                else if (skidTrails.Length > i && skidTrails[i] != null)
                {
                    skidTrails[i].emitting = false;
                }

                // Control particle emission intensity based on slip amount
                if (tireSmoke.Length > i && tireSmoke[i] != null)
                {
                    var em = tireSmoke[i].emission;
                    if (isSkidding)
                    {
                        em.enabled = true;
                        float intensity = Mathf.Clamp01((combinedSlip - skidThreshold) / (skidThreshold * 2f));
                        em.rateOverTime = new ParticleSystem.MinMaxCurve(smokeEmissionRate * intensity);
                        if (!tireSmoke[i].isPlaying) tireSmoke[i].Play();
                    }
                    else
                    {
                        em.enabled = false;
                        if (tireSmoke[i].isPlaying) tireSmoke[i].Stop();
                    }
                }
            }
            else
            {
                // Wheel not touching ground -> disable effects
                if (tireSmoke.Length > i && tireSmoke[i] != null)
                {
                    var em = tireSmoke[i].emission;
                    em.enabled = false;
                    if (tireSmoke[i].isPlaying) tireSmoke[i].Stop();
                }
                if (skidTrails.Length > i && skidTrails[i] != null)
                    skidTrails[i].emitting = false;
            }
        }
    }

    // Exposed API for your joystick UI to pass values into the controller
    public void SetJoystickInput(Vector2 input)
    {
        joystickInput = input;
    }

    public Vector2 GetJoystickInput() => joystickInput;

    #if UNITY_EDITOR
    [ContextMenu("Auto Configure WheelColliders")]
    void AutoConfigureWheelColliders()
    {
        foreach (var wc in wheelColliders)
        {
            if (wc == null) continue;
            wc.suspensionDistance = 0.18f; // slightly firmer for mobile responsiveness
            JointSpring spring = wc.suspensionSpring;
            spring.spring = 36000f;
            spring.damper = 5000f;
            wc.suspensionSpring = spring;

            WheelFrictionCurve forward = wc.forwardFriction;
            forward.stiffness = 1.35f;
            wc.forwardFriction = forward;

            WheelFrictionCurve sideways = wc.sidewaysFriction;
            sideways.stiffness = 2.2f; // stronger grip for stability on touch
            wc.sidewaysFriction = sideways;
        }
        Debug.Log("WheelColliders auto-configured for responsive arcade racing (mobile). Tweak for fine feel.");
    }
    #endif
}
