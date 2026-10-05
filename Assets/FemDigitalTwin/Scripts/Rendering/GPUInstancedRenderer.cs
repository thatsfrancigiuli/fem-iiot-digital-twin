using System.Collections.Generic;
using UnityEngine;

namespace FemDigitalTwin.Rendering
{
    /// <summary>
    /// Draws one small mesh (an octahedron) per FEM node with GPU instancing.
    ///
    /// Nodes are grouped in batches of at most 1023 instances (the per-call limit of
    /// Graphics.DrawMeshInstanced). Each batch is drawn with a single call per frame; the
    /// per-node colour is passed through a MaterialPropertyBlock, which keeps instancing active.
    ///
    /// Per-instance colours require the "FemDigitalTwin/InstancedColored" shader. With any other
    /// instancing-enabled material the renderer falls back to one average colour per batch
    /// (useful only as a geometry preview).
    /// </summary>
    public class GPUInstancedRenderer : MonoBehaviour
    {
        public const int BatchSize = 1023;
        public const string ColoredShaderName = "FemDigitalTwin/InstancedColored";

        private Mesh mesh;
        private Material material;
        private bool perInstanceColor;
        private readonly List<Matrix4x4[]> batchMatrices = new List<Matrix4x4[]>();
        private readonly List<Vector4[]> batchColors = new List<Vector4[]>();
        private readonly List<MaterialPropertyBlock> propertyBlocks = new List<MaterialPropertyBlock>();

        public int NodeCount { get; private set; }

        public void Initialize(Mesh instanceMesh, Material instanceMaterial, IList<Vector3> positions,
                               IList<float> values, float pointSize, ColorScale scale)
        {
            mesh = instanceMesh;
            material = new Material(instanceMaterial) { enableInstancing = true };
            perInstanceColor = material.shader != null && material.shader.name == ColoredShaderName;

            batchMatrices.Clear();
            batchColors.Clear();
            propertyBlocks.Clear();
            NodeCount = positions.Count;

            int batchCount = (NodeCount + BatchSize - 1) / BatchSize;
            for (int b = 0; b < batchCount; b++)
            {
                int start = b * BatchSize;
                int size = Mathf.Min(BatchSize, NodeCount - start);
                var matrices = new Matrix4x4[size];
                var colors = new Vector4[size];
                for (int i = 0; i < size; i++)
                {
                    int k = start + i;
                    matrices[i] = Matrix4x4.TRS(positions[k], Quaternion.identity, Vector3.one * pointSize);
                    colors[i] = scale.ColorOf(values[k]);
                }
                batchMatrices.Add(matrices);
                batchColors.Add(colors);
                var block = new MaterialPropertyBlock();
                ApplyColors(block, colors);
                propertyBlocks.Add(block);
            }
        }

        /// Recolours the existing batches in place (no reallocation of matrices or blocks).
        /// The paper's measurements rebuild the renderer instead: see PointCloudGenerator.rebuildRendererOnUpdate.
        public void UpdateValues(IList<float> values, ColorScale scale)
        {
            for (int b = 0; b < batchColors.Count; b++)
            {
                Vector4[] colors = batchColors[b];
                int start = b * BatchSize;
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = scale.ColorOf(values[start + i]);
                ApplyColors(propertyBlocks[b], colors);
            }
        }

        private void ApplyColors(MaterialPropertyBlock block, Vector4[] colors)
        {
            if (perInstanceColor)
            {
                block.SetVectorArray("_Color", colors);
            }
            else
            {
                Vector4 sum = Vector4.zero;
                for (int i = 0; i < colors.Length; i++) sum += colors[i];
                block.SetColor("_Color", sum / Mathf.Max(1, colors.Length));
            }
        }

        private void Update()
        {
            if (mesh == null || material == null) return;
            for (int b = 0; b < batchMatrices.Count; b++)
                Graphics.DrawMeshInstanced(mesh, 0, material, batchMatrices[b], batchMatrices[b].Length, propertyBlocks[b]);
        }

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
        }

        /// Low-poly octahedron (6 vertices, 8 triangles) used as the instance mesh.
        public static Mesh CreateOctahedron()
        {
            var m = new Mesh { name = "NodeOctahedron" };
            m.vertices = new[]
            {
                new Vector3(0, 0.5f, 0), new Vector3(0.5f, 0, 0), new Vector3(0, 0, 0.5f),
                new Vector3(-0.5f, 0, 0), new Vector3(0, 0, -0.5f), new Vector3(0, -0.5f, 0)
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1, 5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>Fixed colour scale: built once from the imported field, then kept constant.</summary>
    public struct ColorScale
    {
        public float Min, Max, Exponent, Offset;
        public Color ColorMin, ColorMax;
        public bool UseSpectrum;

        public Color ColorOf(float value)
        {
            return ColorMapping.Evaluate(value, Min, Max, ColorMin, ColorMax, UseSpectrum, Exponent, Offset);
        }
    }
}
