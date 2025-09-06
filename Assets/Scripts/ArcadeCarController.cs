using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ArcadeCarController : MonoBehaviour
{
    [Header("Wheel Colliders (0:FL,1:FR,2:RL,3:RR)")]
    public WheelCollider[] wheelColliders = new WheelCollider[4];
    public Transform[] wheelMeshes = new Transform[4];

    [Header("Drive (separate forward/reverse)")]
    public float motorTorqueForward = 800f;    // forward torque magnitude
    public float motorTorqueReverse = 1400f;   // reverse (stronger) torque magnitude
    public float brakeTorque = 1500f;
    public float handbrakeTorque = 2500f;
    public float maxSpeed = 22f;               // forward top speed (m/s)
    public float maxReverseSpeed = 10f;        // reverse top speed (m/s)

    [Header("Steering & Drift")]
    public float maxSteerAngle = 30f;
    public float steerHelper = 0.5f;

    [Header("Friction / Drift tuning")]
    [Tooltip("Sideways slip threshold to consider a wheel 'skidding'")]
    public float skidSlipThreshold = 0.25f;
    [Range(0.1f, 1f)]
    public float driftStiffness = 0.8f;        // stiffness factor when handbrake engaged

    [Header("Rigidbody")]
    public Vector3 centerOfMassOffset = new Vector3(0f, -0.4f, 0f);
    public float downforce = 100f;

    [Header("Effects - Rear Left, Rear Right (indices map to wheel order)")]
    public TrailRenderer trailRL;
    public TrailRenderer trailRR;
    public ParticleSystem smokeRL;
    public ParticleSystem smokeRR;

    [Header("Misc")]
    public bool useHandbrake = true;

    Rigidbody rb;

    // internal
    private float inputSteer;
    private float inputThrottle;
    private bool inputHandbrake;
    private float currentSpeed;

    // store original sideways friction for rear wheels (2 and 3)
    private WheelFrictionCurve[] originalRearSideways = null;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass += centerOfMassOffset;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        // cache original rear sideways friction
        if (wheelColliders.Length >= 4)
        {
            originalRearSideways = new WheelFrictionCurve[2];
            originalRearSideways[0] = wheelColliders[2].sidewaysFriction;
            originalRearSideways[1] = wheelColliders[3].sidewaysFriction;
        }

        // Ensure trails are disabled initially and cleared
        if (trailRL) { trailRL.emitting = false; trailRL.Clear(); }
        if (trailRR) { trailRR.emitting = false; trailRR.Clear(); }
        if (smokeRL) { var e = smokeRL.emission; e.enabled = false; }
        if (smokeRR) { var e = smokeRR.emission; e.enabled = false; }
    }

    void Update()
    {
        inputSteer = Input.GetAxis("Horizontal");
        inputThrottle = Input.GetAxis("Vertical");
        inputHandbrake = useHandbrake && Input.GetKey(KeyCode.Space);

        UpdateWheelMeshes();
    }

    void FixedUpdate()
    {
        currentSpeed = rb.linearVelocity.magnitude;

        // Steering (less at high speed)
        float speedFactor = Mathf.Clamp01(1f - (currentSpeed / maxSpeed));
        float steer = maxSteerAngle * inputSteer * (0.5f + 0.5f * speedFactor);
        wheelColliders[0].steerAngle = steer;
        wheelColliders[1].steerAngle = steer;

        ApplyMotorAndBrakes();

        // downforce
        rb.AddForce(-transform.up * downforce * rb.linearVelocity.magnitude);

        // small stability helper
        SteerHelper();

        UpdateSkidEffects();
    }

    void ApplyMotorAndBrakes()
    {
        // compute forward velocity along car forward axis
        float velForward = Vector3.Dot(rb.linearVelocity, transform.forward);

        float throttle = inputThrottle; // -1..1

        // Decide motor torque depending on sign of throttle
        float appliedMotorTorque = 0f;
        if (throttle > 0f)
        {
            // if above forward top speed, don't add more forward torque
            if (velForward < maxSpeed)
                appliedMotorTorque = throttle * motorTorqueForward;
            else
                appliedMotorTorque = 0f;
        }
        else if (throttle < 0f)
        {
            // if above reverse max speed (in reverse direction), don't add more reverse torque
            if (velForward > -maxReverseSpeed || Mathf.Abs(velForward) < 0.5f) // allow to start reversing from near stop
                appliedMotorTorque = throttle * motorTorqueReverse; // throttle is negative so torque will be negative
            else
                appliedMotorTorque = 0f;
        }
        else
        {
            appliedMotorTorque = 0f;
        }

        // apply motor torque to rear wheels (RL and RR)
        wheelColliders[2].motorTorque = appliedMotorTorque;
        wheelColliders[3].motorTorque = appliedMotorTorque;

        // Braking logic:
        // Only apply heavy brake when player explicitly wants to brake or when velocity and input are opposite and car is moving reasonably fast.
        float appliedBrake = 0f;
        bool wantsBrake = false;
        // Player pressed opposite pedal: e.g. pressing forward while moving strongly backward, or pressing backward while moving forward
        if (Mathf.Abs(throttle) > 0.01f)
        {
            float dot = Mathf.Sign(throttle) * velForward;
            if (dot < -0.1f && Mathf.Abs(velForward) > 0.5f)
            {
                wantsBrake = true;
            }
        }

        if (wantsBrake)
            appliedBrake = brakeTorque;
        else
            appliedBrake = 0f;

        // Handbrake overrides and loosens rear friction
        if (inputHandbrake)
        {
            wheelColliders[2].brakeTorque = handbrakeTorque;
            wheelColliders[3].brakeTorque = handbrakeTorque;
            ReduceRearFriction(driftStiffness);
        }
        else
        {
            // set brake for all wheels only when braking explicitly (prevents accidental slow reverse after collisions)
            wheelColliders[0].brakeTorque = appliedBrake;
            wheelColliders[1].brakeTorque = appliedBrake;
            wheelColliders[2].brakeTorque = appliedBrake;
            wheelColliders[3].brakeTorque = appliedBrake;

            RestoreRearFriction();
        }
    }

    void ReduceRearFriction(float stiffnessFactor)
    {
        if (originalRearSideways == null) return;
        WheelFrictionCurve ws2 = wheelColliders[2].sidewaysFriction;
        WheelFrictionCurve ws3 = wheelColliders[3].sidewaysFriction;
        ws2.stiffness = originalRearSideways[0].stiffness * stiffnessFactor;
        ws3.stiffness = originalRearSideways[1].stiffness * stiffnessFactor;
        wheelColliders[2].sidewaysFriction = ws2;
        wheelColliders[3].sidewaysFriction = ws3;
    }

    void RestoreRearFriction()
    {
        if (originalRearSideways == null) return;
        wheelColliders[2].sidewaysFriction = originalRearSideways[0];
        wheelColliders[3].sidewaysFriction = originalRearSideways[1];
    }

    void UpdateWheelMeshes()
    {
        for (int i = 0; i < 4; i++)
        {
            if (wheelMeshes == null || wheelMeshes.Length <= i) continue;
            if (wheelMeshes[i] == null || wheelColliders[i] == null) continue;
            Vector3 pos;
            Quaternion rot;
            wheelColliders[i].GetWorldPose(out pos, out rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    void SteerHelper()
    {
        if (Mathf.Abs(inputSteer) > 0.01f)
        {
            Vector3 vel = rb.linearVelocity;
            Vector3 localVel = transform.InverseTransformDirection(vel);
            localVel.x *= 1f - steerHelper * (rb.linearVelocity.magnitude / maxSpeed);
            rb.linearVelocity = transform.TransformDirection(localVel);
        }
    }

    // Returns true if the specified wheel (index) is skidding (enough sideways slip)
    bool IsWheelSkidding(int wheelIndex)
    {
        if (wheelColliders[wheelIndex] == null) return false;
        WheelHit hit;
        if (wheelColliders[wheelIndex].GetGroundHit(out hit))
        {
            // only count real sideways slip above threshold and wheel is grounded
            if (Mathf.Abs(hit.sidewaysSlip) > skidSlipThreshold)
                return true;
        }
        return false;
    }

    void UpdateSkidEffects()
    {
        // Only rear wheels produce smoke/trail (2 = RL, 3 = RR)
        bool skidRL = IsWheelSkidding(2);
        bool skidRR = IsWheelSkidding(3);

        ToggleTrail(trailRL, skidRL);
        ToggleTrail(trailRR, skidRR);
        ToggleSmoke(smokeRL, skidRL);
        ToggleSmoke(smokeRR, skidRR);
    }

    void ToggleTrail(TrailRenderer t, bool on)
    {
        if (t == null) return;
        if (on && !t.emitting)
        {
            t.Clear();   // clear previous trail then enable to avoid ghost
            t.emitting = true;
        }
        else if (!on && t.emitting)
        {
            t.emitting = false;
            // keep one last frame? we clear so player doesn't see ghost lines
            t.Clear();
        }
    }

    void ToggleSmoke(ParticleSystem ps, bool on)
    {
        if (ps == null) return;
        var emission = ps.emission;
        if (on && !ps.isPlaying)
        {
            emission.enabled = true;
            ps.Play(true);
        }
        else if (!on && ps.isPlaying)
        {
            emission.enabled = false;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear();
        }
    }
}
