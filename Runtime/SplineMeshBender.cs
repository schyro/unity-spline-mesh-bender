using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Schyro.SplineMeshBender
{
    /// <summary>
    /// Repeats and bends a source mesh along every spline in the sibling <see cref="SplineContainer"/>.
    /// Generated chunks are rebuilt from the source and are transient.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Splines/Spline Mesh Bender")]
    [RequireComponent(typeof(SplineContainer))]
    public class SplineMeshBender : MonoBehaviour
    {
        private const string RootName = "SplineMeshBender_Generated";
        private const int MaxVerts16 = 65535;

        [Tooltip("Source model. The visual mesh is the first child mesh not named COL_*; an optional child named COL_* with a MeshCollider provides the collider mesh.")]
        [SerializeField] private GameObject _segment;
        [Tooltip("Offset applied in the bent frame (x right, y up, z forward).")]
        [SerializeField] private Vector3 _offset;
        [Tooltip("Generate a MeshCollider per chunk from the COL_* mesh of the source model.")]
        [SerializeField] private bool _generateColliders = true;
        [Tooltip("How many segments are merged into one chunk mesh.")]
        [SerializeField, Min(1)] private int _segmentsPerChunk = 50;
        [Tooltip("Distance between spline samples, in local units.")]
        [SerializeField, Min(0.01f)] private float _sampleStep = 0.1f;

        private readonly List<Mesh> _meshes = new List<Mesh>();
        private GameObject _root;
        private bool _warned;

        // Spline lookup table, reused between splines.
        private Vector3[] _samplePos = new Vector3[0];
        private Vector3[] _sampleFwd = new Vector3[0];
        private int _sampleCount;
        private float _splineLength;

#if UNITY_EDITOR
        private bool _rebuildQueued;
#endif

        /// <summary>Number of generated chunk objects.</summary>
        public int ChunkCount => _root != null ? _root.transform.childCount : 0;

        private void OnEnable()
        {
#if UNITY_EDITOR
            Spline.Changed += OnSplineChanged;
            SplineContainer.SplineAdded += OnContainerChanged;
            SplineContainer.SplineRemoved += OnContainerChanged;
#endif
            Rebuild();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            Spline.Changed -= OnSplineChanged;
            SplineContainer.SplineAdded -= OnContainerChanged;
            SplineContainer.SplineRemoved -= OnContainerChanged;
#endif
            Clear();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _warned = false;
            QueueRebuild();
        }

        private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            var container = GetComponent<SplineContainer>();
            if (container == null) return;
            var splines = container.Splines;
            for (int i = 0; i < splines.Count; i++)
            {
                if (ReferenceEquals(splines[i], spline))
                {
                    QueueRebuild();
                    return;
                }
            }
        }

        private void OnContainerChanged(SplineContainer container, int index)
        {
            if (container == GetComponent<SplineContainer>()) QueueRebuild();
        }

        private void QueueRebuild()
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            EditorApplication.delayCall += () =>
            {
                _rebuildQueued = false;
                if (this != null && isActiveAndEnabled) Rebuild();
            };
        }
