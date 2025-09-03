using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class CarController : MonoBehaviour
{
    // -----------------------
    // Inspector-exposed fields
    // -----------------------

    [Header("References")]
    // WheelColliders order must be: FrontLeft, FrontRight, RearLeft, RearRight
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    // Visual wheel transforms (meshes) in the same order as wheelColliders
    public Transform[] wheelMeshes = new Transform[4];

    [Header("Effects")]
    // Particle systems for tire smoke. Assign particle systems for each wheel (or null if unused).
    // Recommended: only assign rear wheels for drifting (indexes 2 and 3).
    public ParticleSystem[] tireSmoke = new ParticleSystem[4];
    // TrailRenderers used as skid mark renderers. They should be separate objects that follow the wheel positions.
    // Assign for the rear wheels (indexes 2 and 3) to get skidmarks.
    public TrailRenderer[] skidTrails = new TrailRenderer[4];
    // Sideways slip threshold above which we consider the wheel to be skidding (and spawn smoke/trail)
    public float skidThreshold = 0.4f;

    [Header("Drive")]
    // Select which axle(s) receive motor torque
    public bool rearWheelDrive = true;
    public bool frontWheelDrive = false;
    // Torque and braking values (start with these and tune per-car)
    public float maxMotorTorque = 1200f;   // Nm applied to driven wheels
    public float maxBrakeTorque = 3000f;   // normal brake strength
    public float handbrakeTorque = 4000f;  // handbrake (applied to rear wheels)

    [Header("Steering")]
    // Steering limits and responsiveness
    public float maxSteerAngle = 30f;      // maximum wheel steer angle in degrees
    public float steerSpeed = 5f;          // how quickly steering transitions to target angle

    [Header("Stability & Feel")]
    // Lowering center of mass improves stability (reduce flipping). Use small negative Y values.
    public Vector3 centerOfMassOffset = new Vector3(0, -0.5f, 0);
    public float antiRoll = 5000f;         // anti-roll bar strength (higher = less body roll)
    public float downforce = 50f;          // extra downward force proportional to speed
    public float topSpeedKph = 180f;       // soft top-speed where torque softens

    [Header("Tuning")]
    // Controls how sharply torque falls off near top speed
    public float motorTorqueCurve = 1.0f;

    // -----------------------
    // Internal runtime fields
    // -----------------------
    Rigidbody rb;
    float currentSteer = 0f; // smoothed steer angle used each FixedUpdate

    // Called once when object is enabled
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        // Adjust the Rigidbody centre of mass at start to improve handling stability
        rb.centerOfMass += centerOfMassOffset;
    }

    // Physics update loop — preferred for Rigidbody interactions
    void FixedUpdate()
    {
        // ---- INPUTS ----
        // Use Unity's input axes by default (customise for mobile/touch later)
        float motorInput = Input.GetAxis("Vertical");   // forward/back (-1..1)
        float steerInput = Input.GetAxis("Horizontal"); // left/right (-1..1)
        bool handbrake = Input.GetKey(KeyCode.Space) || Input.GetAxis("Jump") > 0f;

        // ---- TORQUE SCALING BY SPEED ----
        // Convert velocity to KPH for easier tuning
        float speedKph = rb.linearVelocity.magnitude * 3.6f;
        // speedFactor goes from 1 (stopped) to 0 (at topSpeedKph or above)
        float speedFactor = Mathf.Clamp01(1f - (speedKph / topSpeedKph));
        // effectiveMotor applies speed-based falloff so car doesn't accelerate infinitely
        float effectiveMotor = maxMotorTorque * motorInput * Mathf.Pow(speedFactor, motorTorqueCurve);

        // ---- STEERING SMOOTHING ----
        float targetSteer = steerInput * maxSteerAngle;
        // Smooth steering to avoid instant snap — makes it feel more natural
        currentSteer = Mathf.Lerp(currentSteer, targetSteer, Time.fixedDeltaTime * steerSpeed);

        // Apply steering to the front wheels (assumes first two wheel colliders are front-left/right)
        if (wheelColliders.Length >= 2)
        {
            wheelColliders[0].steerAngle = currentSteer;
            wheelColliders[1].steerAngle = currentSteer;
        }

        // ---- DRIVE & BRAKES ----
        ApplyDrive(effectiveMotor);
        ApplyBrakes(handbrake, motorInput);

        // ---- STABILITY HELPERS ----
        DoAntiRoll();
        // Add aerodynamic downforce proportional to speed (helps grip at higher speeds)
        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        // ---- VISUALS & EFFECTS ----
        UpdateWheelMeshes(); // sync visual wheels with WheelColliders
        HandleSkids();       // spawn smoke and skidmarks when slipping
    }

    // Applies motor torque to the selected driven wheels
    void ApplyDrive(float torque)
    {
        // Rear-drive: apply to indexes 2 (RL) and 3 (RR)
        if (rearWheelDrive)
        {
            if (wheelColliders.Length >= 4)
            {
                wheelColliders[2].motorTorque = torque;
                wheelColliders[3].motorTorque = torque;
            }
        }

        // Front-drive: apply to indexes 0 (FL) and 1 (FR)
        if (frontWheelDrive)
        {
            if (wheelColliders.Length >= 2)
            {
                wheelColliders[0].motorTorque = torque;
                wheelColliders[1].motorTorque = torque;
            }
        }

        // Fallback: if neither front nor rear selected, apply to all wheels (safe default)
        if (!rearWheelDrive && !frontWheelDrive)
        {
            for (int i = 0; i < wheelColliders.Length; i++)
                wheelColliders[i].motorTorque = torque;
        }
    }

    // Handles regular braking and handbrake behavior
    void ApplyBrakes(bool handbrake, float motorInput)
    {
        float brake = 0f;

        // Slight automatic brake to help stabilize when throttle is released while moving
        if (Mathf.Approximately(Input.GetAxisRaw("Vertical"), 0f) && rb.linearVelocity.magnitude > 1f)
            brake = 50f;

        // If player is pushing throttle opposite to travel direction, add stronger braking
        if (motorInput * Vector3.Dot(transform.forward, rb.linearVelocity) < -0.1f)
            brake = maxBrakeTorque * 0.5f;

        // Apply same brake to all wheels for simplicity
        for (int i = 0; i < wheelColliders.Length; i++)
        {
            wheelColliders[i].brakeTorque = brake;
        }

        // Handbrake: strong brake applied to rear wheels to induce skid / drift
        if (handbrake)
        {
            if (wheelColliders.Length >= 4)
            {
                wheelColliders[2].brakeTorque = handbrakeTorque; // RL
                wheelColliders[3].brakeTorque = handbrakeTorque; // RR

                // Also force skid effects on the rear wheels while handbrake is held
                TriggerEffects(2, true);
                TriggerEffects(3, true);
            }
        }
    }

    // Anti-roll implementation to reduce excessive body roll using suspension travel difference
    void DoAntiRoll()
    {
        if (wheelColliders.Length < 4) return;
        ApplyAntiRollPair(0, 1);
        ApplyAntiRollPair(2, 3);
    }

    // Applies anti-roll forces to a pair of wheels (left/right)
    void ApplyAntiRollPair(int leftIndex, int rightIndex)
    {
        WheelHit hit;
        float travelL = 1f;
        float travelR = 1f;

        // Measure suspension travel for left wheel
        bool groundedL = wheelColliders[leftIndex].GetGroundHit(out hit);
        if (groundedL)
            travelL = (-wheelColliders[leftIndex].transform.InverseTransformPoint(hit.point).y - wheelColliders[leftIndex].radius) / wheelColliders[leftIndex].suspensionDistance;

        // Measure suspension travel for right wheel
        bool groundedR = wheelColliders[rightIndex].GetGroundHit(out hit);
        if (groundedR)
            travelR = (-wheelColliders[rightIndex].transform.InverseTransformPoint(hit.point).y - wheelColliders[rightIndex].radius) / wheelColliders[rightIndex].suspensionDistance;

        // Difference in travel produces an anti-roll force
        float antiRollForce = (travelL - travelR) * antiRoll;

        if (groundedL)
            rb.AddForceAtPosition(wheelColliders[leftIndex].transform.up * -antiRollForce, wheelColliders[leftIndex].transform.position);
        if (groundedR)
            rb.AddForceAtPosition(wheelColliders[rightIndex].transform.up * antiRollForce, wheelColliders[rightIndex].transform.position);
    }

    // Synchronize visual wheel transforms with the physics WheelColliders
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

    // Check each wheel for sideways slip and trigger smoke and skid trail effects
    void HandleSkids()
    {
        for (int i = 0; i < wheelColliders.Length; i++)
        {
            if (wheelColliders[i] == null) continue;
            WheelHit hit;
            if (wheelColliders[i].GetGroundHit(out hit))
            {
                // sidewaysSlip is positive/negative depending on direction, take absolute value
                float sidewaysSlip = Mathf.Abs(hit.sidewaysSlip);
                bool isSkidding = sidewaysSlip > skidThreshold;

                TriggerEffects(i, isSkidding);
            }
            else
            {
                // Wheel not touching ground -> no effects
                TriggerEffects(i, false);
            }
        }
    }

    // Helper to start/stop smoke particle and skid trail emission for a wheel index
    void TriggerEffects(int index, bool state)
    {
        // Tire smoke: play/stop particle system
        if (tireSmoke.Length > index && tireSmoke[index] != null)
        {
            if (state && !tireSmoke[index].isPlaying)
                tireSmoke[index].Play();
            else if (!state && tireSmoke[index].isPlaying)
                tireSmoke[index].Stop();
        }

        // Skid trail: toggle TrailRenderer emission
        if (skidTrails.Length > index && skidTrails[index] != null)
        {
            skidTrails[index].emitting = state;
        }
    }

    #if UNITY_EDITOR
    // Convenience method (right-click component header -> Auto Configure WheelColliders)
    // Sets reasonable default values for WheelCollider suspension and friction so you can start tuning.
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
            forward.stiffness = 1.1f;
            wc.forwardFriction = forward;

            WheelFrictionCurve sideways = wc.sidewaysFriction;
            sideways.stiffness = 1.4f;
            wc.sidewaysFriction = sideways;
        }
        Debug.Log("WheelColliders auto-configured. Tweak values for desired feel.");
    }
    #endif
}
