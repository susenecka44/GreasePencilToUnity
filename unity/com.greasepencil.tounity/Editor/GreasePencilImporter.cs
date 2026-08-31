using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace GreasePencilToUnity.Editor
{
    /// <summary>
    /// Imports a .gpencil file exported from Blender 5 into a GameObject
    /// hierarchy: one child per Grease Pencil layer, a ribbon mesh per drawing,
    /// materials carrying the layer's blend mode and sort order, and an
    /// AnimationClip when the export contains more than one frame.
    /// </summary>
    [ScriptedImporter(Version, Extension)]
    public sealed class GreasePencilImporter : ScriptedImporter
    {
        public const int Version = 2;
        public const string Extension = "gpencil";

        public GreasePencilImportSettings settings = new GreasePencilImportSettings();

        /// <summary>Filled in during import and shown in the inspector.</summary>
        [SerializeField] private string summary;

        public string Summary => summary;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            GpFile file;
            try
            {
                file = GpFile.Parse(File.ReadAllBytes(ctx.assetPath), Path.GetFileName(ctx.assetPath));
            }
            catch (Exception error)
            {
                ctx.LogImportError($"Could not read {ctx.assetPath}: {error.Message}", null);
                return;
            }

            var manifest = file.Manifest;
            var shader = Shader.Find(GpMaterialBuilder.ShaderName);
            if (shader == null)
            {
                ctx.LogImportError(
                    $"Shader \"{GpMaterialBuilder.ShaderName}\" was not found. The Grease Pencil " +
                    "package needs the Universal Render Pipeline.", null);
            }
            else
            {
                ctx.DependsOnSourceAsset(AssetDatabase.GetAssetPath(shader));
            }

            var root = new GameObject(SafeName(manifest.@object.name));
            ApplyTransform(root.transform, manifest.@object.matrix, settings.scaleFactor);

            bool encodeToGamma = PlayerSettings.colorSpace == ColorSpace.Gamma;
            var drawings = new GpDrawingData[manifest.drawings.Length];
            for (int i = 0; i < drawings.Length; i++)
            {
                drawings[i] = GpDrawingData.Read(file, manifest.drawings[i], encodeToGamma);
            }

            var layers = new List<GpLayerBuild>(manifest.layers.Length);
            int meshCount = 0;
            var usedNames = new HashSet<string>();

            for (int layerIndex = 0; layerIndex < manifest.layers.Length; layerIndex++)
            {
                var build = BuildLayer(ctx, manifest, layerIndex, drawings, shader, root, usedNames);
                layers.Add(build);
                meshCount += build.MeshCount;
            }

            AnimationClip clip = null;
            if (settings.importAnimation)
            {
                clip = GpClipBuilder.Build(manifest, file, layers, settings);
            }

            if (clip != null)
            {
                ctx.AddObjectToAsset("clip", clip);
                if (settings.playback == GpPlaybackMode.MeshSwapCurves)
                {
                    root.AddComponent<Animator>();
                }
            }

            if (settings.importAnimation && settings.playback == GpPlaybackMode.RuntimeComponent &&
                HasDrawingChanges(layers))
            {
                AddPlayer(root, manifest, layers);
            }

            ctx.AddObjectToAsset("root", root);
            ctx.SetMainObject(root);

            summary = BuildSummary(manifest, meshCount, clip);
        }

        private GpLayerBuild BuildLayer(AssetImportContext ctx, GpManifest manifest, int layerIndex,
                                        GpDrawingData[] drawings, Shader shader, GameObject root,
                                        HashSet<string> usedNames)
        {
            var layer = manifest.layers[layerIndex];
            var referenced = DistinctDrawings(layer);

            var sources = new List<GpDrawingData>(referenced.Count);
            foreach (int index in referenced)
            {
                sources.Add(drawings[index]);
            }

            var layout = GpMeshBuilder.LayoutFor(sources);

            // Every mesh in this layer uses the same submesh layout, so the
            // renderer's material array stays valid when animation swaps meshes.
            var meshByDrawing = new Dictionary<int, Mesh>(referenced.Count);
            foreach (int index in referenced)
            {
                var mesh = GpMeshBuilder.Build(drawings[index], layout, $"{layer.name}_{index}");
                ctx.AddObjectToAsset($"mesh_{layerIndex}_{index}", mesh);
                meshByDrawing[index] = mesh;
            }

            var gameObject = new GameObject(UniqueName(layer.name, layerIndex, usedNames));
            gameObject.transform.SetParent(root.transform, false);

            var filter = gameObject.AddComponent<MeshFilter>();
            var renderer = gameObject.AddComponent<MeshRenderer>();
            // Grease Pencil never casts or receives shadows.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.staticShadowCaster = false;

            var materials = new Material[layout.Count];
            for (int i = 0; i < layout.Count; i++)
            {
                if (shader == null)
                {
                    continue;
                }

                materials[i] = GpMaterialBuilder.Create(
                    shader, manifest, layerIndex, layout[i], settings, out string warning);
                if (warning != null)
                {
                    ctx.LogImportWarning($"{layer.name}: {warning}", null);
                }

                ctx.AddObjectToAsset($"material_{layerIndex}_{layout[i]}", materials[i]);
            }

            renderer.sharedMaterials = materials;

            var meshes = new Mesh[layer.frame_times?.Length ?? 0];
            for (int i = 0; i < meshes.Length; i++)
            {
                int drawing = layer.frame_drawings[i];
                meshes[i] = drawing >= 0 && meshByDrawing.TryGetValue(drawing, out var mesh)
                    ? mesh
                    : null;
            }

            filter.sharedMesh = meshes.Length > 0 ? meshes[0] : null;
            gameObject.SetActive(!layer.hide);

            return new GpLayerBuild
            {
                Layer = layer,
                GameObject = gameObject,
                Filter = filter,
                Layout = layout,
                Meshes = meshes,
                MeshCount = meshByDrawing.Count,
            };
        }

        private static List<int> DistinctDrawings(GpLayer layer)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (layer.frame_drawings == null)
            {
                return result;
            }

            foreach (int index in layer.frame_drawings)
            {
                if (index >= 0 && seen.Add(index))
                {
                    result.Add(index);
                }
            }

            return result;
        }

        private static bool HasDrawingChanges(List<GpLayerBuild> layers)
        {
            foreach (var layer in layers)
            {
                if (layer.Layer.frame_times != null && layer.Layer.frame_times.Length > 1)
                {
                    return true;
                }
            }

            return false;
        }

        private void AddPlayer(GameObject root, GpManifest manifest, List<GpLayerBuild> layers)
        {
            var player = root.AddComponent<GreasePencilPlayer>();
            player.frameRate = manifest.scene.fps > 0f ? manifest.scene.fps : 24f;
            player.firstFrame = manifest.scene.frame_start;
            player.lastFrame = manifest.scene.frame_end;
            player.tracks = new List<GreasePencilTrack>(layers.Count);

            foreach (var layer in layers)
            {
                player.tracks.Add(new GreasePencilTrack
                {
                    layerName = layer.Layer.name,
                    target = layer.Filter,
                    frames = layer.Layer.frame_times ?? Array.Empty<int>(),
                    meshes = layer.Meshes,
                });
            }
        }

        private static void ApplyTransform(Transform transform, float[] matrix, float scaleFactor)
        {
            if (matrix == null || matrix.Length < 16)
            {
                return;
            }

            var m = GpFile.ToMatrix(matrix, 0);
            transform.localPosition = (Vector3)m.GetColumn(3) * scaleFactor;
            transform.localRotation = m.rotation;
            transform.localScale = m.lossyScale;
        }

        private static string SafeName(string name)
        {
            return string.IsNullOrEmpty(name) ? "GreasePencil" : name;
        }

        /// <summary>
        /// Layer names become the animation paths, so duplicates would make two
        /// layers share one curve.
        /// </summary>
        private static string UniqueName(string name, int index, HashSet<string> used)
        {
            string candidate = SafeName(name);
            if (used.Add(candidate))
            {
                return candidate;
            }

            candidate = $"{candidate}_{index}";
            used.Add(candidate);
            return candidate;
        }

        private static string BuildSummary(GpManifest manifest, int meshCount, AnimationClip clip)
        {
            int points = 0;
            foreach (var drawing in manifest.drawings)
            {
                points += drawing.n_points;
            }

            string animation = clip == null
                ? "static"
                : $"{manifest.animation.mode.ToLowerInvariant()}, frames " +
                  $"{manifest.scene.frame_start}-{manifest.scene.frame_end} at {manifest.scene.fps:0.##} fps";

            return $"{manifest.layers.Length} layers, {manifest.materials.Length} materials, " +
                   $"{manifest.drawings.Length} drawings ({points} points), {meshCount} meshes\n" +
                   $"Sorting: {manifest.@object.depth_order} from Blender\n" +
                   $"Animation: {animation}";
        }
    }
}
