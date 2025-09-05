using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    // -----------------------
    // ARCADE / MOBILE-FRIENDLY CAR CONTROLLER
    // Based on your Prometeo example - adapted to a WheelCollider array and virtual joystick.
    // - Mobile joystick support (SetJoystickInput)
    // - Smooth throttle/steering axes
    // - Auto-decelerate / auto-stop when joystick released
    // - Traction & simple stability control
    // - Rear smoke & skid effects
    // -----------------------

    [Header("References")]
    // Order: 0 = FL, 1 = FR, 2 = RL, 3 = RR
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    public Transform[] wheelMeshes = new Transform[4];

    [Header("Car - basic values")]
    [Range(20, 220)] public int maxSpeedKph = 120;            // top speed (km/h)
    [Range(5, 80)] public int maxReverseSpeedKph = 30;        // reverse top speed (km/h)
    [Range(1, 10)] public int accelerationMultiplier = 3;     // 1..10 - scales applied motor torque
    [Range(1, 10)] public int decelerationMultiplier = 3;     // how fast car slows when joystick released
    [Range(10, 45)] public int maxSteerAngle = 30;           // degrees
    [Range(0.1f, 5f)] public float steeringSpeed = 0.6f;     // steering Lerp speed
    [Range(100, 6000)] public int brakeForce = 1500;         // brake torque

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
    [Tooltip("How strongly the script corrects yaw when sliding")]
    [Range(0f, 3f)] public float stabilityStrength = 0.9f;

    [Header("Effects & Skid")]
    public ParticleSystem[] tireSmoke = new ParticleSystem[4]; // optional: assign 2 rear particle systems
    public TrailRenderer[] skidTrails = new TrailRenderer[4];  // optional: assign rear trails
    public float skidThreshold = 0.4f;

    [Header("Mobile / Joystick")]
    public bool useVirtualJoystick = true; // set false to use Unity Input axes for testing in editor
    [HideInInspector] public Vector2 joystickInput = Vector2.zero; // set from UI: (-1..1, -1..1)
    public bool useJoystickWorldDirection = true; // interpret joystick relative to camera
    public Transform cameraTransform; // required when using world-direction
    [Range(0f, 0.4f)] public float joystickDeadzone = 0.15f;
    [Tooltip("Strong braking applied while joystick released to come to a stop")]
    public float autoBrakeForce = 3000f;

    // -----------------------
    // Internal runtime
    // -----------------------
    Rigidbody rb;

    // smooth axes (similar to Prometeo): throttleAxis [-1..1], steeringAxis [-1..1]
    float throttleAxis = 0f;
    float steeringAxis = 0f;

    // drifting / skid detection
    float localVelocityX;
    float localVelocityZ;
    bool isDrifting = false;
    bool isTractionLocked = false;

    // saved friction values so we can change friction for drift if needed (optional)
    WheelFrictionCurve[] originalSideways = new WheelFrictionCurve[4];

    // helper state
    bool deceleratingCar = false;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass += centerOfMassOffset;

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Cache original sideways friction so RecoverTraction can restore values
        for (int i = 0; i < 4 && i < wheelColliders.Length; i++)
        {
            if (wheelColliders[i] != null)
                originalSideways[i] = wheelColliders[i].sidewaysFriction;
        }
    }

    void Update()
    {
        // Read joystick or keyboard/touch controls and update throttleAxis/steeringAxis (smoothly)
        HandleInput();

        // simple visual update for wheel meshes
        UpdateWheelMeshes();
    }

    void FixedUpdate()
    {
        // Update local velocity for skid checks
        localVelocityX = transform.InverseTransformDirection(rb.linearVelocity).x;
        localVelocityZ = transform.InverseTransformDirection(rb.linearVelocity).z;

        // Apply motor & brakes based on throttleAxis
        ApplyMotorAndBrakes();

        // Stability helpers
        DoAntiRoll();
        if (enableStabilityControl) ApplyStabilityControl();

        // Downforce
        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        // Skid effects
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
                // convert joystick to world-space direction relative to camera
                Vector3 camF = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                Vector3 camR = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
                Vector3 desiredDir = (camF * js.y + camR * js.x).normalized;

                // steering: signed angle between car forward and desiredDir
                float angleToDesired = Vector3.SignedAngle(transform.forward, desiredDir, Vector3.up);
                desiredSteer = Mathf.Clamp(angleToDesired / maxSteerAngle, -1f, 1f);

                // throttle: drive forward based on how aligned desiredDir is with car forward
                float forwardDot = Mathf.Clamp01(Vector3.Dot(transform.forward, desiredDir));
                desiredThrottle = js.magnitude * (forwardDot > 0.1f ? 1f : 0.35f);

                // If joystick points roughly backwards, use reverse throttle (negative)
                if (Vector3.Dot(transform.forward, desiredDir) < -0.6f)
                    desiredThrottle = -js.magnitude; // reverse
            }
            else
            {
                // local-style joystick: y = throttle (-1..1), x = steer
                desiredThrottle = joystickInput.y;
                desiredSteer = joystickInput.x;
            }
        }
        else
        {
            // Editor / keyboard fallback
            desiredThrottle = Input.GetAxis("Vertical");
            desiredSteer = Input.GetAxis("Horizontal");
        }

        // Smoothly approach desired axes (gives arcade smoothing like Prometeo)
        // Throttle smoothing: ramp up/down faster when pressing, slower when releasing
        float accelRamp = 3f * Time.deltaTime;
        float decelRamp = 6.5f * Time.deltaTime;
        if (Mathf.Abs(desiredThrottle) > Mathf.Abs(throttleAxis))
            throttleAxis = Mathf.MoveTowards(throttleAxis, desiredThrottle, accelRamp);
        else
            throttleAxis = Mathf.MoveTowards(throttleAxis, desiredThrottle, decelRamp);

        // Steering smoothing
        steeringAxis = Mathf.MoveTowards(steeringAxis, desiredSteer, steeringSpeed * Time.deltaTime * 6f);

        // Auto-stop: if joystick released and we are moving, start deceleration coroutine using InvokeRepeating
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
    // Motor / braking
    // -----------------------
    void ApplyMotorAndBrakes()
    {
        // compute current speed kph
        float speedKph = rb.linearVelocity.magnitude * 3.6f;

        // Apply steering to front wheels
        float steerAngle = steeringAxis * maxSteerAngle;
        if (wheelColliders.Length >= 2)
        {
            if (wheelColliders[0] != null) wheelColliders[0].steerAngle = Mathf.Lerp(wheelColliders[0].steerAngle, steerAngle, steeringSpeed * Time.fixedDeltaTime * 60f);
            if (wheelColliders[1] != null) wheelColliders[1].steerAngle = Mathf.Lerp(wheelColliders[1].steerAngle, steerAngle, steeringSpeed * Time.fixedDeltaTime * 60f);
        }

        // Basic brake handling: when throttleAxis ~ 0 and joystick not active, brakes are applied in DecelerateCar.

        // Limit torque near top speed
        float speedFactor = Mathf.Clamp01(1f - (speedKph / Mathf.Max(1f, maxSpeedKph)));

        // Torque to apply (base)
        float baseTorque = accelerationMultiplier * 50f; // tuned constant similar to Prometeo
        float appliedTorque = baseTorque * throttleAxis * Mathf.Pow(speedFactor, 1f);

        // Traction control: reduce torque when wheel forwardSlip too high
        if (enableTractionControl && Mathf.Abs(appliedTorque) > 0.001f)
        {
            if (wheelColliders != null)
            {
                for (int i = 0; i < wheelColliders.Length; i++)
                {
                    if (wheelColliders[i] == null) continue;
                    if ((i == 2 || i == 3)) // rear wheels typically give power in RWD case
                    {
                        float t = appliedTorque;
                        if (enableTractionControl)
                            t = AdjustForTraction(wheelColliders[i], appliedTorque);

                        wheelColliders[i].motorTorque = t;
                    }
                    else
                    {
                        // front wheels: set to zero unless frontWheelDrive
                        if (i < 2 && wheelColliders.Length >= 2)
                        {
                            // if front-wheel drive desired, set here (not default)
                        }
                    }
                }
            }
        }
        else
        {
            // apply torque directly without TC (or if torque is near zero)
            for (int i = 0; i < wheelColliders.Length; i++)
            {
                if (wheelColliders[i] == null) continue;
                if (i == 2 || i == 3)
                    wheelColliders[i].motorTorque = appliedTorque;
            }
        }

        // Apply small forward stabilization brake if throttle not pressed and car speed is low
        if (Mathf.Abs(throttleAxis) < 0.01f && rb.linearVelocity.magnitude > 0.05f)
        {
            // keep small brake
            for (int i = 0; i < wheelColliders.Length; i++) if (wheelColliders[i] != null) wheelColliders[i].brakeTorque = 0f;
        }
    }

    // Adjust torque for traction based on forward slip
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
    // Called repeatedly with InvokeRepeating when joystick released
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
            // stop completely
            rb.linearVelocity = Vector3.zero;
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

                // rear wheels smoke/trail
                TriggerEffects(i, isSkiddingNow);

                // small flag for other logic
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
    // Called from your on-screen joystick script every frame with (-1..1, -1..1)
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
