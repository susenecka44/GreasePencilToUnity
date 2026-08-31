using System;
using System.Collections.Generic;
using UnityEngine;

namespace GreasePencilToUnity
{
    /// <summary>One layer's drawing changes over time.</summary>
    [Serializable]
    public class GreasePencilTrack
    {
        public string layerName;

        /// <summary>The layer's MeshFilter, whose mesh is swapped as the frame changes.</summary>
        public MeshFilter target;

        /// <summary>Ascending frame numbers at which the drawing changes.</summary>
        public int[] frames = Array.Empty<int>();

        /// <summary>The mesh shown from the matching frame on; null means nothing is drawn.</summary>
        public Mesh[] meshes = Array.Empty<Mesh>();
    }

    /// <summary>
    /// Plays a Grease Pencil import by swapping each layer's mesh, the way
    /// Blender holds a drawing until the next keyframe.
    ///
    /// <see cref="frame"/> is a plain serialised float, so an AnimationClip,
    /// Animator or Timeline can drive it instead of letting this component
    /// advance time on its own -- turn off <see cref="playOnAwake"/> then.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Grease Pencil/Grease Pencil Player")]
    public class GreasePencilPlayer : MonoBehaviour
    {
        [Tooltip("Frames per second the export was made at.")]
        public float frameRate = 24f;

        public int firstFrame = 1;
        public int lastFrame = 1;
        public bool loop = true;

        [Tooltip("Advance the frame automatically in play mode. Turn this off when an " +
                 "Animator or Timeline drives the Frame property instead.")]
        public bool playOnAwake = true;

        [Tooltip("Apply the current frame while editing, so scrubbing shows the drawing.")]
        public bool previewInEditor = true;

        [SerializeField]
        [Tooltip("The Blender frame currently shown. Animate this to drive playback.")]
        private float frame = 1f;

        public List<GreasePencilTrack> tracks = new List<GreasePencilTrack>();

        private bool _playing;

        /// <summary>The Blender frame currently shown.</summary>
        public float Frame
        {
            get => frame;
            set
            {
                frame = value;
                Apply();
            }
        }

        public void Play()
        {
            _playing = true;
        }

        public void Stop()
        {
            _playing = false;
        }

        private void OnEnable()
        {
            _playing = playOnAwake;
            Apply();
        }

        private void OnValidate()
        {
            Apply();
        }

        // Called by the animation system after it writes `frame`, which is how
        // an AnimationClip driving this component takes effect.
        private void OnDidApplyAnimationProperties()
        {
            Apply();
        }

        private void Update()
        {
            if (!Application.isPlaying || !_playing || tracks.Count == 0)
            {
                return;
            }

            frame += Time.deltaTime * frameRate;
            float span = lastFrame - firstFrame;
            if (frame > lastFrame)
            {
                if (loop && span > 0f)
                {
                    frame = firstFrame + Mathf.Repeat(frame - firstFrame, span);
                }
                else
                {
                    frame = lastFrame;
                    _playing = loop;
                }
            }

            Apply();
        }

        /// <summary>Show the drawing each layer holds at the current frame.</summary>
        public void Apply()
        {
            if (!Application.isPlaying && !previewInEditor)
            {
                return;
            }

            int current = Mathf.FloorToInt(frame);
            foreach (var track in tracks)
            {
                if (track?.target == null || track.frames == null || track.frames.Length == 0)
                {
                    continue;
                }

                int index = IndexAt(track.frames, current);
                Mesh mesh = index >= 0 && index < track.meshes.Length ? track.meshes[index] : null;
                if (track.target.sharedMesh != mesh)
                {
                    track.target.sharedMesh = mesh;
                }
            }
        }

        /// <summary>Index of the last entry at or before <paramref name="value"/>, or -1.</summary>
        private static int IndexAt(int[] frames, int value)
        {
            if (value < frames[0])
            {
                return -1;
            }

            int low = 0;
            int high = frames.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (frames[middle] <= value)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low;
        }
    }
}
