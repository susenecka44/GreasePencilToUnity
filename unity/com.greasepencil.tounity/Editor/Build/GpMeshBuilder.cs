using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GreasePencilToUnity.Editor
{
    /// <summary>Which material a run of triangles belongs to, and whether it is a fill.</summary>
    public readonly struct GpSubmeshKey : IEquatable<GpSubmeshKey>
    {
        public readonly int Material;
        public readonly bool Fill;

        public GpSubmeshKey(int material, bool fill)
        {
            Material = material;
            Fill = fill;
        }

        public bool Equals(GpSubmeshKey other) => Material == other.Material && Fill == other.Fill;
        public override bool Equals(object obj) => obj is GpSubmeshKey other && Equals(other);
        public override int GetHashCode() => (Material << 1) | (Fill ? 1 : 0);
        public override string ToString() => $"{Material}{(Fill ? "f" : "s")}";
    }

    /// <summary>
    /// Builds the ribbon meshes.  Only the centreline is stored: two vertices per
    /// curve point, which the shader pushes apart sideways towards the camera by
    /// the point's radius.  That keeps meshes small and reproduces the way
    /// Blender draws Grease Pencil from any viewing angle.
    ///
    /// Round caps cost two extra vertices per end: the ribbon is extended by one
    /// radius past the endpoint and the cap coordinate in uv1.x lets the shader
    /// clip it back to a half circle.
    /// </summary>
    public static class GpMeshBuilder
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GpVertex
        {
            public Vector3 Position; // centreline point, object space
            public Vector3 Normal;   // along-curve tangent, or the fill's face normal
            public Color Color;      // baked RGBA, linear, full float precision
            public Vector4 Uv0;      // x: u along stroke, y: side (-1/+1), z: radius, w: softness
            public Vector4 Uv1;      // x: cap coordinate, y: arc length, z: is fill, w: unused
        }

        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
        };

        private const float Epsilon = 1e-12f;

        /// <summary>
        /// The submeshes a layer needs, as the union over every drawing it shows.
        ///
        /// Every mesh in a layer is built with this same layout -- empty submeshes
        /// included -- so the renderer's material array stays valid when animation
        /// swaps one mesh for another.
        /// </summary>
        public static List<GpSubmeshKey> LayoutFor(IEnumerable<GpDrawingData> drawings)
        {
            var order = new List<GpSubmeshKey>();
            var seen = new HashSet<GpSubmeshKey>();
            foreach (var data in drawings)
            {
                for (int curve = 0; curve < data.CurveCount; curve++)
                {
                    var flags = data.FlagsOf(curve);
                    // Fill first: Grease Pencil draws a stroke's fill behind it.
                    if ((flags & GpCurveFlags.Fill) != 0)
                    {
                        Add(new GpSubmeshKey(data.Material[curve], true));
                    }

                    if ((flags & GpCurveFlags.Stroke) != 0)
                    {
                        Add(new GpSubmeshKey(data.Material[curve], false));
                    }
                }
            }

            return order;

            void Add(GpSubmeshKey key)
            {
                if (seen.Add(key))
                {
                    order.Add(key);
                }
            }
        }

        public static Mesh Build(GpDrawingData data, List<GpSubmeshKey> layout, string name)
        {
            var slot = new Dictionary<GpSubmeshKey, int>(layout.Count);
            for (int i = 0; i < layout.Count; i++)
            {
                slot[layout[i]] = i;
            }

            var vertices = new List<GpVertex>(data.PointCount * 2 + 64);
            var indices = new List<int>[Mathf.Max(layout.Count, 1)];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = new List<int>();
            }

            for (int curve = 0; curve < data.CurveCount; curve++)
            {
                var flags = data.FlagsOf(curve);
                if ((flags & GpCurveFlags.Fill) != 0 &&
                    slot.TryGetValue(new GpSubmeshKey(data.Material[curve], true), out int fillSlot))
                {
                    AppendFill(data, curve, vertices, indices[fillSlot]);
                }

                if ((flags & GpCurveFlags.Stroke) != 0 &&
                    slot.TryGetValue(new GpSubmeshKey(data.Material[curve], false), out int strokeSlot))
                {
                    AppendStroke(data, curve, flags, vertices, indices[strokeSlot]);
                }
            }

            return CreateMesh(name, vertices, indices, layout.Count, MaxRadius(data));
        }

        private static void AppendStroke(GpDrawingData data, int curve, GpCurveFlags flags,
                                         List<GpVertex> vertices, List<int> indices)
        {
            int first = data.Offsets[curve];
            int count = data.Offsets[curve + 1] - first;
            if (count <= 0)
            {
                return;
            }

            bool cyclic = (flags & GpCurveFlags.Cyclic) != 0;
            float softness = data.Softness[curve];
            int baseVertex = vertices.Count;

            var tangents = Tangents(data, first, count, cyclic);
            var arc = ArcLengths(data, first, count, cyclic);
            float total = arc[arc.Length - 1];
            float inverseTotal = total > Epsilon ? 1f / total : 0f;

            int stations = 0;

            // Round start cap: one radius of ribbon before the first point.
            if (!cyclic && (flags & GpCurveFlags.StartFlat) == 0)
            {
                Emit(data.Position[first] - tangents[0] * data.Radius[first],
                     tangents[0], data.Radius[first], data.Color[first], 0f, -1f);
            }

            for (int i = 0; i < count; i++)
            {
                Emit(data.Position[first + i], tangents[i], data.Radius[first + i],
                     data.Color[first + i], arc[i] * inverseTotal, 0f);
            }

            if (cyclic)
            {
                // Close the loop by repeating the first point at the end.
                Emit(data.Position[first], tangents[0], data.Radius[first],
                     data.Color[first], 1f, 0f);
            }
            else if ((flags & GpCurveFlags.EndFlat) == 0)
            {
                int last = first + count - 1;
                Emit(data.Position[last] + tangents[count - 1] * data.Radius[last],
                     tangents[count - 1], data.Radius[last], data.Color[last], 1f, 1f);
            }

            if (count == 1 && stations < 2)
            {
                // A flat-capped single point has no extent at all; skip it rather
                // than emitting a degenerate quad.
                vertices.RemoveRange(baseVertex, vertices.Count - baseVertex);
                return;
            }

            for (int station = 0; station < stations - 1; station++)
            {
                int a = baseVertex + station * 2;
                indices.Add(a);
                indices.Add(a + 1);
                indices.Add(a + 2);
                indices.Add(a + 2);
                indices.Add(a + 1);
                indices.Add(a + 3);
            }

            void Emit(Vector3 position, Vector3 tangent, float radius, Color color, float u, float cap)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    vertices.Add(new GpVertex
                    {
                        Position = position,
                        Normal = tangent,
                        Color = color,
                        Uv0 = new Vector4(u, side, radius, softness),
                        Uv1 = new Vector4(cap, total, 0f, 0f),
                    });
                }

                stations++;
            }
        }

        private static void AppendFill(GpDrawingData data, int curve,
                                       List<GpVertex> vertices, List<int> indices)
        {
            int triangleStart = data.FillTriRange[curve * 2];
            int triangleCount = data.FillTriRange[curve * 2 + 1];
            if (triangleCount <= 0)
            {
                return;
            }

            int first = data.Offsets[curve];
            int count = data.Offsets[curve + 1] - first;
            int baseVertex = vertices.Count;
            Color color = data.FillColor[curve];
            Vector3 normal = FaceNormal(data.Position, first, count);

            for (int i = 0; i < count; i++)
            {
                vertices.Add(new GpVertex
                {
                    Position = data.Position[first + i],
                    Normal = normal,
                    Color = color,
                    Uv0 = Vector4.zero,
                    // Radius 0 and the fill flag keep the vertex shader from
                    // expanding this into a ribbon.
                    Uv1 = new Vector4(0f, 0f, 1f, 0f),
                });
            }

            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                int index = (triangleStart + triangle) * 3;
                for (int corner = 0; corner < 3; corner++)
                {
                    indices.Add(baseVertex + data.FillTris[index + corner] - first);
                }
            }
        }

        private static Vector3[] Tangents(GpDrawingData data, int first, int count, bool cyclic)
        {
            var tangents = new Vector3[count];
            Vector3 lastGood = Vector3.right;
            for (int i = 0; i < count; i++)
            {
                int previous = i > 0 ? i - 1 : (cyclic ? count - 1 : 0);
                int next = i < count - 1 ? i + 1 : (cyclic ? 0 : count - 1);
                Vector3 direction = data.Position[first + next] - data.Position[first + previous];
                if (direction.sqrMagnitude <= Epsilon)
                {
                    // Coincident neighbours: hold the last usable direction so the
                    // ribbon does not collapse at duplicated points.
                    tangents[i] = lastGood;
                    continue;
                }

                lastGood = direction.normalized;
                tangents[i] = lastGood;
            }

            return tangents;
        }

        private static float[] ArcLengths(GpDrawingData data, int first, int count, bool cyclic)
        {
            var arc = new float[count + (cyclic ? 1 : 0)];
            for (int i = 1; i < count; i++)
            {
                arc[i] = arc[i - 1] +
                         Vector3.Distance(data.Position[first + i], data.Position[first + i - 1]);
            }

            if (cyclic && count > 0)
            {
                arc[count] = arc[count - 1] +
                             Vector3.Distance(data.Position[first], data.Position[first + count - 1]);
            }

            return arc;
        }

        /// <summary>Newell normal of a closed polygon, for lighting flat fills.</summary>
        private static Vector3 FaceNormal(Vector3[] positions, int first, int count)
        {
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                Vector3 current = positions[first + i];
                Vector3 next = positions[first + (i + 1) % count];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }

            return normal.sqrMagnitude > Epsilon ? normal.normalized : Vector3.up;
        }

        private static float MaxRadius(GpDrawingData data)
        {
            float max = 0f;
            foreach (float radius in data.Radius)
            {
                max = Mathf.Max(max, radius);
            }

            return max;
        }

        private static Mesh CreateMesh(string name, List<GpVertex> vertices, List<int>[] indices,
                                       int submeshCount, float maxRadius)
        {
            var mesh = new Mesh { name = name };
            mesh.indexFormat = IndexFormat.UInt32;

            if (vertices.Count == 0)
            {
                // A layer can legitimately be empty on some frames.  The submesh
                // count still has to match the layer's material array.
                mesh.SetVertexBufferParams(0, Layout);
                mesh.SetIndexBufferParams(0, IndexFormat.UInt32);
                mesh.subMeshCount = Mathf.Max(submeshCount, 1);
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    mesh.SetSubMesh(i, new SubMeshDescriptor(0, 0), MeshUpdateFlags.DontRecalculateBounds);
                }

                mesh.bounds = new Bounds(Vector3.zero, Vector3.zero);
                return mesh;
            }

            var vertexArray = vertices.ToArray();
            mesh.SetVertexBufferParams(vertexArray.Length, Layout);
            mesh.SetVertexBufferData(vertexArray, 0, 0, vertexArray.Length);

            int total = 0;
            foreach (var list in indices)
            {
                total += list.Count;
            }

            var indexArray = new int[total];
            var descriptors = new SubMeshDescriptor[Mathf.Max(submeshCount, 1)];
            int cursor = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i].CopyTo(indexArray, cursor);
                descriptors[i] = new SubMeshDescriptor(cursor, indices[i].Count)
                {
                    firstVertex = 0,
                    vertexCount = vertexArray.Length,
                };
                cursor += indices[i].Count;
            }

            mesh.SetIndexBufferParams(total, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indexArray, 0, 0, total);

            // The shader moves vertices outward by up to one radius, so the bounds
            // have to be padded or strokes get culled at the edge of the screen.
            var bounds = Bounds(vertexArray, maxRadius);
            mesh.subMeshCount = descriptors.Length;
            for (int i = 0; i < descriptors.Length; i++)
            {
                var descriptor = descriptors[i];
                descriptor.bounds = bounds;
                mesh.SetSubMesh(i, descriptor, MeshUpdateFlags.DontRecalculateBounds);
            }

            mesh.bounds = bounds;
            mesh.UploadMeshData(false);
            return mesh;
        }

        private static Bounds Bounds(GpVertex[] vertices, float maxRadius)
        {
            Vector3 min = vertices[0].Position;
            Vector3 max = min;
            foreach (var vertex in vertices)
            {
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }

            var padding = Vector3.one * maxRadius;
            var bounds = new Bounds();
            bounds.SetMinMax(min - padding, max + padding);
            return bounds;
        }
    }
}
