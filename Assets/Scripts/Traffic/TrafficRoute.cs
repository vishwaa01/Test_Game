using System.Collections.Generic;
using UnityEngine;

// ==================================================================
// TRAFFIC ROUTE - editor convenience for building lanes fast
// ------------------------------------------------------------------
// Two ways to build a lane:
//
// Manual - add TrafficWaypoint children yourself wherever you want
// them, in order, then right-click -> "Auto-Link Children In Order".
//
// Fast, recommended for long or straight-ish stretches - place a
//    few ANCHOR points at the corners and curves of the lane, drag
//    any Transform in as an anchor, assign them to `anchors` in
//    order, set a spacing, then right-click ->
//    "Generate Waypoints From Anchors". It fills in evenly-spaced
//    waypoints between each pair of anchors and links them
//    automatically. Re-running it wipes and rebuilds - safe to nudge
//    an anchor and regenerate as many times as you want.
//
// Mix both freely: generate the straight runs from anchors, then hand-
// add a couple of manual waypoints near an intersection for branch
// links into another route.
// ==================================================================

public class TrafficRoute : MonoBehaviour
{
    [Tooltip("Link the last child back to the first, forming a closed loop.")]
    public bool loop = true;

    [Header("Anchor-Based Generation (optional, see header comment)")]
    [Tooltip("Corner/curve points along this lane, in order. Leave empty if you're placing TrafficWaypoints manually instead.")]
    public List<Transform> anchors = new List<Transform>();
    [Tooltip("Target distance between generated waypoints, in meters. Denser (smaller) on tight curves, sparser on long straights is fine to do per-route.")]
    public float waypointSpacing = 10f;
    [Tooltip("Generate TWO parallel chains in opposite directions - one per driving side - instead of a single centerline chain. Place the anchors down the ROAD CENTER; each lane is offset automatically.")]
    public bool twoWayLanes = false;
    [Tooltip("How far each lane sits from the anchor centerline, in meters. Roughly a quarter of the road's total width (each lane center sits halfway into its half of the road).")]
    public float laneOffset = 2f;

    [ContextMenu("Generate Waypoints From Anchors")]
    public void GenerateFromAnchors()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            // ContextMenu methods run synchronously INSIDE the Inspector's GUI
            // pass. Destroying objects mid-pass is what actually produces the
            // MissingReferenceException / SerializedObjectNotCreatableException
            // console spam - the Inspector has already captured its target list
            // for the current draw, so reassigning the selection isn't enough.
            // Deferring with delayCall runs the work AFTER the GUI pass ends.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null) DoGenerate();
            };
            return;
        }
