using UnityEngine;
using UnityEngine.Rendering;

namespace GreasePencilToUnity.Editor
{
    /// <summary>How strokes are sorted against each other and the rest of the scene.</summary>
    public enum GpSortMode
    {
        /// <summary>Use the object's own Blender setting (Stroke Depth Order).</summary>
        FromBlender = 0,

        /// <summary>Painter's order: transparent, no depth writes, layers stacked back to front.</summary>
        LayerOrder2D = 1,

        /// <summary>Alpha clipped with depth writes, so strokes intersect scene geometry.</summary>
        Depth3D = 2,
    }

    /// <summary>
    /// Creates one material per (layer, Grease Pencil material, stroke/fill).
    /// Layers need their own materials because both the render queue and the
    /// blend mode come from the layer.
    /// </summary>
    public static class GpMaterialBuilder
    {
        public const string ShaderName = "Grease Pencil/Stroke";

        private const int TransparentQueue = 3000;
        private const int AlphaTestQueue = 2450;

        public static Material Create(Shader shader, GpManifest manifest, int layerIndex,
                                      GpSubmeshKey key, GreasePencilImportSettings settings,
                                      out string warning)
        {
            warning = null;
            var layer = manifest.layers[layerIndex];
            var source = key.Material >= 0 && key.Material < manifest.materials.Length
                ? manifest.materials[key.Material]
                : null;

            string materialName = source != null ? source.name : "Material";
            var material = new Material(shader)
            {
                name = $"{layer.name}_{materialName}{(key.Fill ? "_Fill" : "")}",
                enableInstancing = false,
            };

            material.SetColor("_Tint", Color.white);
            material.SetFloat("_Opacity", 1f);
            material.SetFloat("_WidthScale", settings.widthScale);
            material.SetFloat("_Puff", key.Fill ? 0f : settings.puff);
            material.SetFloat("_Softness", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetFloat("_ScreenWidthScale", settings.screenWidthScale);

            // Blender's per-layer "Use Lights" toggle decides whether this layer
            // reacts to scene lighting at all.
            material.SetFloat("_LightInfluence", layer.use_lights ? settings.lightInfluence : 0f);

            if (settings.screenSpaceWidth && !key.Fill)
            {
                material.EnableKeyword("_SCREEN_SPACE_WIDTH");
                material.SetFloat("_ScreenSpaceWidth", 1f);
            }

            ApplyBlendMode(material, layer.blend_mode, ref warning);
            ApplySorting(material, manifest, layerIndex, key, settings);
            return material;
        }

        private static void ApplyBlendMode(Material material, string blendMode, ref string warning)
        {
            BlendMode source = BlendMode.SrcAlpha;
            BlendMode destination = BlendMode.OneMinusSrcAlpha;
            BlendOp operation = BlendOp.Add;

            switch (blendMode)
            {
                case "REGULAR":
                    break;
                case "ADD":
                    destination = BlendMode.One;
                    break;
                case "SUBTRACT":
                    destination = BlendMode.One;
                    operation = BlendOp.ReverseSubtract;
                    break;
                case "MULTIPLY":
                    source = BlendMode.DstColor;
                    destination = BlendMode.Zero;
                    break;
                default:
                    // HARDLIGHT and DIVIDE have no single fixed-function equivalent.
                    warning = $"Layer blend mode {blendMode} has no Unity equivalent; " +
                              "using Regular instead.";
                    break;
            }

            material.SetFloat("_SrcBlend", (float)source);
            material.SetFloat("_DstBlend", (float)destination);
            material.SetFloat("_BlendOp", (float)operation);
        }

        private static void ApplySorting(Material material, GpManifest manifest, int layerIndex,
                                         GpSubmeshKey key, GreasePencilImportSettings settings)
        {
            var mode = settings.sortMode;
            if (mode == GpSortMode.FromBlender)
            {
                mode = manifest.@object.depth_order == "3D"
                    ? GpSortMode.Depth3D
                    : GpSortMode.LayerOrder2D;
            }

            if (mode == GpSortMode.Depth3D)
            {
                // Alpha clipped and depth writing, so strokes intersect the scene
                // correctly instead of relying on draw order.
                material.SetFloat("_ZWrite", 1f);
                material.SetFloat("_AlphaClip", 1f);
                material.SetFloat("_Cutoff", settings.alphaCutoff);
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = AlphaTestQueue;
                material.SetOverrideTag("RenderType", "TransparentCutout");
                return;
            }

            // Painter's order, the way Grease Pencil stacks layers in 2D mode.
            // Two slots per layer so a stroke draws over its own fill.
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = TransparentQueue + layerIndex * 2 + (key.Fill ? 0 : 1);
        }
    }
}
