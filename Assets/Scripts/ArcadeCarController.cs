using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem; // Optional: New Input System
#endif

[RequireComponent(typeof(Rigidbody))]
public class ArcadeCarController : MonoBehaviour
{
    [Header("Speed & Acceleration")]
    [Tooltip("Top forward speed in m/s.")]
    public float maxSpeed = 30f;
    [Tooltip("Top reverse speed in m/s.")]
    public float maxReverseSpeed = 15f;
    [Tooltip("Forward accel (m/s^2).")]
    public float acceleration = 45f;
    [Tooltip("Braking / reverse accel (m/s^2).")]
    public float brakeAcceleration = 60f;

    [Header("Steering")]
    [Tooltip("Base steer rate in deg/s at low speed.")]
    public float baseSteerRate = 120f;
    [Tooltip("How quickly angular velocity y blends to target.")]
    public float steerResponsiveness = 8f;
    [Tooltip("Reduce steer at high speed (0..1 contribution).")]
    public float highSpeedSteerFactor = 0.5f;

    [Header("Grip & Drift")]
    [Tooltip("Lateral velocity damping strength.")]
    public float lateralGrip = 6f;
    [Tooltip("Grip multiplier while drifting (hold drift).")]
    public float driftGripMultiplier = 0.35f;

    [Header("Downforce & Gravity")]
    [Tooltip("Downforce coefficient per m/s.")]
    public float downforcePerSpeed = 0.6f;
    [Tooltip("Extra gravity scale (1 = default gravity).")]
    public float gravityMultiplier = 1.2f;

    [Header("Drag")]
    [Tooltip("Rigidbody.drag when grounded.")]
    public float groundDrag = 0.4f;
    [Tooltip("Rigidbody.drag when airborne.")]
    public float airDrag = 0.05f;
    [Tooltip("Rigidbody.angularDrag (always applied).")]
    public float angularDrag = 2f;

    [Header("Grounding")]
    [Tooltip("Ray length to detect ground below car.")]
    public float groundRayLength = 1.5f;
    [Tooltip("Layers treated as drivable ground.")]
    public LayerMask groundMask = ~0;
    [Tooltip("How fast the car tilts to ground normal.")]
    public float groundTiltLerp = 10f;

    [Header("Wheel Visuals")]
    public Transform wheelFL;
    public Transform wheelFR;
    public Transform wheelRL;
    public Transform wheelRR;
    [Tooltip("Approximate wheel radius in meters.")]
    public float wheelRadius = 0.35f;

    Rigidbody rb;
    bool grounded;
    Vector3 groundNormal = Vector3.up;

