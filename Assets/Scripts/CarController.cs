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
    public float skidThreshold = 0.4f;

    [Header("Drive")]
    public bool rearWheelDrive = true;
    public bool frontWheelDrive = false;
    public float maxMotorTorque = 1200f;
    public float maxBrakeTorque = 3000f;

    [Header("Steering")]
    public float maxSteerAngle = 30f;
    public float steerSpeed = 5f;

    [Header("Stability & Feel")]
    public Vector3 centerOfMassOffset = new Vector3(0, -0.5f, 0);
    public float antiRoll = 5000f;
    public float downforce = 50f;
    public float topSpeedKph = 180f;

    [Header("Tuning")]
    public float motorTorqueCurve = 1.0f;

    [Header("Traction Control")]
    public bool enableTractionControl = true;
    public float slipLimit = 0.3f;
    public float tractionControlStrength = 0.5f;

    [Header("Stability Control")]
    public bool enableStabilityControl = true;
    public float stabilityStrength = 0.8f; // higher = more correction force

    [Header("Mobile / Joystick")]
    // If true, the controller will read from the public joystickInput (set from your touch joystick script)
    // If false, it uses Unity's Input axes (useful for editor testing).
    public bool useVirtualJoystick = true;
    // The joystick input should be set from your UI joystick: x = left/right (-1..1), y = forward/back (-1..1)
    [HideInInspector] public Vector2 joystickInput = Vector2.zero;

    // When true the joystick direction is interpreted in world-space relative to camera direction.
    // The car will attempt to steer toward that world direction and drive forward automatically.
    public bool useJoystickWorldDirection = true;
    public Transform cameraTransform; // required for world-direction mode (assign your main camera here)

    // When joystick is released (magnitude < deadzone) we apply braking so car comes to a stop automatically
    public float joystickDeadzone = 0.15f;
    public float autoBrakeForce = 3000f; // brakeTorque applied automatically when joystick released

    Rigidbody rb;
    float currentSteer = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass += centerOfMassOffset;

        // If camera not assigned and main camera exists, auto-assign it for convenience
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    void FixedUpdate()
    {
        // ---- INPUTS ----
        float motorInput = 0f;
        float steerInput = 0f;

        if (useVirtualJoystick)
        {
            Vector2 js = joystickInput;
            // apply deadzone
            if (js.magnitude < joystickDeadzone)
            {
                js = Vector2.zero;
            }

            if (useJoystickWorldDirection && js != Vector2.zero && cameraTransform != null)
            {
                // Convert joystick to world-space direction relative to camera forward
                Vector3 camForward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                Vector3 camRight = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
                Vector3 desiredDir = (camForward * js.y + camRight * js.x).normalized;

                // Compute angle between car forward and desired direction in degrees (+ right, - left)
                float angleToDesired = Vector3.SignedAngle(transform.forward, desiredDir, Vector3.up);

                // Map angle to steering input (-1..1)
                steerInput = Mathf.Clamp(angleToDesired / maxSteerAngle, -1f, 1f);

                // Motor input: drive forward proportional to joystick magnitude and the forward component
                float forwardDot = Mathf.Clamp01(Vector3.Dot(transform.forward, desiredDir));
                motorInput = js.magnitude * (forwardDot > 0.1f ? 1f : 0.6f); // if backwards, reduce throttle
            }
            else
            {
                // Local-style joystick: y = throttle, x = steer
                motorInput = joystickInput.y;
                steerInput = joystickInput.x;
            }
        }
        else
        {
            motorInput = Input.GetAxis("Vertical");
            steerInput = Input.GetAxis("Horizontal");
        }

        // ---- TORQUE SCALING BY SPEED ----
        float speedKph = rb.linearVelocity.magnitude * 3.6f;
        float speedFactor = Mathf.Clamp01(1f - (speedKph / topSpeedKph));
        float effectiveMotor = maxMotorTorque * motorInput * Mathf.Pow(speedFactor, motorTorqueCurve);

        // ---- STEERING SMOOTHING ----
        float targetSteer = steerInput * maxSteerAngle;
        currentSteer = Mathf.Lerp(currentSteer, targetSteer, Time.fixedDeltaTime * steerSpeed);

        if (wheelColliders.Length >= 2)
        {
            wheelColliders[0].steerAngle = currentSteer;
            wheelColliders[1].steerAngle = currentSteer;
        }

        // ---- DRIVE & BRAKES ----
        ApplyDrive(effectiveMotor);
        ApplyBrakesAutoStop(motorInput);

        // ---- STABILITY HELPERS ----
        DoAntiRoll();
        if (enableStabilityControl)
            ApplyStabilityControl();

        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        UpdateWheelMeshes();
        HandleSkids();
    }

    void ApplyDrive(float torque)
    {
        if (rearWheelDrive && wheelColliders.Length >= 4)
        {
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
            if (hit.forwardSlip >= slipLimit)
            {
                return inputTorque * (1f - tractionControlStrength);
            }
        }
        return inputTorque;
    }

    // Braking that automatically brings the car to stop when joystick released
    void ApplyBrakesAutoStop(float motorInput)
    {
        bool joystickActive = useVirtualJoystick ? joystickInput.magnitude >= joystickDeadzone : !Mathf.Approximately(motorInput, 0f);

        float brake = 0f;

        if (!joystickActive)
        {
            // if joystick released, apply strong brakes until near zero velocity
            if (rb.linearVelocity.magnitude > 0.1f)
                brake = autoBrakeForce;
            else
                brake = 0f;

            // remove motor torque while braking to prevent creeping
            for (int i = 0; i < wheelColliders.Length; i++)
                wheelColliders[i].motorTorque = 0f;
        }
        else
        {
            // normal small auto-brake to stabilize when throttle not pressed but joystick horizontal only
            if (Mathf.Approximately(motorInput, 0f) && rb.linearVelocity.magnitude > 1f)
                brake = 50f;

            // If player is pushing throttle opposite to travel direction, add stronger braking
            if (motorInput * Vector3.Dot(transform.forward, rb.linearVelocity) < -0.1f)
                brake = maxBrakeTorque * 0.5f;
        }

        for (int i = 0; i < wheelColliders.Length; i++)
        {
            wheelColliders[i].brakeTorque = brake;
        }
    }

    void ApplyBrakes(bool handbrake, float motorInput) { /* kept for compatibility but not used */ }

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
        float angle = Mathf.Atan2(localVel.x, localVel.z) * Mathf.Rad2Deg;

        rb.AddTorque(Vector3.up * -angle * stabilityStrength);
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
                bool isSkidding = sidewaysSlip > skidThreshold;

                TriggerEffects(i, isSkidding);
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
            if (state && !tireSmoke[index].isPlaying)
                tireSmoke[index].Play();
            else if (!state && tireSmoke[index].isPlaying)
                tireSmoke[index].Stop();
        }

        if (skidTrails.Length > index && skidTrails[index] != null)
        {
            skidTrails[index].emitting = state;
        }
    }

    // Exposed API for your joystick UI to pass values into the controller
    // Call from your UI joystick script: carController.SetJoystickInput(new Vector2(x, y));
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
            wc.suspensionDistance = 0.2f;
            JointSpring spring = wc.suspensionSpring;
            spring.spring = 35000f;
            spring.damper = 4500f;
            wc.suspensionSpring = spring;

            WheelFrictionCurve forward = wc.forwardFriction;
            forward.stiffness = 1.2f;
            wc.forwardFriction = forward;

            WheelFrictionCurve sideways = wc.sidewaysFriction;
            sideways.stiffness = 2.0f; // stronger grip for stability
            wc.sidewaysFriction = sideways;
        }
        Debug.Log("WheelColliders auto-configured for arcade racing. Tweak for fine feel.");
    }
    #endif
}
