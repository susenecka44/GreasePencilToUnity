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

        /// <summary>
        /// Read one drawing.  Colours are exported in linear space, which is what
        /// a Linear colour space project wants.  In a Gamma project Unity passes
        /// shader output to the display untouched, so encode them instead --
        /// otherwise strokes come out darker and more saturated than in Blender.
        /// </summary>
        public static GpDrawingData Read(GpFile file, GpDrawing drawing, bool encodeToGamma = false)
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
                Color = ToColors(file.Floats(drawing.points.color), encodeToGamma),
                FillColor = ToColors(file.Floats(drawing.curves.fill_color), encodeToGamma),
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

        private static Color[] ToColors(float[] values, bool encodeToGamma)
        {
            var result = new Color[values.Length / 4];
            for (int i = 0; i < result.Length; i++)
            {
                var color = new Color(
                    values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3]);
                // Alpha is never colour managed.
                result[i] = encodeToGamma
                    ? new Color(Encode(color.r), Encode(color.g), Encode(color.b), color.a)
                    : color;
            }

            return result;
        }

        /// <summary>Linear to sRGB, the transform Blender's display applies.</summary>
        private static float Encode(float value)
        {
            value = Mathf.Max(value, 0f);
            return value <= 0.0031308f
                ? value * 12.92f
                : 1.055f * Mathf.Pow(value, 1f / 2.4f) - 0.055f;
        }
    }
}
