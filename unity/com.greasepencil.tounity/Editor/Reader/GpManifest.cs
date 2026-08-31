using System;

namespace GreasePencilToUnity.Editor
{
    /// <summary>
    /// The JSON manifest of a .gpencil file, shaped for Unity's JsonUtility:
    /// no nested arrays, no nulls, no dictionaries.  Field names match the
    /// exporter in blender/grease_pencil_to_unity/exporter.py exactly.
    /// </summary>
    [Serializable]
    public class GpManifest
    {
        public GpScene scene;

        // "object" is a C# keyword; @ only escapes it for the compiler, the
        // serialised field name is still "object".
        public GpObjectInfo @object;

        public GpMaterial[] materials;
        public GpLayer[] layers;
        public GpDrawing[] drawings;
        public GpAnimation animation;
    }

    /// <summary>A typed run of values inside the file's binary blob.</summary>
    [Serializable]
    public class GpSlice
    {
        public int offset;
        public int count;
        public string type;
    }

    [Serializable]
    public class GpScene
    {
        public float fps;
        public int frame_start;
        public int frame_end;
    }

    [Serializable]
    public class GpObjectInfo
    {
        public string name;

        /// <summary>Object-to-world matrix, row-major, already in Unity axes.</summary>
        public float[] matrix;

        /// <summary>Blender's stroke depth order: "2D" (painter's) or "3D".</summary>
        public string depth_order;
    }

    [Serializable]
    public class GpMaterial
    {
        public string name;
        public bool show_stroke;
        public bool show_fill;
        public float[] stroke_color;
        public float[] fill_color;

        /// <summary>Grease Pencil stroke mode: "LINE", "DOTS" or "BOX".</summary>
        public string mode;

        public bool holdout;
    }

    [Serializable]
    public class GpLayer
    {
        public string name;
        public float opacity;
        public string blend_mode;
        public float[] tint_color;
        public float tint_factor;
        public float radius_offset;
        public bool use_lights;
        public bool hide;

        /// <summary>Frame numbers at which this layer's drawing changes.</summary>
        public int[] frame_times;

        /// <summary>Drawing index shown from the matching frame time on, or -1 for nothing.</summary>
        public int[] frame_drawings;
    }

    [Serializable]
    public class GpDrawing
    {
        public int n_points;
        public int n_curves;
        public GpPointArrays points;
        public GpCurveArrays curves;

        /// <summary>Fill triangles, three point indices each, local to this drawing.</summary>
        public GpSlice fill_tris;
    }

    [Serializable]
    public class GpPointArrays
    {
        public GpSlice position;
        public GpSlice radius;
        public GpSlice color;
    }

    [Serializable]
    public class GpCurveArrays
    {
        /// <summary>n_curves + 1 point offsets; curve c covers [offsets[c], offsets[c + 1]).</summary>
        public GpSlice offsets;

        public GpSlice material;
        public GpSlice flags;
        public GpSlice softness;
        public GpSlice fill_color;

        /// <summary>Two ints per curve: first triangle and triangle count in fill_tris.</summary>
        public GpSlice fill_tri_range;
    }

    [Serializable]
    public class GpAnimation
    {
        /// <summary>"NONE", "KEYFRAMES" or "BAKE".</summary>
        public string mode;

        public int step;

        /// <summary>Every sampled frame number, ascending.</summary>
        public int[] times;

        /// <summary>One row-major 4x4 object matrix per sampled frame; empty when static.</summary>
        public GpSlice matrices;
    }

    /// <summary>Per-curve flag bits, mirroring exporter.py.</summary>
    [Flags]
    public enum GpCurveFlags : byte
    {
        None = 0,
        Cyclic = 1,
        Stroke = 2,
        Fill = 4,
        StartFlat = 8,
        EndFlat = 16,
    }
}