#endif
        DoGenerate();
    }

    void DoGenerate()
    {
        if (anchors == null || anchors.Count < 2)
        {
            Debug.LogWarning($"{name}: need at least 2 anchors to generate a lane.", this);
            return;
        }

#if UNITY_EDITOR
        // Reassigning Selection here does NOT synchronously update an already-
        // open Inspector - that only happens on Unity's next repaint. Destroying
        // a still-selected object in the SAME call, even right after changing
        // Selection, is exactly what throws MissingReferenceException /
        // SerializedObjectNotCreatableException, because the Inspector never
        // got a chance to act on the change before the object vanished under it.
        // Fix: if selection touches anything of ours, clear it and DEFER the
        // actual destructive work to the next Editor update via delayCall -
        // by then the Inspector has already repainted against "nothing
        // selected" and there's nothing left for it to be holding onto.
        bool selectionTouchesRoute = false;
        foreach (var sel in UnityEditor.Selection.gameObjects)
        {
            if (sel != null && sel.transform.IsChildOf(transform))
            {
                selectionTouchesRoute = true;
                break;
            }
        }

        if (selectionTouchesRoute)
        {
            UnityEditor.Selection.activeGameObject = null;
            UnityEditor.EditorApplication.delayCall += GenerateFromAnchorsImmediate;
            return;
        }
#endif
        GenerateFromAnchorsImmediate();
    }

    void GenerateFromAnchorsImmediate()
    {
        if (anchors == null || anchors.Count < 2) return; // re-checked: state may have changed across the deferred frame

        // Guard: if an anchor is itself one of this route's TrafficWaypoint
        // children, the wipe below would destroy it mid-operation and the
        // generation would produce nothing. Auto-convert such anchors into
        // plain marker Transforms at the same position first - so using your
        // existing hand-placed waypoints as anchors Just Works.
        int converted = 0;
        for (int i = 0; i < anchors.Count; i++)
        {
            Transform a = anchors[i];
            if (a == null) continue;
            if (a.GetComponent<TrafficWaypoint>() == null) continue;
            if (!a.IsChildOf(transform)) continue; // another route's waypoint won't be wiped - safe to reference

            var marker = new GameObject($"Anchor_{i}");
            marker.transform.SetParent(transform);
            marker.transform.position = a.position;
            anchors[i] = marker.transform;
            converted++;
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(marker, "Generate Traffic Waypoints");
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
        if (converted > 0)
            Debug.Log($"{name}: converted {converted} waypoint anchor(s) into plain Anchor markers so generation can't destroy its own inputs.", this);

        // Wipe any previously generated waypoints so re-running this is idempotent.
        var existing = new List<Transform>();
        foreach (Transform child in transform) existing.Add(child);
        foreach (var child in existing)
        {
            if (child.GetComponent<TrafficWaypoint>() != null)
#if UNITY_EDITOR
                UnityEditor.Undo.DestroyObjectImmediate(child.gameObject);
#else
                Destroy(child.gameObject);
#endif
        }

        // Build the centerline point list once, then stamp one or two lane
        // chains from it. Each chain links ITSELF sequentially - a two-way
        // route must never be linked in raw hierarchy order, or the two
        // opposite-direction chains would get chained into one another.
        var centers = new List<Vector3>();
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            if (anchors[i] == null || anchors[i + 1] == null) continue;

            Vector3 a = anchors[i].position;
            Vector3 b = anchors[i + 1].position;
            float dist = Vector3.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.RoundToInt(dist / Mathf.Max(0.5f, waypointSpacing)));

            // Skip step 0 after the first segment so the shared corner point
            // between two segments isn't duplicated.
            int startStep = i == 0 ? 0 : 1;
            for (int s = startStep; s <= steps; s++)
                centers.Add(Vector3.Lerp(a, b, s / (float)steps));
        }

        if (centers.Count < 2)
        {
            Debug.LogWarning($"{name}: anchors produced fewer than 2 usable points - nothing generated.", this);
            return;
        }

        int created;
        if (twoWayLanes)
        {
            // Forward lane runs the centers in order; the opposite lane runs
            // them REVERSED. Both offset toward the right of their own travel
            // direction, which automatically lands them on opposite sides.
            created = CreateChain(centers, laneOffset, "WP_F");
            centers.Reverse();
            created += CreateChain(centers, laneOffset, "WP_B");
        }
        else
        {
            created = CreateChain(centers, 0f, "WP");
        }

        Debug.Log($"{name}: generated {created} waypoints from {anchors.Count} anchors" +
                  (twoWayLanes ? " (two-way: WP_F + WP_B chains)." : "."), this);
    }

    // Stamps one linked chain of waypoints along `pts`, each offset toward the
    // right of its local travel direction by `offset`. Returns how many were made.
    int CreateChain(List<Vector3> pts, float offset, string prefix)
    {
        var made = new List<TrafficWaypoint>(pts.Count);

        for (int i = 0; i < pts.Count; i++)
        {
            // travel direction at this point: toward the next point (wrapping
            // for a loop), or carried from the previous segment at a dead end
            Vector3 dir;
            if (i < pts.Count - 1) dir = pts[i + 1] - pts[i];
            else if (loop) dir = pts[0] - pts[i];
            else dir = pts[i] - pts[i - 1];
            dir.y = 0f;
            dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;

            Vector3 right = Vector3.Cross(Vector3.up, dir);
            Vector3 pos = pts[i] + right * offset;

            var go = new GameObject($"{prefix}_{i:00}");
            go.transform.SetParent(transform);
            go.transform.position = pos;
            made.Add(go.AddComponent<TrafficWaypoint>());
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Generate Traffic Waypoints");
#endif
        }

        for (int i = 0; i < made.Count; i++)
        {
            bool last = i == made.Count - 1;
            if (last && !loop) break;
            TrafficWaypoint to = made[last ? 0 : i + 1];
            if (!made[i].next.Contains(to)) made[i].next.Add(to);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(made[i]);
#endif
        }

        return made.Count;
    }

    [ContextMenu("Auto-Link Children In Order")]
    public void AutoLinkChildren()
    {
        var points = GetComponentsInChildren<TrafficWaypoint>();
        if (points.Length < 2)
        {
            Debug.LogWarning($"{name}: TrafficRoute needs at least 2 TrafficWaypoint children to link.", this);
            return;
        }

        int added = 0;
        for (int i = 0; i < points.Length; i++)
        {
            bool last = i == points.Length - 1;
            if (last && !loop) break;

            TrafficWaypoint from = points[i];
            TrafficWaypoint to = points[last ? 0 : i + 1];

            if (!from.next.Contains(to))
            {
                from.next.Add(to);
                added++;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(from);
#endif
            }
        }

        Debug.Log($"{name}: TrafficRoute linked {points.Length} waypoints ({added} new links added).", this);
    }
}