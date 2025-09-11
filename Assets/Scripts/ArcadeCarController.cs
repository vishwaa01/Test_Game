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

    [Header("Skid Detection")]
    public float skidSlipThreshold = 0.20f;
    public float minHitForce = 5f;
    public float skidHoldTime = 0.12f;
    public float emissionFadeSpeed = 200f;

    [Header("Effects - Rear (0 = RL, 1 = RR)")]
    public TrailRenderer[] rearTrails = new TrailRenderer[2];
    public ParticleSystem[] rearSmokes = new ParticleSystem[2];
    public float smokeEmissionRate = 60f;

    [Header("Rigidbody")]
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
    [Tooltip("Image set to 'Filled' (radial) - this will fill with speed.")]
    public Image speedFillImage;
    [Tooltip("Minimum rotation angle for needle (e.g. -120)")]
    public float needleMinAngle = -120f;
    [Tooltip("Maximum rotation angle for needle (e.g. 120)")]
    public float needleMaxAngle = 120f;
    [Tooltip("How quickly the needle & fill smoothly move (higher = faster)")]
    public float needleSmoothSpeed = 6f;

    // runtime
    Rigidbody rb;
    float inputSteer;
    float inputThrottle;
    float currentSpeed;

    // skid timers and emission states per rear wheel
    private float[] skidTimers = new float[2];
    private float[] currentSmokeRate = new float[2];
    private float[] targetSmokeRate = new float[2];

    // smoothing state for UI
    private float uiNeedleAngleCurrent = 0f;
    private float uiFillCurrent = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass += centerOfMassOffset;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        // initialize fx state
        for (int i = 0; i < 2; i++)
        {
            skidTimers[i] = 0f;
            currentSmokeRate[i] = 0f;
            targetSmokeRate[i] = 0f;

            if (rearTrails != null && rearTrails.Length > i && rearTrails[i] != null)
                rearTrails[i].emitting = false;

            if (rearSmokes != null && rearSmokes.Length > i && rearSmokes[i] != null)
            {
                var em = rearSmokes[i].emission;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                rearSmokes[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        // init UI smoother values
        uiNeedleAngleCurrent = needleMinAngle;
        uiFillCurrent = 0f;

        // validate speedFillImage: if set, ensure Image.type = Filled (we will still work if user forgot, but note in logs)
#if UNITY_EDITOR
        if (speedFillImage != null && speedFillImage.type != Image.Type.Filled)
            Debug.LogWarning("ArcadeCarController: speedFillImage should be Image.Type = Filled (radial) for proper behavior.");
#endif
    }

    void Update()
    {
        inputSteer = Input.GetAxis("Horizontal");
        inputThrottle = Input.GetAxis("Vertical");
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

    Vector3 GetWheelContactPoint(int wheelIndex)
    {
        if (wheelColliders == null || wheelIndex < 0 || wheelIndex >= wheelColliders.Length)
            return transform.position;

        WheelCollider wc = wheelColliders[wheelIndex];
        WheelHit hit;
        if (wc.GetGroundHit(out hit))
        {
            return hit.point;
        }

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
            if (hit.force > minHitForce && Mathf.Abs(hit.sidewaysSlip) > skidSlipThreshold)
                return true;
        }
        return false;
    }

    void UpdateSkidEffects()
    {
        bool rawRL = RawWheelSkid(2, out WheelHit hitRL);
        bool rawRR = RawWheelSkid(3, out WheelHit hitRR);

        skidTimers[0] = rawRL ? skidHoldTime : Mathf.Max(0f, skidTimers[0] - Time.fixedDeltaTime);
        skidTimers[1] = rawRR ? skidHoldTime : Mathf.Max(0f, skidTimers[1] - Time.fixedDeltaTime);

        bool skidActiveRL = skidTimers[0] > 0f;
        bool skidActiveRR = skidTimers[1] > 0f;

        for (int i = 0; i < 2; i++)
        {
            int wheelIndex = 2 + i;
            Vector3 contact = GetWheelContactPoint(wheelIndex);

            if (rearTrails != null && i < rearTrails.Length && rearTrails[i] != null)
            {
                rearTrails[i].transform.position = contact;
                rearTrails[i].emitting = (i == 0) ? skidActiveRL : skidActiveRR;
            }

            if (rearSmokes != null && i < rearSmokes.Length && rearSmokes[i] != null)
            {
                rearSmokes[i].transform.position = contact;
                targetSmokeRate[i] = (i == 0) ? (skidActiveRL ? smokeEmissionRate : 0f) : (skidActiveRR ? smokeEmissionRate : 0f);
                if (targetSmokeRate[i] > 0f && !rearSmokes[i].isPlaying) rearSmokes[i].Play(true);
            }
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

        // needle & fill mapping
        // compute target angle and fill amount from normalized vehicleSpeed (0..1)
        float targetAngle = Mathf.Lerp(needleMinAngle, needleMaxAngle, vehicleSpeed);
        float targetFill = Mathf.Clamp01(vehicleSpeed);

        // smooth values
        uiNeedleAngleCurrent = Mathf.LerpAngle(uiNeedleAngleCurrent, targetAngle, Mathf.Clamp01(needleSmoothSpeed * Time.deltaTime));
        uiFillCurrent = Mathf.MoveTowards(uiFillCurrent, targetFill, needleSmoothSpeed * Time.deltaTime);

        // apply rotation to needle (around Z)
        if (needleTransform != null)
        {
            needleTransform.localEulerAngles = new Vector3(0f, 0f, uiNeedleAngleCurrent);
        }

        // apply fill to image (Image.type must be Filled)
        if (speedFillImage != null)
        {
            // prefer setting fillAmount only; user must set image type = Filled in inspector
            speedFillImage.fillAmount = uiFillCurrent;
        }
    }

#if UNITY_EDITOR
    // Press K in Play mode to log rear slips/force (useful for tuning skidSlipThreshold)
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