    // Inputs
    Vector2 moveInput; // x = steer (-1..1), y = throttle (-1..1)
    bool driftHeld;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.angularDamping = angularDrag;
        rb.linearDamping = groundDrag;
    }

    void Update()
    {
        // Read inputs (works with old Input Manager; optional new Input System below).
#if ENABLE_INPUT_SYSTEM
        // If Input System is installed, read from Keyboard (or map to your InputActions).
        float steer = 0f;
        float throttle = 0f;

        if (Keyboard.current != null)
        {
            steer += Keyboard.current.aKey.isPressed ? -1f : 0f;
            steer += Keyboard.current.dKey.isPressed ? +1f : 0f;

            throttle += Keyboard.current.sKey.isPressed ? -1f : 0f;
            throttle += Keyboard.current.wKey.isPressed ? +1f : 0f;

            driftHeld = Keyboard.current.spaceKey.isPressed;
        }

        moveInput = Vector2.ClampMagnitude(new Vector2(steer, throttle), 1f);
#else
        moveInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        driftHeld = Input.GetKey(KeyCode.Space);
#endif
    }

    void FixedUpdate()
    {
        // 1) Ground check and tilt towards ground normal for stable visuals/handling.
        DoGroundCheckAndTilt();

        // 2) Compute local velocity for intuitive control.
        Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
        float speedForward = localVel.z;
        float speedAbs = new Vector2(localVel.x, localVel.z).magnitude;

        // 3) Throttle/brake forces (arcade accel along forward).
        float accel = moveInput.y >= 0f ? acceleration * moveInput.y : brakeAcceleration * moveInput.y;
        rb.AddForce(transform.forward * accel, ForceMode.Acceleration);

        // 4) Speed limiting (forward vs reverse).
        float maxF = moveInput.y >= 0f ? maxSpeed : maxReverseSpeed;
        Vector3 flatVel = Vector3.ProjectOnPlane(rb.linearVelocity, Vector3.up);
        float flatSpeed = flatVel.magnitude;
        if (flatSpeed > maxF)
        {
            // Nudge back toward cap without hard snapping.
            Vector3 excessDir = flatVel.normalized;
            float excess = flatSpeed - maxF;
            rb.AddForce(-excessDir * excess * 5f, ForceMode.Acceleration);
        }

        // 5) Lateral grip and drift control (damp sideways slide).
        float grip = lateralGrip * (driftHeld ? driftGripMultiplier : 1f);
        localVel.x = Mathf.Lerp(localVel.x, 0f, grip * Time.fixedDeltaTime);
        rb.linearVelocity = transform.TransformDirection(localVel);

        // 6) Steering by shaping angular velocity around Y (strong at low speed, milder at high).
        float speedFactor = Mathf.Clamp01(flatSpeed / Mathf.Max(1f, maxSpeed));
        float steerRate = Mathf.Lerp(baseSteerRate, baseSteerRate * highSpeedSteerFactor, speedFactor);
        float targetYawRateDeg = moveInput.x * steerRate;
        float targetYawRateRad = targetYawRateDeg * Mathf.Deg2Rad;

        Vector3 angVel = rb.angularVelocity;
        float newYaw = Mathf.Lerp(angVel.y, targetYawRateRad, steerResponsiveness * Time.fixedDeltaTime);
        rb.angularVelocity = new Vector3(0f, newYaw, 0f);

        // 7) Downforce scales with speed when grounded for planted feel.
        if (grounded)
        {
            rb.AddForce(-transform.up * (flatSpeed * downforcePerSpeed), ForceMode.Acceleration);
        }

        // 8) Extra gravity keeps car settled over bumps/airtime.
        rb.AddForce(Physics.gravity * (gravityMultiplier - 1f), ForceMode.Acceleration);

        // 9) Adjust drag by state.
        rb.linearDamping = grounded ? groundDrag : airDrag;

        // 10) Spin wheel visuals.
        SpinWheelVisuals(speedForward);
    }

    void DoGroundCheckAndTilt()
    {
        Ray ray = new Ray(transform.position + Vector3.up * 0.1f, -transform.up);
        if (Physics.Raycast(ray, out RaycastHit hit, groundRayLength, groundMask, QueryTriggerInteraction.Ignore))
        {
            grounded = true;
            groundNormal = hit.normal;

            // Smoothly tilt car up-vector toward ground normal.
            Quaternion toGround = Quaternion.FromToRotation(transform.up, groundNormal) * transform.rotation;
            Quaternion target = Quaternion.Slerp(transform.rotation, toGround, groundTiltLerp * Time.fixedDeltaTime);

            // For an arcade feel we can directly set rotation; for stricter physics use torque alignment.
            rb.MoveRotation(target);
        }
        else
        {
            grounded = false;
            groundNormal = Vector3.up;
        }
    }

    void SpinWheelVisuals(float forwardSpeed)
    {
        if (wheelRadius <= 0.0001f) return;
        float omega = (forwardSpeed / wheelRadius) * Mathf.Rad2Deg * Time.fixedDeltaTime;
        if (wheelFL) wheelFL.Rotate(Vector3.right, omega, Space.Self);
        if (wheelFR) wheelFR.Rotate(Vector3.right, omega, Space.Self);
        if (wheelRL) wheelRL.Rotate(Vector3.right, omega, Space.Self);
        if (wheelRR) wheelRR.Rotate(Vector3.right, omega, Space.Self);
    }
}
