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
    public float maxMotorTorque = 9000f; // engine peak
    public float maxBrakeTorque = 6000f;

    [Header("Steer")]
    public float maxSteerAngle = 38f;
    public float steerResponsiveness = 540f; // degrees per second - high value for immediate feel
    public float steeringSensitivity = 1.2f; // multiplier for joystick world-direction mapping
    public float steeringReductionAtTopSpeed = 0.45f; // how much steering is reduced at top speed
    public float steeringResponseCurve = 1.05f; // >1 = more aggressive near edges

    [Header("Stability & Feel")]
    public Vector3 centerOfMassOffset = new Vector3(0, -0.6f, 0);
    public float antiRoll = 5000f;
    public float downforce = 60f;
    public float topSpeedKph = 220f;

    [Header("Rigidbody (auto-adjust)")]
    public float recommendedLinearDrag = 0.01f;   // default linear drag for arcade feel
    public float recommendedAngularDrag = 0.05f;  // default angular drag
    public bool enforceRigidbodySettings = true;

    [Header("Drivetrain Assist")]
    // Helps the car reach target speeds while keeping Rigidbody drag intact
    public bool useDriveAssist = true;
    public float driveAssistForce = 30f; // acceleration applied (m/s^2) as assist (higher = faster)
    public float drivetrainPower = 1.25f; // global multiplier on motor torque
    public float dragCompensationFactor = 6f; // how aggressively we compensate when drag > recommended

    [Header("Reverse / Collision Assist")]
    public float reverseTorqueMultiplier = 2.0f; // multiplier when reversing normally
    public float reverseMovingTorque = 4200f; // torque to initiate reversing when already moving
    public float reverseBrakeAssist = 8000f; // extra brake applied when switching to reverse
    public float reverseEngageSpeed = 0.6f; // m/s - below this full reverse torque is allowed
    public float collisionIgnoreDuration = 0.35f; // time after collision to relax reverse assist

    [Header("Traction & Stability")]
    public bool enableTractionControl = true;
    public float slipLimit = 0.25f;
    [Range(0f, 1f)] public float tractionControlStrength = 0.6f; // stronger correction
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

    // Collision / traction-temp state
    float lastCollisionTime = -10f; // timestamp of last physics collision
    float tractionControlTempDisableUntil = -10f; // time until traction control is disabled
    bool joystickActiveFlag = false;

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        if (rb != null && enforceRigidbodySettings)
        {
            rb.linearDamping = recommendedLinearDrag;
            rb.angularDamping = recommendedAngularDrag;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

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

    void OnCollisionEnter(Collision collision)
    {
        lastCollisionTime = Time.time;
    }

    void FixedUpdate()
    {
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

                float baseSteer = Mathf.Clamp(angleToDesired / maxSteerAngle, -1f, 1f) * steeringSensitivity;
                float speedReduce = Mathf.Lerp(1f, steeringReductionAtTopSpeed, Mathf.Clamp01(speedKph / topSpeedKph));
                steerInput = Mathf.Clamp(baseSteer * speedReduce, -1f, 1f);
                steerInput = Mathf.Sign(steerInput) * Mathf.Pow(Mathf.Abs(steerInput), steeringResponseCurve);

                float forwardDot = Mathf.Clamp01(Vector3.Dot(transform.forward, desiredDir));
                motorInput = js.magnitude * (forwardDot > 0.05f ? 1f : 0.75f);

                if (Vector2.Dot(js.normalized, Vector2.up) < -0.3f)
                {
                    motorInput = -js.magnitude;
                }
            }
            else
            {
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

        bool joystickActive = useVirtualJoystick ? joystickInput.magnitude >= joystickDeadzone : !Mathf.Approximately(motorInput, 0f);
        joystickActiveFlag = joystickActive;

        // ---- STEERING ----
        float targetSteer = steerInput * maxSteerAngle;
        currentSteer = Mathf.MoveTowards(currentSteer, targetSteer, steerResponsiveness * Time.fixedDeltaTime);

        if (wheelColliders.Length >= 2)
        {
            wheelColliders[0].steerAngle = currentSteer;
            wheelColliders[1].steerAngle = currentSteer;
        }

        // ---- BRAKES ----
        HandleBrakeRelease(motorInput, joystickActive);

        // ---- TORQUE CALC ----
        float speedFactor = Mathf.Clamp01(1f - (speedKph / topSpeedKph));
        float torqueScalar = 0.15f + 0.85f * speedFactor; // keep some torque at top
        float effectiveMotor = maxMotorTorque * Mathf.Clamp(motorInput, -1f, 1f) * torqueScalar;

        // detect moving forward state
        bool movingForward = Vector3.Dot(transform.forward, rb.linearVelocity) > 0.4f;
        bool recentCollision = (Time.time - lastCollisionTime) < collisionIgnoreDuration;

        // If player requests reverse while moving forward and there was a collision recently, allow immediate partial reverse
        if (motorInput < 0f && movingForward && recentCollision && joystickActive)
        {
            // clear brakes immediately
            for (int i = 0; i < wheelColliders.Length; i++)
                wheelColliders[i].brakeTorque = 0f;

            // give an immediate reverse 'kick' scaled by input
            effectiveMotor = -Mathf.Max(reverseMovingTorque, maxMotorTorque * 0.5f) * Mathf.Abs(motorInput) * 0.7f;

            // disable traction control briefly so we don't cut power
            tractionControlTempDisableUntil = Time.time + 0.45f;
        }
        else if (motorInput < 0f && movingForward)
        {
            // normal reverse assist when switching direction
            effectiveMotor = -reverseMovingTorque * Mathf.Abs(motorInput);
        }
        else
        {
            // normal forward or reverse scaling
            if (motorInput < 0f)
                effectiveMotor *= reverseTorqueMultiplier;
        }

        // mass compensation
        effectiveMotor *= Mathf.Clamp01(2000f / Mathf.Max(800f, rb.mass)) * 1.05f;

        // drivetrain & drag compensation
        float dragComp = Mathf.Clamp01((rb.linearDamping - recommendedLinearDrag) * dragCompensationFactor);
        float finalMultiplier = drivetrainPower * (1f + dragComp);
        effectiveMotor *= finalMultiplier;

        effectiveMotor = Mathf.Clamp(effectiveMotor, -maxMotorTorque * 4f, maxMotorTorque * 4f);

        // ---- APPLY DRIVE & STABILITY ----
        ApplyDrive(effectiveMotor, motorInput);
        DoAntiRoll();
        if (enableStabilityControl) ApplyStabilityControl();

        // Drive assist: apply additional rigidbody acceleration to overcome high drag while keeping drag values
        if (useDriveAssist && Mathf.Abs(motorInput) > 0.05f)
        {
            float currentSpeed = rb.linearVelocity.magnitude * 3.6f;
            if ((motorInput > 0 && currentSpeed < topSpeedKph) || (motorInput < 0 && rb.linearVelocity.magnitude < reverseEngageSpeed * 1.5f))
            {
                // ForceMode.Acceleration ignores mass (applies m/s^2), nice for predictable feel across masses
                rb.AddForce(transform.forward * Mathf.Sign(motorInput) * driveAssistForce, ForceMode.Acceleration);
            }
        }

        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        UpdateWheelMeshes();
        HandleSkids();
    }

    void HandleBrakeRelease(float motorInput, bool joystickActive)
    {
        float desiredBrake = 0f;

        if (!joystickActive)
        {
            if (rb.linearVelocity.magnitude > 0.05f)
                desiredBrake = autoBrakeForce;
            else
                desiredBrake = 0f;

            for (int i = 0; i < wheelColliders.Length; i++)
                wheelColliders[i].motorTorque = 0f;
        }
        else
        {
            if (Mathf.Abs(motorInput) > 0.12f)
            {
                currentBrakeTorque = 0f;
                desiredBrake = 0f;

                for (int i = 0; i < wheelColliders.Length; i++)
                    wheelColliders[i].brakeTorque = 0f;
            }
            else
            {
                if (rb.linearVelocity.magnitude > 1f)
                    desiredBrake = 50f;
                else
                    desiredBrake = 0f;

                if (motorInput < -0.1f && Vector3.Dot(transform.forward, rb.linearVelocity) > 0.5f)
                    desiredBrake = Mathf.Min(desiredBrake, maxBrakeTorque * 0.25f);
            }
        }

        currentBrakeTorque = Mathf.Lerp(currentBrakeTorque, desiredBrake, Time.fixedDeltaTime * brakeLerpSpeed);

        for (int i = 0; i < wheelColliders.Length; i++)
            wheelColliders[i].brakeTorque = currentBrakeTorque;
    }

    void ApplyDrive(float torque, float motorInput)
    {
        bool isReverse = torque < 0f;
        float forwardVel = Vector3.Dot(transform.forward, rb.linearVelocity);
        bool movingForward = forwardVel > 0.4f;

        bool recentCollision = (Time.time - lastCollisionTime) < collisionIgnoreDuration;

        if (rearWheelDrive && wheelColliders.Length >= 4)
        {
            float tRL = torque;
            float tRR = torque;

            if (isReverse && movingForward && !recentCollision)
            {
                float extraBrake = Mathf.Clamp(rb.linearVelocity.magnitude * reverseBrakeAssist, 0f, maxBrakeTorque);
                wheelColliders[2].brakeTorque = Mathf.Max(wheelColliders[2].brakeTorque, extraBrake);
                wheelColliders[3].brakeTorque = Mathf.Max(wheelColliders[3].brakeTorque, extraBrake);

                tRL *= 0.25f;
                tRR *= 0.25f;

                if (rb.linearVelocity.magnitude < reverseEngageSpeed)
                {
                    tRL = torque;
                    tRR = torque;
                }
            }

            // If we recently collided and player actively reverses, we already cleared brakes and applied a kick in FixedUpdate.
            // Respect temporary traction-control disable if set
            wheelColliders[2].motorTorque = AdjustForTraction(wheelColliders[2], tRL);
            wheelColliders[3].motorTorque = AdjustForTraction(wheelColliders[3], tRR);
        }

        if (frontWheelDrive && wheelColliders.Length >= 2)
        {
            float tFL = torque;
            float tFR = torque;

            if (isReverse && movingForward && !recentCollision)
            {
                float extraBrake = Mathf.Clamp(rb.linearVelocity.magnitude * reverseBrakeAssist, 0f, maxBrakeTorque);
                wheelColliders[0].brakeTorque = Mathf.Max(wheelColliders[0].brakeTorque, extraBrake);
                wheelColliders[1].brakeTorque = Mathf.Max(wheelColliders[1].brakeTorque, extraBrake);

                tFL *= 0.25f;
                tFR *= 0.25f;

                if (rb.linearVelocity.magnitude < reverseEngageSpeed)
                {
                    tFL = torque;
                    tFR = torque;
                }
            }

            wheelColliders[0].motorTorque = AdjustForTraction(wheelColliders[0], tFL);
            wheelColliders[1].motorTorque = AdjustForTraction(wheelColliders[1], tFR);
        }

        if (!rearWheelDrive && !frontWheelDrive)
        {
            for (int i = 0; i < wheelColliders.Length; i++)
            {
                float t = torque;
                bool movingF = Vector3.Dot(transform.forward, rb.linearVelocity) > 0.4f;
                if (isReverse && movingF && !recentCollision)
                {
                    float extraBrake = Mathf.Clamp(rb.linearVelocity.magnitude * reverseBrakeAssist, 0f, maxBrakeTorque);
                    wheelColliders[i].brakeTorque = Mathf.Max(wheelColliders[i].brakeTorque, extraBrake);
                    t *= 0.25f;
                    if (rb.linearVelocity.magnitude < reverseEngageSpeed) t = torque;
                }

                wheelColliders[i].motorTorque = AdjustForTraction(wheelColliders[i], t);
            }
        }
    }

    float AdjustForTraction(WheelCollider wheel, float inputTorque)
    {
        // Respect temporary disable window
        if (Time.time < tractionControlTempDisableUntil) return inputTorque;

        if (!enableTractionControl || wheel == null) return inputTorque;

        WheelHit hit;
        if (wheel.GetGroundHit(out hit))
        {
            float sideways = Mathf.Abs(hit.sidewaysSlip);
            float forward = Mathf.Abs(hit.forwardSlip);

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
