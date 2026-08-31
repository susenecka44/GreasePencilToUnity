using System;
using UnityEngine;

namespace GreasePencilToUnity.Editor
{
    /// <summary>
    /// Everything about a .gpencil import that can be changed without
    /// re-exporting from Blender.
    /// </summary>
    [Serializable]
    public class GreasePencilImportSettings
    {
        [Tooltip("Multiplies positions. Blender units map 1:1 to Unity units by default.")]
        public float scaleFactor = 1f;

        [Tooltip("Multiplies every stroke radius.")]
        public float widthScale = 1f;

        [Range(0f, 1f)]
        [Tooltip("How far the normal bends across the ribbon. 0 shades flat, 1 shades " +
                 "like a full tube cross-section.")]
        public float puff = 1f;

        [Range(0f, 1f)]
        [Tooltip("0 keeps Blender's colours exactly; 1 lights the strokes with the scene. " +
                 "Layers with Use Lights off in Blender stay at 0 either way.")]
        public float lightInfluence = 0.6f;

        [Tooltip("Keep the apparent thickness constant with distance instead of shrinking.")]
        public bool screenSpaceWidth;

        [Tooltip("Thickness multiplier used by Screen Space Width.")]
        public float screenWidthScale = 0.02f;

        [Tooltip("How strokes sort. From Blender follows the object's Stroke Depth Order.")]
        public GpSortMode sortMode = GpSortMode.FromBlender;

        [Range(0f, 1f)]
        [Tooltip("Alpha threshold used by the depth-sorted 3D mode.")]
        public float alphaCutoff = 0.35f;

        [Tooltip("Build an AnimationClip from the exported frames.")]
        public bool importAnimation = true;

        [Tooltip("What swaps the meshes at runtime.")]
        public GpPlaybackMode playback = GpPlaybackMode.RuntimeComponent;
    }
}
