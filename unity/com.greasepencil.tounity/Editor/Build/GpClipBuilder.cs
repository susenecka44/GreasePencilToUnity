using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GreasePencilToUnity.Editor
{
    /// <summary>What drives the drawing changes at runtime.</summary>
    public enum GpPlaybackMode
    {
        /// <summary>
        /// A GreasePencilPlayer component swaps the meshes. Plays on its own and
        /// its Frame property can still be driven by an Animator or Timeline.
        /// </summary>
        RuntimeComponent = 0,

        /// <summary>
        /// Object-reference curves on each layer's MeshFilter, the same mechanism
        /// Unity uses for sprite swapping. No runtime component involved.
        /// </summary>
        MeshSwapCurves = 1,
    }

    /// <summary>Everything the clip builder needs to know about one imported layer.</summary>
    public sealed class GpLayerBuild
    {
        public GpLayer Layer;
        public GameObject GameObject;
        public MeshFilter Filter;
        public List<GpSubmeshKey> Layout;

        /// <summary>One mesh per entry in <c>Layer.frame_times</c>; null where nothing is drawn.</summary>
        public Mesh[] Meshes;

        /// <summary>How many distinct meshes this layer contributed, for the import summary.</summary>
        public int MeshCount;
    }

    public static class GpClipBuilder
    {
        /// <summary>
        /// Build the AnimationClip for an import, or null when there is nothing
        /// to animate.
        /// </summary>
        public static AnimationClip Build(GpManifest manifest, GpFile file,
                                          List<GpLayerBuild> layers,
                                          GreasePencilImportSettings settings)
        {
            float fps = manifest.scene.fps > 0f ? manifest.scene.fps : 24f;
            int firstFrame = manifest.scene.frame_start;
            int lastFrame = manifest.scene.frame_end;

            var matrices = file.Floats(manifest.animation.matrices);
            bool hasTransform = matrices.Length >= 32 && manifest.animation.times != null;
            bool hasDrawingChanges = false;
            foreach (var layer in layers)
            {
                if (layer.Layer.frame_times != null && layer.Layer.frame_times.Length > 1)
                {
                    hasDrawingChanges = true;
                    break;
                }
            }

            if (!hasTransform && !hasDrawingChanges)
            {
                return null;
            }

            var clip = new AnimationClip
            {
                name = manifest.@object.name + "_Anim",
                frameRate = fps,
            };

            if (hasTransform)
            {
                AddTransformCurves(clip, manifest, matrices, fps, firstFrame, settings.scaleFactor);
            }

            if (hasDrawingChanges)
            {
                if (settings.playback == GpPlaybackMode.MeshSwapCurves)
                {
                    AddMeshSwapCurves(clip, layers, fps, firstFrame);
                }
                else
                {
                    AddPlayerFrameCurve(clip, fps, firstFrame, lastFrame);
                }
            }

            var clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
            clipSettings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, clipSettings);
            return clip;
        }

        /// <summary>
        /// One object-reference key per drawing change. These are stepped by
        /// construction, so a drawing holds until the next key exactly as it does
        /// in Blender's dope sheet.
        /// </summary>
        private static void AddMeshSwapCurves(AnimationClip clip, List<GpLayerBuild> layers,
                                              float fps, int firstFrame)
        {
            foreach (var layer in layers)
            {
                int[] times = layer.Layer.frame_times;
                if (times == null || times.Length == 0)
                {
                    continue;
                }

                var keys = new ObjectReferenceKeyframe[times.Length];
                for (int i = 0; i < times.Length; i++)
                {
                    keys[i] = new ObjectReferenceKeyframe
                    {
                        time = (times[i] - firstFrame) / fps,
                        value = layer.Meshes[i],
                    };
                }

                var binding = EditorCurveBinding.PPtrCurve(
                    layer.GameObject.name, typeof(MeshFilter), "m_Mesh");
                AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
            }
        }

        /// <summary>
        /// A single linear ramp on GreasePencilPlayer.frame, so Timeline and the
        /// Animator can scrub the drawing without object-reference curves.
        /// </summary>
        private static void AddPlayerFrameCurve(AnimationClip clip, float fps,
                                                int firstFrame, int lastFrame)
        {
            var curve = AnimationCurve.Linear(
                0f, firstFrame, Mathf.Max(lastFrame - firstFrame, 1) / fps, lastFrame);
            var binding = EditorCurveBinding.FloatCurve(
                string.Empty, typeof(GreasePencilPlayer), "frame");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void AddTransformCurves(AnimationClip clip, GpManifest manifest,
                                               float[] matrices, float fps, int firstFrame,
                                               float scaleFactor)
        {
            int[] times = manifest.animation.times;
            int count = Mathf.Min(times.Length, matrices.Length / 16);
            if (count < 2)
            {
                return;
            }

            var position = NewCurves(3);
            var rotation = NewCurves(4);
            var scale = NewCurves(3);

            var previous = Quaternion.identity;
            for (int i = 0; i < count; i++)
            {
                var matrix = GpFile.ToMatrix(matrices, i * 16);
                float time = (times[i] - firstFrame) / fps;

                Vector3 translation = matrix.GetColumn(3) * scaleFactor;
                Quaternion orientation = matrix.rotation;
                Vector3 lossyScale = matrix.lossyScale;

                // Keep the quaternion on the same hemisphere as the previous key,
                // otherwise the interpolated rotation takes the long way round.
                if (i > 0 && Quaternion.Dot(previous, orientation) < 0f)
                {
                    orientation = new Quaternion(
                        -orientation.x, -orientation.y, -orientation.z, -orientation.w);
                }

                previous = orientation;

                AddKey(position[0], time, translation.x);
                AddKey(position[1], time, translation.y);
                AddKey(position[2], time, translation.z);
                AddKey(rotation[0], time, orientation.x);
                AddKey(rotation[1], time, orientation.y);
                AddKey(rotation[2], time, orientation.z);
                AddKey(rotation[3], time, orientation.w);
                AddKey(scale[0], time, lossyScale.x);
                AddKey(scale[1], time, lossyScale.y);
                AddKey(scale[2], time, lossyScale.z);
            }

            Bind(clip, position, "m_LocalPosition");
            Bind(clip, rotation, "m_LocalRotation");
            Bind(clip, scale, "m_LocalScale");
        }

        private static AnimationCurve[] NewCurves(int count)
        {
            var curves = new AnimationCurve[count];
            for (int i = 0; i < count; i++)
            {
                curves[i] = new AnimationCurve();
            }

            return curves;
        }

        private static void AddKey(AnimationCurve curve, float time, float value)
        {
            curve.AddKey(new Keyframe(time, value)
            {
                inTangent = 0f,
                outTangent = 0f,
                weightedMode = WeightedMode.None,
            });
        }

        private static void Bind(AnimationClip clip, AnimationCurve[] curves, string property)
        {
            var suffixes = curves.Length == 4
                ? new[] { ".x", ".y", ".z", ".w" }
                : new[] { ".x", ".y", ".z" };

            for (int i = 0; i < curves.Length; i++)
            {
                if (curves[i].length == 0)
                {
                    continue;
                }

                // Linear between samples: the exporter already sampled every frame
                // it needed, so nothing should overshoot.
                for (int key = 0; key < curves[i].length; key++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curves[i], key, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curves[i], key, AnimationUtility.TangentMode.Linear);
                }

                var binding = EditorCurveBinding.FloatCurve(
                    string.Empty, typeof(Transform), property + suffixes[i]);
                AnimationUtility.SetEditorCurve(clip, binding, curves[i]);
            }
        }
    }
}
