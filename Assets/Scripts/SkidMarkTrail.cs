using System.Collections.Generic;
using UnityEngine;

// ==================================================================
// SKID MARK TRAIL - TrailRenderer-based tire marks, managed from code
// ------------------------------------------------------------------
// Uses Unity's native TrailRenderer for the actual ribbon (smooth,
// battle-tested rendering) but manages everything around it from
// script so the classic TrailRenderer faults can't happen:
//
//  - NO connect-line artifact: every skid episode gets its own pooled
//    TrailRenderer instance. It's positioned FIRST, then Clear()ed,
//    then enabled - that exact order resets the trail's internal
//    position memory, so the first segment can never draw a stray
//    line back to wherever the instance last sat.
//  - NO fragmenting into many stacked pieces: the underlying skid
//    detection can flicker true/false several times during ONE real
//    turn (perfectly normal right at a slip threshold). A brief gap
//    (< endGraceTime) keeps extending the SAME trail through it
//    instead of ending it and starting a new overlapping fragment -
//    only a SUSTAINED stop actually finalizes a mark. This is what
//    was producing the "stacked trails" / "only one side lighting up"
//    look - many short fragments from one turn, each independently
//    timed, visually reading as one long trail that outlives its
//    own markLifetime.
//  - Settings are re-applied every time a trail is reused from the
//    pool, not just once at creation - so changing markLifetime (or
//    anything else) always takes effect on the next mark, instead of
//    already-pooled instances silently keeping stale values forever.
//  - NO height jitter: the wheel's ground-hit Y is noisy per physics
//    step, and that vertical noise is the #1 source of visible
//    shimmer in ground trails. The emitter's Y is exponentially
//    smoothed; X/Z stay exact so the mark's path is precise.
//  - NO twisting/billboarding: alignment is TransformZ and the
//    emitter's Z axis is re-asserted straight up every update, so the
//    ribbon lies flat on the road from any camera angle, even while
//    the car pitches/rolls.
//  - Misconfiguration-proof: every TrailRenderer property is set in
//    code. If no material is assigned, a working dark translucent one
//    is created automatically instead of rendering magenta.
//
// Setup: one child GameObject per rear wheel with this component
// (position irrelevant - it's a container for pooled trails), wired
// into the controller's rearSkidMarks array. Optionally assign
// trailMaterial; everything else works out of the box.
//
// The public API (UpdateMark) is unchanged, so ArcadeCarController /
// CopCarController need no edits beyond the L/R sync change noted
// separately in their own UpdateSkidEffects.
// ==================================================================

public class SkidMarkTrail : MonoBehaviour
{
    [Header("Appearance")]
    [Tooltip("Optional. Leave empty and a working dark translucent material is created automatically. If assigning your own, use a shader that supports vertex color + transparency (e.g. Sprites/Default or a URP/Particles Unlit) so the tail fade works.")]
    public Material trailMaterial;
    public float width = 0.18f;
    [Tooltip("Opacity of the mark at the fresh end. It fades to zero toward the tail.")]
    [Range(0f, 1f)] public float markAlpha = 0.8f;
    [Tooltip("Small lift off the road surface to avoid z-fighting flicker with the ground.")]
    public float groundOffset = 0.02f;

    [Header("Behavior")]
    [Tooltip("How long each piece of mark remains before dissolving away, tail-first - this is the TrailRenderer 'time'.")]
    public float markLifetime = 1f;
    [Tooltip("Minimum distance the emitter must travel before a new trail vertex is added. Too small = dense noisy vertices; too big = visibly segmented curves.")]
    public float minVertexDistance = 0.12f;
    [Tooltip("How quickly the emitter's height follows the measured ground height. Lower = smoother but slower to track slopes; higher = more responsive but lets more physics noise through.")]
    public float heightSmoothing = 18f;
    [Tooltip("A gap in the skid signal shorter than this keeps extending the CURRENT mark instead of ending it - this is what stops one real turn from fragmenting into several stacked/overlapping pieces. Only a gap longer than this actually finalizes a mark.")]
    public float endGraceTime = 0.25f;

    [Header("Pooling")]
    [Tooltip("Max finished-but-still-visible marks for this wheel. Starting another skid beyond this force-expires the oldest.")]
    public int maxConcurrentTrails = 3;

    static Material sharedFallbackMaterial;

    TrailRenderer active;
    float pendingEndTimer;
    readonly List<TrailRenderer> retiring = new List<TrailRenderer>();
    readonly Queue<TrailRenderer> pool = new Queue<TrailRenderer>();

    float smoothedY;
    bool hasSmoothedY;

