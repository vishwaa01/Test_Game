using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    // -----------------------
    // ARCADE / MOBILE-FRIENDLY CAR CONTROLLER (TORQUE-BASED)
    // Reworked to use realistic engine/wheel torques and to allow immediate
    // movement when the player steers from stopped position.
    // -----------------------

    [Header("References")]
    // Order: 0 = FL, 1 = FR, 2 = RL, 3 = RR
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    public Transform[] wheelMeshes = new Transform[4];

    [Header("Engine & Transmission (torque-based)")]
    [Tooltip("Maximum engine torque in Nm (applied to driven wheels before distribution)")]
    public float maxEngineTorque = 1600f;   // Nm
    [Tooltip("Maximum brake torque in Nm applied when braking")]
    public float maxBrakeTorque = 4200f;    // Nm
    [Tooltip("Final drive multiplier to simulate gearing (applied to engine torque)")]
    public float finalDriveRatio = 1.0f;    // simple scalar
    [Tooltip("Fraction of torque sent to rear wheels (0..1). 1 = pure RWD, 0 = pure FWD")]
    [Range(0f,1f)] public float rearTorqueFraction = 1f;

    [Header("Car - basic values")]
    [Range(20, 220)] public int maxSpeedKph = 120;            // top speed (km/h)
    [Range(10, 120)] public int maxReverseSpeedKph = 30;      // reverse top speed (km/h)
    [Range(1, 10)] public float accelerationMultiplier = 3f;  // scale for torque application

    [Header("Stability & feel")]
    public Vector3 centerOfMassOffset = new Vector3(0, -0.6f, 0);
    public float antiRoll = 4000f;
    public float downforce = 60f;

    [Header("Traction & stability control")]
    public bool enableTractionControl = true;
    [Tooltip("Forward slip threshold to reduce torque")]
    public float slipLimit = 0.3f;
    [Range(0f, 1f)] public float tractionControlStrength = 0.5f;

    public bool enableStabilityControl = true;
    [Range(0f, 3f)] public float stabilityStrength = 0.9f;

    [Header("Steering")]
    [Range(10, 45)] public int maxSteerAngle = 30;
    [Range(0.05f, 5f)] public float steeringSpeed = 0.6f;

    [Header("Effects & Skid")]
    public ParticleSystem[] tireSmoke = new ParticleSystem[4];
    public TrailRenderer[] skidTrails = new TrailRenderer[4];
    public float skidThreshold = 0.4f;

    [Header("Mobile / Joystick")]
    public bool useVirtualJoystick = true;
    [HideInInspector] public Vector2 joystickInput = Vector2.zero;
    public bool useJoystickWorldDirection = true;
    public Transform cameraTransform;
    [Range(0f, 0.4f)] public float joystickDeadzone = 0.15f;
    [Tooltip("Strong braking applied while joystick released to come to a stop")]
    public float autoBrakeForce = 3000f;

    [Header("Creep & Steering from Stop")]
    [Tooltip("When steering from standstill, apply this small forward torque (fraction of maxEngineTorque)")]
    [Range(0f, 0.5f)] public float creepTorqueFraction = 0.08f;
    [Tooltip("Minimum steering magnitude to trigger creep movement when stopped")]
    public float steerCreepThreshold = 0.15f;

    // -----------------------
    // Internal runtime
    // -----------------------
    Rigidbody rb;
    float throttleAxis = 0f; // -1..1
    float steeringAxis = 0f; // -1..1
    float localVelocityX;
    float localVelocityZ;
    bool isDrifting = false;
    bool isTractionLocked = false;
    WheelFrictionCurve[] originalSideways = new WheelFrictionCurve[4];
    bool deceleratingCar = false;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass += centerOfMassOffset;
        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;

        for (int i = 0; i < 4 && i < wheelColliders.Length; i++)
        {
            if (wheelColliders[i] != null)
                originalSideways[i] = wheelColliders[i].sidewaysFriction;
        }
    }

    void Update()
    {
        HandleInput();
        UpdateWheelMeshes();
    }

    void FixedUpdate()
    {
        localVelocityX = transform.InverseTransformDirection(rb.linearVelocity).x;
        localVelocityZ = transform.InverseTransformDirection(rb.linearVelocity).z;

        ApplyMotorAndBrakes();

        DoAntiRoll();
        if (enableStabilityControl) ApplyStabilityControl();

        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        HandleSkids();
    }

    // -----------------------
    // Input handling
    // -----------------------
    void HandleInput()
    {
        float desiredSteer = 0f;
        float desiredThrottle = 0f;

        if (useVirtualJoystick)
        {
            Vector2 js = joystickInput;
            if (js.magnitude < joystickDeadzone) js = Vector2.zero;

            if (useJoystickWorldDirection && js != Vector2.zero && cameraTransform != null)
            {
                Vector3 camF = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                Vector3 camR = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
                Vector3 desiredDir = (camF * js.y + camR * js.x).normalized;
                float angleToDesired = Vector3.SignedAngle(transform.forward, desiredDir, Vector3.up);
                desiredSteer = Mathf.Clamp(angleToDesired / maxSteerAngle, -1f, 1f);
                float forwardDot = Mathf.Clamp01(Vector3.Dot(transform.forward, desiredDir));
                desiredThrottle = js.magnitude * (forwardDot > 0.1f ? 1f : 0.35f);

                if (Vector3.Dot(transform.forward, desiredDir) < -0.6f)
                    desiredThrottle = -js.magnitude;

                // If joystick has steering but no throttle and car almost stopped, cancel deceleration so creep can take over
                if (Mathf.Abs(js.x) > steerCreepThreshold && Mathf.Abs(js.y) < joystickDeadzone && rb.linearVelocity.magnitude < 0.5f)
                {
                    if (deceleratingCar) { deceleratingCar = false; CancelInvoke(nameof(DecelerateCar)); }
                }
            }
            else
            {
                desiredThrottle = joystickInput.y;
                desiredSteer = joystickInput.x;
            }
        }
        else
        {
            desiredThrottle = Input.GetAxis("Vertical");
            desiredSteer = Input.GetAxis("Horizontal");
        }

        float accelRamp = 3f * Time.deltaTime;
        float decelRamp = 6.5f * Time.deltaTime;
        if (Mathf.Abs(desiredThrottle) > Mathf.Abs(throttleAxis))
            throttleAxis = Mathf.MoveTowards(throttleAxis, desiredThrottle, accelRamp);
        else
            throttleAxis = Mathf.MoveTowards(throttleAxis, desiredThrottle, decelRamp);

        steeringAxis = Mathf.MoveTowards(steeringAxis, desiredSteer, steeringSpeed * Time.deltaTime * 6f);

        bool joystickActive = useVirtualJoystick ? joystickInput.magnitude >= joystickDeadzone : !Mathf.Approximately(desiredThrottle, 0f);
        if (!joystickActive && !deceleratingCar && rb.linearVelocity.magnitude > 0.2f)
        {
            deceleratingCar = true;
            InvokeRepeating(nameof(DecelerateCar), 0f, 0.06f);
        }
        else if (joystickActive && deceleratingCar)
        {
            deceleratingCar = false;
            CancelInvoke(nameof(DecelerateCar));
        }
    }

    // -----------------------
    // Motor / braking (torque-based)
    // -----------------------
    void ApplyMotorAndBrakes()
    {
        float speedKph = rb.linearVelocity.magnitude * 3.6f;

        // steering applied to front wheels
        float steerAngle = steeringAxis * maxSteerAngle;
        if (wheelColliders.Length >= 2)
        {
            if (wheelColliders[0] != null) wheelColliders[0].steerAngle = Mathf.Lerp(wheelColliders[0].steerAngle, steerAngle, steeringSpeed * Time.fixedDeltaTime * 60f);
            if (wheelColliders[1] != null) wheelColliders[1].steerAngle = Mathf.Lerp(wheelColliders[1].steerAngle, steerAngle, steeringSpeed * Time.fixedDeltaTime * 60f);
        }

        // compute engine torque (Nm) based on throttle
        float engineTorque = maxEngineTorque * throttleAxis * accelerationMultiplier * finalDriveRatio;

        // limit torque when above max speed
        if (Mathf.Abs(speedKph) >= maxSpeedKph && throttleAxis > 0f) engineTorque = 0f;
        if (Mathf.Abs(speedKph) >= maxReverseSpeedKph && throttleAxis < 0f) engineTorque = 0f;

        // If steering input present and car nearly stopped, apply a small creep forward torque so steering causes movement
        bool applyCreep = (Mathf.Abs(steeringAxis) > steerCreepThreshold && Mathf.Abs(throttleAxis) < 0.05f && rb.linearVelocity.magnitude < 0.5f);
        float creepTorque = 0f;
        if (applyCreep)
        {
            creepTorque = maxEngineTorque * creepTorqueFraction * Mathf.Abs(steeringAxis);
            engineTorque = Mathf.Max(engineTorque, creepTorque);
        }

        // Distribute torque to wheels according to rearTorqueFraction
        float rearTorque = engineTorque * rearTorqueFraction * 0.5f; // split between RL & RR
        float frontTorque = engineTorque * (1f - rearTorqueFraction) * 0.5f; // split between FL & FR

        // Apply traction control per driven wheel
        for (int i = 0; i < wheelColliders.Length; i++)
        {
            if (wheelColliders[i] == null) continue;

            float torqueToApply = 0f;
            if (i == 2 || i == 3) torqueToApply = rearTorque;
            else torqueToApply = frontTorque;

            // apply traction control
            if (enableTractionControl && Mathf.Abs(torqueToApply) > 0.001f)
                torqueToApply = AdjustForTraction(wheelColliders[i], torqueToApply);

            // Set motor torque for driven wheels. Non-driven will get 0.
            if ((i == 2 || i == 3) && rearTorqueFraction > 0f)
                wheelColliders[i].motorTorque = torqueToApply;
            else if ((i == 0 || i == 1) && rearTorqueFraction < 1f)
                wheelColliders[i].motorTorque = torqueToApply;
            else
                wheelColliders[i].motorTorque = 0f;

            // Apply braking when no throttle and decelerating
            // Brake torque handled in DecelerateCar via brakeTorque assignments
        }
    }

    float AdjustForTraction(WheelCollider wheel, float inputTorque)
    {
        if (!enableTractionControl || wheel == null) return inputTorque;

        WheelHit hit;
        if (wheel.GetGroundHit(out hit))
        {
            if (Mathf.Abs(hit.forwardSlip) > slipLimit)
            {
                return inputTorque * (1f - tractionControlStrength);
            }
        }
        return inputTorque;
    }

    // -----------------------
    // Deceleration / Auto-stop
    // -----------------------
    void DecelerateCar()
    {
        // Gradually reduce throttleAxis
        if (throttleAxis > 0f) throttleAxis = Mathf.Max(0f, throttleAxis - (Time.deltaTime * 8f));
        else if (throttleAxis < 0f) throttleAxis = Mathf.Min(0f, throttleAxis + (Time.deltaTime * 8f));

        // Apply braking torque while decelerating
        if (rb.linearVelocity.magnitude > 0.15f)
        {
            for (int i = 0; i < wheelColliders.Length; i++) if (wheelColliders[i] != null) wheelColliders[i].brakeTorque = autoBrakeForce;
        }
        else
        {
            // remove brakes and stop decelerating, do NOT zero rigidbody velocity so we remain responsive to next input
            for (int i = 0; i < wheelColliders.Length; i++) if (wheelColliders[i] != null) wheelColliders[i].brakeTorque = 0f;
            CancelInvoke(nameof(DecelerateCar));
            deceleratingCar = false;
        }

        // ensure motor torque is zero while decelerating
        for (int i = 0; i < wheelColliders.Length; i++) if (wheelColliders[i] != null) wheelColliders[i].motorTorque = 0f;
    }

    // -----------------------
    // Stability helpers
    // -----------------------
    void DoAntiRoll()
    {
        if (wheelColliders.Length < 4) return;
        ApplyAntiRollPair(0, 1);
        ApplyAntiRollPair(2, 3);
    }

    void ApplyAntiRollPair(int leftIndex, int rightIndex)
    {
        WheelHit hit;
        float travelL = 1f, travelR = 1f;
        bool groundedL = wheelColliders[leftIndex].GetGroundHit(out hit);
        if (groundedL) travelL = (-wheelColliders[leftIndex].transform.InverseTransformPoint(hit.point).y - wheelColliders[leftIndex].radius) / wheelColliders[leftIndex].suspensionDistance;
        bool groundedR = wheelColliders[rightIndex].GetGroundHit(out hit);
        if (groundedR) travelR = (-wheelColliders[rightIndex].transform.InverseTransformPoint(hit.point).y - wheelColliders[rightIndex].radius) / wheelColliders[rightIndex].suspensionDistance;
        float antiRollForce = (travelL - travelR) * antiRoll;
        if (groundedL) rb.AddForceAtPosition(wheelColliders[leftIndex].transform.up * -antiRollForce, wheelColliders[leftIndex].transform.position);
        if (groundedR) rb.AddForceAtPosition(wheelColliders[rightIndex].transform.up * antiRollForce, wheelColliders[rightIndex].transform.position);
    }

    void ApplyStabilityControl()
    {
        if (rb.linearVelocity.magnitude < 1f) return;
        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        float angle = Mathf.Atan2(localVel.x, localVel.z) * Mathf.Rad2Deg;
        rb.AddTorque(Vector3.up * -angle * stabilityStrength);
    }

    // -----------------------
    // Skid / effects
    // -----------------------
    void HandleSkids()
    {
        for (int i = 0; i < wheelColliders.Length; i++)
        {
            if (wheelColliders[i] == null) continue;
            WheelHit hit;
            if (wheelColliders[i].GetGroundHit(out hit))
            {
                float sidewaysSlip = Mathf.Abs(hit.sidewaysSlip);
                bool isSkiddingNow = sidewaysSlip > skidThreshold;
                TriggerEffects(i, isSkiddingNow);
                if (i == 2 || i == 3) isDrifting = isSkiddingNow;
            }
            else
            {
                TriggerEffects(i, false);
            }
        }
    }

    void TriggerEffects(int index, bool state)
    {
        if (tireSmoke.Length > index && tireSmoke[index] != null)
        {
            if (state && !tireSmoke[index].isPlaying) tireSmoke[index].Play();
            else if (!state && tireSmoke[index].isPlaying) tireSmoke[index].Stop();
        }
        if (skidTrails.Length > index && skidTrails[index] != null)
        {
            skidTrails[index].emitting = state;
        }
    }

    // -----------------------
    // Wheel visuals
    // -----------------------
    void UpdateWheelMeshes()
    {
        for (int i = 0; i < wheelColliders.Length && i < wheelMeshes.Length; i++)
        {
            if (wheelMeshes[i] == null || wheelColliders[i] == null) continue;
            Vector3 pos; Quaternion rot;
            wheelColliders[i].GetWorldPose(out pos, out rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    // -----------------------
    // Public API
    // -----------------------
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
            wc.suspensionDistance = 0.18f;
            JointSpring spring = wc.suspensionSpring; spring.spring = 32000f; spring.damper = 4200f; wc.suspensionSpring = spring;
            WheelFrictionCurve f = wc.forwardFriction; f.stiffness = 1.2f; wc.forwardFriction = f;
            WheelFrictionCurve s = wc.sidewaysFriction; s.stiffness = 1.6f; wc.sidewaysFriction = s;
        }
        Debug.Log("WheelColliders auto-configured for mobile arcade feel.");
    }
    #endif
}
