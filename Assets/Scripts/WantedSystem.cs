using UnityEngine;

// ==================================================================
// WANTED SYSTEM - heat meter -> star level
// ------------------------------------------------------------------
// A single continuous "heat" value (0..maxStars*heatPerStar) drives a
// discrete star count. Heat only rises from explicit triggers -
// PlayerWantedTrigger calling AddHeat() on a hard hit, or any other
// script calling it (a mission trigger, a scripted "start a chase"
// button, whatever) - it never rises just from the passage of time.
//
// Losing the cops: this AI tracks the player by position/path data,
// not simulated vision, so a classic "break line of sight" mechanic
// doesn't fit honestly. Instead: heat drains automatically once the
// player has been farther than `evadeDistance` from EVERY active cop
// continuously - the moment a cop gets close again, draining pauses.
// That's an honest match for how the chase AI actually works.
//
// CopSpawner reads `Stars` every frame to decide how many cops should
// be active; nothing here spawns anything directly, keeping the
// heat/star model decoupled from the spawning implementation.
// ==================================================================

public class WantedSystem : MonoBehaviour
{
    public static WantedSystem Instance { get; private set; }

    [Header("References")]
    public Transform player;

    [Header("Stars")]
    public int maxStars = 5;
    [Tooltip("Heat needed to fill one star.")]
    public float heatPerStar = 100f;

    [Header("Evasion (losing the cops)")]
    [Tooltip("No cop within this distance of the player counts as 'clear.'")]
    public float evadeDistance = 45f;
    [Tooltip("Heat drained per second while clear of every cop. Draining pauses entirely (holds steady) whenever any cop is within evadeDistance.")]
    public float heatDecayPerSecond = 8f;

    public int Stars { get; private set; }
    public float Heat { get; private set; }
    public bool IsClear { get; private set; }

    // Subscribe from a HUD script later: WantedSystem.Instance.OnStarsChanged += star => ...
    public event System.Action<int> OnStarsChanged;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"{name}: a second WantedSystem exists - destroying this duplicate. Only one should be in the scene.", this);
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (player == null) return;

        float distToNearestCop = CopCarAI.DistanceToNearestCop(player.position);
        IsClear = distToNearestCop > evadeDistance;

        if (IsClear && Heat > 0f)
        {
            Heat = Mathf.Max(0f, Heat - heatDecayPerSecond * Time.deltaTime);
            RecomputeStars();
        }
    }

    // Call from anywhere: PlayerWantedTrigger on a hard hit, a mission
    // script, a debug key - all of it just adds heat, this system doesn't
    // care about the source.
    public void AddHeat(float amount)
    {
        if (amount <= 0f) return;
        Heat = Mathf.Clamp(Heat + amount, 0f, heatPerStar * maxStars);
        RecomputeStars();
    }

    // Instantly clears the chase (e.g. a "safehouse" trigger, or a debug reset).
    public void ClearWanted()
    {
        Heat = 0f;
        RecomputeStars();
    }

    void RecomputeStars()
    {
        int newStars = Heat <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(Heat / heatPerStar), 0, maxStars);
        if (newStars != Stars)
        {
            Stars = newStars;
            OnStarsChanged?.Invoke(Stars);
        }
    }

    void OnDrawGizmosSelected()
    {
        if (player == null) return;
        Gizmos.color = IsClear ? new Color(0.2f, 1f, 0.3f, 0.8f) : new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(player.position, evadeDistance);
    }
}