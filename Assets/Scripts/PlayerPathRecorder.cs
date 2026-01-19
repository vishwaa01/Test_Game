using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class PlayerPathRecorder : MonoBehaviour
{
    [Header("Recording")]
    [Tooltip("Time between samples in seconds")]
    public float sampleInterval = 0.12f;
    [Tooltip("Maximum number of recorded points to keep (older points are dropped)")]
    public int maxPoints = 200; // at 0.12s -> ~24s of path

    [Header("Road Snap")]
    [Tooltip("If true, points will try to snap to nearest collider with tag roadTag")]
    public bool snapToRoad = true;
    public string roadTag = "Road";
    [Tooltip("Max distance to search for road collider when snapping")]
    public float roadSearchRadius = 6f;

    // Public read-only access to the path (most recent point at end)
    public List<Vector3> Path { get; private set; } = new List<Vector3>();

    float sampleTimer = 0f;

    void Start()
    {
        // seed initial point
        AddPoint(transform.position);
    }

    void Update()
    {
        sampleTimer += Time.deltaTime;
        if (sampleTimer >= sampleInterval)
        {
            sampleTimer = 0f;
            AddPoint(transform.position);
        }
    }

    void AddPoint(Vector3 worldPos)
    {
        Vector3 p = worldPos;
        if (snapToRoad)
        {
            // try raycast down first
            RaycastHit hit;
            Vector3 above = p + Vector3.up * 2f;
            if (Physics.Raycast(above, Vector3.down, out hit, 4f))
            {
                if (hit.collider != null && hit.collider.gameObject.CompareTag(roadTag))
                    p = hit.point + Vector3.up * 0.02f;
            }
            else
            {
                // search nearby for road colliders
                Collider[] cols = Physics.OverlapSphere(p, roadSearchRadius);
                float best = Mathf.Infinity;
                Vector3 bestPoint = p;
                foreach (var c in cols)
                {
                    if (c == null) continue;
                    if (!c.gameObject.CompareTag(roadTag)) continue;
                    Vector3 cp = c.ClosestPoint(p);
                    float d = (cp - p).sqrMagnitude;
                    if (d < best)
                    {
                        best = d; bestPoint = cp;
                    }
                }
                if (best < Mathf.Infinity)
                {
                    p = bestPoint + Vector3.up * 0.02f;
                }
            }
        }

        Path.Add(p);
        if (Path.Count > maxPoints)
            Path.RemoveAt(0);
    }

    // Utility: returns a copy of current path (safe to iterate)
    public Vector3[] GetPathSnapshot()
    {
        return Path.ToArray();
    }
}