#endif

        /// <summary>Regenerates all chunk meshes from the current splines and settings.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            Clear();

            if (!TryGetSource(out var visual, out var materials, out var shadows, out var collisionMesh)) return;

            var container = GetComponent<SplineContainer>();
            var splines = container.Splines;
            float length = visual.bounds.size.z;
            float zMin = visual.bounds.min.z;
            if (length < 0.01f)
            {
                Warn("SplineMeshBender: the source mesh has no extent along Z.");
                return;
            }

            _root = new GameObject(RootName) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
            _root.transform.SetParent(transform, false);

            var visualData = new SourceData(visual, length, zMin);
            SourceData? colliderData = null;
            if (collisionMesh != null) colliderData = new SourceData(collisionMesh, length, zMin);

            Vector3 up = transform.InverseTransformDirection(Vector3.up);
            Vector3 fallbackUp = transform.InverseTransformDirection(Vector3.forward);
            int perChunk = Mathf.Max(1, _segmentsPerChunk);

            for (int s = 0; s < splines.Count; s++)
            {
                var spline = splines[s];
                if (spline.Count < 2) continue;
                if (!BuildLookup(spline)) continue;

                int pieces = Mathf.Max(1, Mathf.RoundToInt(_splineLength / length));
                float stretch = _splineLength / (pieces * length);
                int chunks = (pieces + perChunk - 1) / perChunk;

                for (int c = 0; c < chunks; c++)
                {
                    int first = c * perChunk;
                    int count = Mathf.Min(perChunk, pieces - first);

                    var go = new GameObject($"Spline_{s}_{c}") { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
                    go.transform.SetParent(_root.transform, false);

                    var mesh = Bend(visualData, first, count, stretch, up, fallbackUp);
                    mesh.name = go.name;
                    _meshes.Add(mesh);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = materials;
                    mr.shadowCastingMode = shadows;

                    if (colliderData.HasValue)
                    {
                        var colMesh = Bend(colliderData.Value, first, count, stretch, up, fallbackUp);
                        colMesh.name = go.name + "_Collider";
                        _meshes.Add(colMesh);
                        go.AddComponent<MeshCollider>().sharedMesh = colMesh;
                    }
                }
            }
        }

        private void Clear()
        {
            // Also sweeps leftovers from a previous domain reload, since hidden objects stay childed to us.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name != RootName || child.gameObject.hideFlags != HideFlags.HideAndDontSave) continue;
                child.gameObject.SetActive(false);
                DestroyGenerated(child.gameObject);
            }
            _root = null;

            for (int i = 0; i < _meshes.Count; i++)
            {
                if (_meshes[i] != null) DestroyGenerated(_meshes[i]);
            }
            _meshes.Clear();
        }

        private static void DestroyGenerated(Object obj)
        {
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        private bool TryGetSource(out Mesh visual, out Material[] materials, out ShadowCastingMode shadows, out Mesh collisionMesh)
        {
            visual = null;
            materials = null;
            shadows = ShadowCastingMode.On;
            collisionMesh = null;

            if (_segment == null) return false;

            MeshRenderer renderer = null;
            foreach (var filter in _segment.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.gameObject.name.StartsWith("COL_") || filter.sharedMesh == null) continue;
                visual = filter.sharedMesh;
                renderer = filter.GetComponent<MeshRenderer>();
                break;
            }

            if (visual == null)
            {
                Warn("SplineMeshBender: the segment model has no visual mesh.");
                return false;
            }
            if (!visual.isReadable)
            {
                Warn($"SplineMeshBender: mesh '{visual.name}' is not readable. Enable Read/Write on the model import settings.");
                return false;
            }

            materials = renderer != null ? renderer.sharedMaterials : new Material[0];
            shadows = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.On;

            if (!_generateColliders) return true;

            foreach (var col in _segment.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!col.gameObject.name.StartsWith("COL_") || col.sharedMesh == null) continue;
                if (!col.sharedMesh.isReadable)
                {
                    Warn($"SplineMeshBender: collider mesh '{col.sharedMesh.name}' is not readable; colliders skipped.");
                    break;
                }
                collisionMesh = col.sharedMesh;
                break;
            }
            return true;
        }

        private void Warn(string message)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning(message, this);
        }

        // Fills the lookup table with position and tangent at each step and the exact end.
        private bool BuildLookup(Spline spline)
        {
            float len = spline.GetLength();
            if (len < 0.01f) return false;
            _splineLength = len;

            float step = Mathf.Max(0.01f, _sampleStep);
            int count = Mathf.CeilToInt(len / step) + 1;
            if (_samplePos.Length < count)
            {
                _samplePos = new Vector3[count];
                _sampleFwd = new Vector3[count];
            }
            _sampleCount = count;

            Vector3 lastFwd = Vector3.forward;
            for (int i = 0; i < count; i++)
            {
                float d = i == count - 1 ? len : i * step;
                float t = spline.ConvertIndexUnit(d, PathIndexUnit.Distance, PathIndexUnit.Normalized);
                spline.Evaluate(t, out float3 pos, out float3 tan, out float3 _);
                Vector3 fwd = tan;
                if (fwd.sqrMagnitude < 1e-12f)
                {
                    // Linear knots can have a zero derivative at the knot itself.
                    const float probe = 0.001f;
                    float nearbyT = t < 1f - probe ? t + probe : t - probe;
                    fwd = spline.EvaluateTangent(nearbyT);
                }
                float mag = fwd.magnitude;
                if (mag > 1e-6f) lastFwd = fwd / mag;
                _samplePos[i] = pos;
                _sampleFwd[i] = lastFwd;
            }
            return true;
        }

        private void Sample(float distance, out Vector3 pos, out Vector3 fwd)
        {
            float step = Mathf.Max(0.01f, _sampleStep);
            int i = Mathf.Min((int)(distance / step), _sampleCount - 2);
            float d0 = i * step;
            float d1 = Mathf.Min((i + 1) * step, _splineLength);
            float span = d1 - d0;
            float f = span > 1e-6f ? Mathf.Clamp01((distance - d0) / span) : 0f;
            pos = Vector3.LerpUnclamped(_samplePos[i], _samplePos[i + 1], f);
            fwd = Vector3.LerpUnclamped(_sampleFwd[i], _sampleFwd[i + 1], f);
            float mag = fwd.magnitude;
            fwd = mag > 1e-6f ? fwd / mag : _sampleFwd[i];
        }

        // Source mesh data read once per rebuild.
        private readonly struct SourceData
        {
            public readonly Vector3[] Vertices;
            public readonly Vector3[] Normals;
            public readonly Vector4[] Tangents;
            public readonly Vector2[] Uv;
            public readonly Vector2[] Uv2;
            public readonly Color[] Colors;
            public readonly int[][] Indices;
            public readonly MeshTopology[] Topologies;
            public readonly float Length;
            public readonly float ZMin;

            public SourceData(Mesh mesh, float length, float zMin)
            {
                Vertices = mesh.vertices;
                int n = Vertices.Length;
                Normals = mesh.normals.Length == n ? mesh.normals : null;
                Tangents = mesh.tangents.Length == n ? mesh.tangents : null;
                Uv = mesh.uv.Length == n ? mesh.uv : null;
                Uv2 = mesh.uv2.Length == n ? mesh.uv2 : null;
                Colors = mesh.colors.Length == n ? mesh.colors : null;
                int subs = mesh.subMeshCount;
                Indices = new int[subs][];
                Topologies = new MeshTopology[subs];
                for (int i = 0; i < subs; i++)
                {
                    Indices[i] = mesh.GetIndices(i);
                    Topologies[i] = mesh.GetTopology(i);
                }
                Length = length;
                ZMin = zMin;
            }
        }

        private Mesh Bend(in SourceData src, int firstPiece, int pieceCount, float stretch, Vector3 upRef, Vector3 fallbackUp)
        {
            int vc = src.Vertices.Length;
            int total = vc * pieceCount;
            var verts = new Vector3[total];
            var normals = src.Normals != null ? new Vector3[total] : null;
            var tangents = src.Tangents != null ? new Vector4[total] : null;
            var uv = src.Uv != null ? new Vector2[total] : null;
            var uv2 = src.Uv2 != null ? new Vector2[total] : null;
            var colors = src.Colors != null ? new Color[total] : null;

            for (int p = 0; p < pieceCount; p++)
            {
                float pieceStart = (firstPiece + p) * src.Length;
                int o = p * vc;
                for (int v = 0; v < vc; v++)
                {
                    Vector3 sv = src.Vertices[v];
                    float d = Mathf.Clamp((pieceStart + (sv.z - src.ZMin)) * stretch, 0f, _splineLength);
                    Sample(d, out Vector3 pos, out Vector3 fwd);

                    Vector3 right = Vector3.Cross(upRef, fwd);
                    float rm = right.magnitude;
                    right = rm > 1e-6f ? right / rm : Vector3.Cross(fallbackUp, fwd).normalized;
                    Vector3 up2 = Vector3.Cross(fwd, right);

                    verts[o + v] = pos + right * (sv.x + _offset.x) + up2 * (sv.y + _offset.y) + fwd * _offset.z;
                    if (normals != null)
                    {
                        Vector3 n = src.Normals[v];
                        normals[o + v] = right * n.x + up2 * n.y + fwd * n.z;
                    }
                    if (tangents != null)
                    {
                        Vector4 t = src.Tangents[v];
                        Vector3 rt = right * t.x + up2 * t.y + fwd * t.z;
                        tangents[o + v] = new Vector4(rt.x, rt.y, rt.z, t.w);
                    }
                    if (uv != null) uv[o + v] = src.Uv[v];
                    if (uv2 != null) uv2[o + v] = src.Uv2[v];
                    if (colors != null) colors[o + v] = src.Colors[v];
                }
            }

            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            if (total > MaxVerts16) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            if (normals != null) mesh.normals = normals;
            if (tangents != null) mesh.tangents = tangents;
            if (uv != null) mesh.uv = uv;
            if (uv2 != null) mesh.uv2 = uv2;
            if (colors != null) mesh.colors = colors;

            int subs = src.Indices.Length;
            mesh.subMeshCount = subs;
            for (int s = 0; s < subs; s++)
            {
                int[] srcIdx = src.Indices[s];
                var idx = new int[srcIdx.Length * pieceCount];
                for (int p = 0; p < pieceCount; p++)
                {
                    int vo = p * vc;
                    int io = p * srcIdx.Length;
                    for (int i = 0; i < srcIdx.Length; i++) idx[io + i] = srcIdx[i] + vo;
                }
                mesh.SetIndices(idx, src.Topologies[s], s, false);
            }
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
