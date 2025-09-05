using UnityEngine;

/// <summary>
/// Smooth third-person chase camera designed for arcade car games.
/// Features:
/// - Smooth position & rotation damping
/// - Velocity-based look-ahead (anticipates where the car is going)
/// - Obstruction avoidance using SphereCast (prevents clipping into environment)
/// - Dynamic FOV based on speed (gives sensation of speed)
/// - Simple camera shake API for collisions/nitro
///
/// Attach to your Scene Camera object and assign the `target` to your car's body or a dedicated camera pivot.
/// </summary>
public class CarCameraController : MonoBehaviour
{
    [Header("Target")]
    public Transform target;                   // Car transform or a pivot point on the car (recommended)
    public Rigidbody targetRigidbody;          // optional: used for speed/lookahead if assigned

    [Header("Positioning")]
    public Vector3 targetOffset = new Vector3(0f, 1.2f, 0f); // where the camera looks relative to target
    public float distance = 6f;               // base distance behind the car
    public float height = 2f;                 // vertical offset above target position
    public float minDistance = 1.6f;          // if obstruction forces camera closer, don't go below this

    [Header("Damping")]
    public float positionDamping = 6f;        // smoothness for camera movement
    public float rotationDamping = 8f;        // smoothness for camera rotation

    [Header("Look-Ahead")]
    public float lookAheadDistance = 2.5f;    // how far ahead (in meters) camera will look based on velocity
    public float lookAheadSpeedMultiplier = 0.02f; // scales look-ahead by speed (kph -> meters)

    [Header("Collision Avoidance")]
    public LayerMask obstructionMask = ~0;    // layers to consider as obstacles (default: everything)
    public float clipSphereRadius = 0.35f;    // sphere radius used for spherecast

    [Header("Dynamic FOV")]
    public float baseFov = 60f;               // default field of view
    public float maxFov = 75f;                // max FOV at high speed
    public float fovSpeedKph = 120f;          // speed (kph) at which FOV reaches max
    public float fovDamping = 3f;             // how quickly FOV interpolates

    [Header("Shake")]
    public float defaultShakeDuration = 0.45f;
    public float defaultShakeMagnitude = 0.12f;

    // internal
    Vector3 currentVelocity = Vector3.zero;
    Camera cam;
    Vector3 shakeOffset = Vector3.zero;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogWarning("CarCameraController should be attached to a Camera object.", this);
            cam = Camera.main;
        }

        // Try to auto-assign rigidbody if not provided
        if (target != null && targetRigidbody == null)
        {
            targetRigidbody = target.GetComponentInParent<Rigidbody>();
        }
    }

    void LateUpdate()
    {
        if (target == null) return;

        // Determine look-ahead amount based on car speed (use kph for easier tuning)
        float speedKph = 0f;
        if (targetRigidbody != null)
            speedKph = targetRigidbody.linearVelocity.magnitude * 3.6f;

        float lookAhead = Mathf.Clamp(speedKph * lookAheadSpeedMultiplier, 0f, lookAheadDistance);

        // Desired focus point = target position + offset + forward look-ahead
        Vector3 focusPoint = target.position + target.TransformDirection(targetOffset) + target.forward * lookAhead;

        // Desired camera position (behind the car at distance, with height)
        Vector3 desiredPos = target.position + target.TransformDirection(new Vector3(0f, height, -distance + -lookAhead * 0.1f));

        // Avoid obstacles: spherecast from focus point toward desired position
        Vector3 toCamera = desiredPos - focusPoint;
        float fullDist = toCamera.magnitude;

        if (fullDist > 0.001f)
        {
            RaycastHit hit;
            Vector3 dir = toCamera.normalized;

            // SphereCast returns true if an obstacle is between the focus point and desired camera position
            if (Physics.SphereCast(focusPoint, clipSphereRadius, dir, out hit, fullDist, obstructionMask))
            {
                // place camera slightly in front of collision point so it doesn't clip
                desiredPos = focusPoint + dir * Mathf.Max(hit.distance - clipSphereRadius, minDistance);
            }
        }

        // Smoothly move camera toward desired position
        transform.position = Vector3.SmoothDamp(transform.position, desiredPos + shakeOffset, ref currentVelocity, 1f / Mathf.Max(0.0001f, positionDamping));

        // Smooth rotation: look at focus point
        Quaternion desiredRot = Quaternion.LookRotation((focusPoint - transform.position).normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, Time.deltaTime * rotationDamping);

        // Dynamic FOV based on speed
        if (cam != null)
        {
            float targetFov = baseFov + (maxFov - baseFov) * Mathf.Clamp01(speedKph / Mathf.Max(1f, fovSpeedKph));
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, Time.deltaTime * fovDamping);
        }
    }

    /// <summary>
    /// Public API: call to trigger a quick camera shake (collision, explosion, nitro)
    /// </summary>
    public void Shake(float magnitude = -1f, float duration = -1f)
    {
        float mag = (magnitude <= 0f) ? defaultShakeMagnitude : magnitude;
        float dur = (duration <= 0f) ? defaultShakeDuration : duration;
        StopAllCoroutines();
        StartCoroutine(DoShake(mag, dur));
    }

    System.Collections.IEnumerator DoShake(float magnitude, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float x = (Random.value * 2f - 1f) * magnitude;
            float y = (Random.value * 2f - 1f) * magnitude * 0.6f; // less vertical shake
            // small forward/backward jitter as well
            float z = (Random.value * 2f - 1f) * magnitude * 0.08f;

            shakeOffset = transform.right * x + transform.up * y + transform.forward * z;
            elapsed += Time.deltaTime;
            yield return null;
        }

        shakeOffset = Vector3.zero;
    }

    // Optional editor helper to center camera to default desired pos quickly
    #if UNITY_EDITOR
    [ContextMenu("Snap to Default Position")]
    void SnapToDefaultPosition()
    {
        if (target == null) return;
        float speedKph = 0f;
        if (targetRigidbody != null) speedKph = targetRigidbody.linearVelocity.magnitude * 3.6f;
        float lookAhead = Mathf.Clamp(speedKph * lookAheadSpeedMultiplier, 0f, lookAheadDistance);
        Vector3 desiredPos = target.position + target.TransformDirection(new Vector3(0f, height, -distance + -lookAhead * 0.1f));
        transform.position = desiredPos;
        transform.LookAt(target.position + target.TransformDirection(targetOffset));
    }
    #endif
}
