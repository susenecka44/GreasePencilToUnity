using UnityEngine;

namespace GreasePencilToUnity.Editor
{
    /// <summary>
    /// One drawing unpacked from the blob into arrays the mesh builder can walk.
    /// Read once per drawing and reused by every layer that shows it.
    /// </summary>
    public sealed class GpDrawingData
    {
        public Vector3[] Position;
        public float[] Radius;
        public Color[] Color;

        /// <summary>CurveCount + 1 point offsets; curve c covers [c, c + 1).</summary>
        public int[] Offsets;

        public int[] Material;
        public byte[] Flags;
        public float[] Softness;
        public Color[] FillColor;
        public int[] FillTriRange;
        public int[] FillTris;

        public int CurveCount;
        public int PointCount;

        public GpCurveFlags FlagsOf(int curve) => (GpCurveFlags)Flags[curve];

        public static GpDrawingData Read(GpFile file, GpDrawing drawing)
        {
            var data = new GpDrawingData
            {
                CurveCount = drawing.n_curves,
                PointCount = drawing.n_points,
                Offsets = file.Ints(drawing.curves.offsets),
                Material = file.Ints(drawing.curves.material),
                Flags = file.Bytes(drawing.curves.flags),
                Softness = file.Floats(drawing.curves.softness),
                FillTriRange = file.Ints(drawing.curves.fill_tri_range),
                FillTris = file.Ints(drawing.fill_tris),
                Radius = file.Floats(drawing.points.radius),
                Position = ToVectors(file.Floats(drawing.points.position)),
                Color = ToColors(file.Floats(drawing.points.color)),
                FillColor = ToColors(file.Floats(drawing.curves.fill_color)),
            };
            return data;
        }

        private static Vector3[] ToVectors(float[] values)
        {
            var result = new Vector3[values.Length / 3];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
            }

            return result;
        }

        private static Color[] ToColors(float[] values)
        {
            var result = new Color[values.Length / 4];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new Color(
                    values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3]);
            }

            return result;
        }
    }
}
