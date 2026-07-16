using UnityEngine;

// ==================================================================
// PLAYER WANTED TRIGGER
// ------------------------------------------------------------------
// Turns hard collisions into WantedSystem heat. Deliberately a
// separate component from ArcadeCarController - that script should
// only ever know about driving physics, not game-design concepts like
// "wanted level."
//
// Add to the player car alongside ArcadeCarController.
// ==================================================================

public class PlayerWantedTrigger : MonoBehaviour
{
    [Header("Heat Amounts")]
    public string copTag = "Cop";
    public float copRamHeat = 35f;
    public float trafficHitHeat = 20f;

    [Header("Filtering")]
    [Tooltip("Collisions below this impulse are ignored - filters out gentle taps/scrapes.")]
    public float minImpulseForHeat = 800f;
    [Tooltip("Minimum time between heat-triggering hits, so one crunching multi-frame collision doesn't count several times over.")]
    public float heatCooldown = 0.5f;

    float cooldownTimer;

    void Update()
    {
        if (cooldownTimer > 0f) cooldownTimer -= Time.deltaTime;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (cooldownTimer > 0f) return;
        if (WantedSystem.Instance == null) return;

        float impulse = collision.impulse.magnitude;
        if (impulse < minImpulseForHeat) return;

        if (collision.gameObject.CompareTag(copTag))
        {
            WantedSystem.Instance.AddHeat(copRamHeat);
            cooldownTimer = heatCooldown;
        }
        else if (collision.gameObject.GetComponentInParent<TrafficCar>() != null)
        {
            WantedSystem.Instance.AddHeat(trafficHitHeat);
            cooldownTimer = heatCooldown;
        }
    }
}