    // Call every physics step: `isSkidding` = is this wheel skidding right
    // now, `worldPoint` = the current wheel ground contact point.
    public void UpdateMark(bool isSkidding, Vector3 worldPoint)
    {
        Vector3 point = worldPoint + Vector3.up * groundOffset;

        // Exponentially smooth ONLY the vertical axis - the per-step noise in
        // wheel ground-hit height is what reads as jitter/shimmer in the mark,
        // while X/Z must stay exact so the mark traces the true path.
        if (!hasSmoothedY)
        {
            smoothedY = point.y;
            hasSmoothedY = true;
        }
        smoothedY = Mathf.Lerp(smoothedY, point.y, Mathf.Clamp01(heightSmoothing * Time.fixedDeltaTime));
        point.y = smoothedY;

        if (isSkidding)
        {
            pendingEndTimer = 0f;
            if (active == null)
                BeginTrail(point);
            else
                PositionEmitter(active, point);
        }
        else if (active != null)
        {
            // Debounce: keep extending the SAME mark through a brief gap in
            // the skid signal rather than ending it immediately - this is
            // what stops one real turn (where detection can flicker near its
            // threshold) from fragmenting into several stacked pieces.
            pendingEndTimer += Time.fixedDeltaTime;
            PositionEmitter(active, point);

            if (pendingEndTimer >= endGraceTime)
            {
                EndTrail();
                pendingEndTimer = 0f;
            }
        }

        RecycleExpired();
    }

    void BeginTrail(Vector3 point)
    {
        active = GetFromPoolOrCreate();
        active.gameObject.SetActive(true);

        // ORDER MATTERS - this exact sequence is what makes the connect-line
        // artifact impossible: put the emitter at the start point first, THEN
        // Clear() (which resets the trail's internal last-position memory to
        // the transform's current location), THEN start emitting.
        PositionEmitter(active, point);
        active.Clear();
        active.emitting = true;
    }

    void PositionEmitter(TrailRenderer tr, Vector3 point)
    {
        tr.transform.position = point;
        // Re-assert flat alignment every update: TransformZ lays the ribbon
        // perpendicular to the transform's Z axis, so Z must keep pointing
        // straight up even while the car (this object's parent) pitches and
        // rolls under braking and cornering.
        tr.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
    }

    void EndTrail()
    {
        active.emitting = false;
        retiring.Add(active);
        active = null;
    }

    void RecycleExpired()
    {
        for (int i = retiring.Count - 1; i >= 0; i--)
        {
            TrailRenderer tr = retiring[i];
            if (tr == null)
            {
                retiring.RemoveAt(i);
                continue;
            }

            // A retired trail's points age out on their own (trail time);
            // once none remain it's invisible and safe to recycle.
            if (tr.positionCount == 0)
            {
                tr.gameObject.SetActive(false);
                pool.Enqueue(tr);
                retiring.RemoveAt(i);
            }
        }

        // Too many finished marks alive: force-expire the oldest so a long
        // drift session can't accumulate unbounded renderers.
        while (retiring.Count > maxConcurrentTrails)
        {
            TrailRenderer oldest = retiring[0];
            retiring.RemoveAt(0);
            if (oldest != null)
            {
                oldest.Clear();
                oldest.gameObject.SetActive(false);
                pool.Enqueue(oldest);
            }
        }
    }

    TrailRenderer GetFromPoolOrCreate()
    {
        while (pool.Count > 0)
        {
            TrailRenderer tr = pool.Dequeue();
            if (tr != null)
            {
                // Re-applied on every reuse, not just at creation - otherwise
                // an already-pooled instance keeps whatever settings were
                // current when IT was first created, silently ignoring any
                // later Inspector changes (e.g. markLifetime) forever.
                ConfigureTrail(tr);
                return tr;
            }
        }

        var go = new GameObject("SkidTrail");
        go.transform.SetParent(transform, false);
        var newTr = go.AddComponent<TrailRenderer>();
        ConfigureTrail(newTr);
        return newTr;
    }

    // Every property set in code - there is no Inspector setup on the trail
    // instances themselves to get wrong. Called both when a trail is first
    // created AND every time one is pulled back out of the pool.
    void ConfigureTrail(TrailRenderer tr)
    {
        tr.time = markLifetime;
        tr.minVertexDistance = minVertexDistance;
        tr.widthMultiplier = width;
        tr.alignment = LineAlignment.TransformZ;
        tr.textureMode = LineTextureMode.Stretch;
        tr.autodestruct = false;
        tr.emitting = false;
        tr.numCapVertices = 4;
        tr.numCornerVertices = 4;
        tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tr.receiveShadows = false;
        tr.generateLightingData = false;

        tr.sharedMaterial = trailMaterial != null ? trailMaterial : GetFallbackMaterial();

        // Fade to nothing toward the tail so point expiry is seamless instead
        // of visibly popping segments off the old end of the mark.
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.black, 1f) },
            new[]
            {
                new GradientAlphaKey(markAlpha, 0f),
                new GradientAlphaKey(markAlpha * 0.85f, 0.6f),
                new GradientAlphaKey(0f, 1f)
            });
        tr.colorGradient = gradient;
    }

    static Material GetFallbackMaterial()
    {
        if (sharedFallbackMaterial == null)
        {
            // Sprites/Default: vertex-color + transparency support in both
            // Built-in and URP, which is exactly what the gradient fade needs.
            sharedFallbackMaterial = new Material(Shader.Find("Sprites/Default"));
            sharedFallbackMaterial.color = Color.white; // tint comes from the gradient
        }
        return sharedFallbackMaterial;
    }
}