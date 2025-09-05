// CarCameraController.cs
// Features:
// - Modes: Chase, Orbit, Hood
// - Smooth position/rotation (LateUpdate) with SmoothDamp/Slerp
// - Obstruction handling via Physics.SphereCast
// - Speed-based FOV and velocity look-ahead
// - Tunable clamps and sensitivities

using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CarCameraController : MonoBehaviour
{
    public enum CameraMode { Chase, Orbit, Hood }

    [Header("Targets")]
    public Transform target;                 // Car root
    public Rigidbody targetRigidbody;        // Optional for velocity-based features
    public Transform hoodAnchor;             // Optional anchor for hood/first-person

    [Header("Mode")]
    public CameraMode mode = CameraMode.Chase;
    public bool alignYawToTargetInChase = true;  // Align yaw to car in Chase
    public bool allowInputInChase = true;        // Add manual orbit offset while in Chase

    [Header("Positioning")]
    public float distance = 6f;
    public float minDistance = 1.2f;
    public float height = 1.5f;

    [Header("Smoothing")]
    public float positionSmoothTime = 0.05f; // Lower = tighter follow
    public float rotationLerpSpeed = 12f;    // Higher = snappier rotation

    [Header("Orbit/Input")]
    public float yawSensitivity = 120f;
    public float pitchSensitivity = 90f;
    public Vector2 pitchLimits = new Vector2(-20f, 60f); // degrees

    [Header("Collision")]
    public LayerMask obstructionMask = ~0;
    public float collisionRadius = 0.3f;
    public float collisionBuffer = 0.2f;

    [Header("Look-Ahead")]
    public float lookAheadFactor = 0.02f;    // Meters of offset per m/s
    public float lookAheadMaxSpeed = 40f;    // Clamp velocity contribution

    [Header("Dynamic FOV")]
    public float baseFOV = 60f;
    public float fovMaxAdd = 20f;
    public float fovSpeedFactor = 0.5f;      // FOV added per m/s
    public float fovSmoothTime = 0.2f;

    [Header("Custom Offset")]
    public float cameraZOffset = 0f; // <-- New offset variable

    // Internals
    Camera cam;
    float yaw, pitch;
    float yawVel;
    Vector3 posVel;
    float fovVel;
    Vector3 lastTargetPos;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (target)
        {
            var e = target.rotation.eulerAngles;
            yaw = e.y;
            pitch = Mathf.Clamp(50f, pitchLimits.x, pitchLimits.y);
            lastTargetPos = target.position;
        }
        if (cam != null) cam.fieldOfView = baseFOV;
    }

    void Update()
    {
        // Gather input each frame for responsiveness.
        float mx = Input.GetAxisRaw("Mouse X");
        float my = Input.GetAxisRaw("Mouse Y");

        if (mode == CameraMode.Orbit || (mode == CameraMode.Chase && allowInputInChase))
        {
            yaw += mx * yawSensitivity * Time.unscaledDeltaTime;
            pitch -= my * pitchSensitivity * Time.unscaledDeltaTime;
            pitch = Mathf.Clamp(pitch, pitchLimits.x, pitchLimits.y);
        }
    }

    void LateUpdate()
    {
        if (!target) return;

        // Optionally auto-align yaw to target heading in Chase mode.
        if (mode == CameraMode.Chase && alignYawToTargetInChase)
        {
            float targetYaw = target.rotation.eulerAngles.y;
            yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVel, 0.08f);
        }

        // Compute look-ahead from velocity.
        Vector3 vel = Vector3.zero;
        if (targetRigidbody)
        {
            vel = targetRigidbody.linearVelocity;
        }
        else
        {
            // Fallback velocity estimate if no Rigidbody provided.
            vel = (target.position - lastTargetPos) / Mathf.Max(Time.deltaTime, 1e-6f);
        }
        Vector3 lookAhead = Vector3.ClampMagnitude(vel, lookAheadMaxSpeed) * lookAheadFactor;

        // Anchor the camera around a target height.
        Vector3 focus = target.position + Vector3.up * height;

        // Desired camera pose by mode.
        Vector3 desiredPos;
        Quaternion desiredRot;

        if (mode == CameraMode.Hood && hoodAnchor != null)
        {
            desiredPos = hoodAnchor.position;
            desiredRot = Quaternion.LookRotation((focus + lookAhead) - desiredPos, Vector3.up);
        }
        else
        {
            // Use orbit angles to position the boom.
            Quaternion orbitRot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 boom = orbitRot * new Vector3(0f, 0f, -distance + cameraZOffset); // <-- Apply offset here
            desiredPos = focus + boom;

            // Aim at the focus with look-ahead.
            desiredRot = Quaternion.LookRotation((focus + lookAhead) - desiredPos, Vector3.up);
        }

        // Obstruction handling: sphere cast from focus toward desiredPos.
        Vector3 toCam = desiredPos - focus;
        float desiredDist = toCam.magnitude;
        Vector3 dir = desiredDist > 1e-3f ? toCam / desiredDist : transform.forward;

        if (Physics.SphereCast(focus, collisionRadius, dir, out RaycastHit hit, desiredDist, obstructionMask, QueryTriggerInteraction.Ignore))
        {
            float blockedDist = Mathf.Max(hit.distance - collisionBuffer, minDistance);
            desiredPos = focus + dir * blockedDist;
        }

        // Smooth position.
        Vector3 newPos = Vector3.SmoothDamp(transform.position, desiredPos, ref posVel, positionSmoothTime);

        // Smooth rotation.
        Quaternion newRot = Quaternion.Slerp(transform.rotation, desiredRot, 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime));

        // Apply.
        transform.SetPositionAndRotation(newPos, newRot);

        // Dynamic FOV based on speed.
        float speed = vel.magnitude;
        float targetFOV = Mathf.Clamp(baseFOV + speed * fovSpeedFactor, baseFOV, baseFOV + fovMaxAdd);
        cam.fieldOfView = Mathf.SmoothDamp(cam.fieldOfView, targetFOV, ref fovVel, fovSmoothTime);

        lastTargetPos = target.position;
    }
}
