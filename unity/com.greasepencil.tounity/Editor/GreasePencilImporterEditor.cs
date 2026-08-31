using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GreasePencilToUnity.Editor
{
    [CustomEditor(typeof(GreasePencilImporter))]
    public sealed class GreasePencilImporterEditor : ScriptedImporterEditor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var settings = serializedObject.FindProperty("settings");
            EditorGUILayout.PropertyField(settings, new GUIContent("Import Settings"), true);

            var importer = (GreasePencilImporter)target;
            if (!string.IsNullOrEmpty(importer.Summary))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Contents", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(importer.Summary, MessageType.None);
            }

            var playback = settings.FindPropertyRelative("playback");
            if (playback != null && playback.enumValueIndex == (int)GpPlaybackMode.MeshSwapCurves)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Mesh swap curves need an Animator Controller or a Timeline track to play. " +
                    "Runtime Component playback plays on its own.", MessageType.Info);

                if (GUILayout.Button("Create Animator Controller"))
                {
                    CreateController(importer.assetPath);
                }
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        /// <summary>
        /// Writes a one-state controller next to the imported file, playing the
        /// clip that came out of the import.
        /// </summary>
        private static void CreateController(string assetPath)
        {
            AnimationClip clip = null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (asset is AnimationClip found)
                {
                    clip = found;
                    break;
                }
            }

            if (clip == null)
            {
                EditorUtility.DisplayDialog(
                    "Grease Pencil",
                    "This import has no AnimationClip. Export with an animation mode other than " +
                    "None, and make sure Import Animation is on.",
                    "OK");
                return;
            }

            string directory = Path.GetDirectoryName(assetPath) ?? "Assets";
            string name = Path.GetFileNameWithoutExtension(assetPath);
            string controllerPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(directory, name + ".controller").Replace('\\', '/'));

            var controller = AnimatorController.CreateAnimatorControllerAtPathWithClip(
                controllerPath, clip);
            Selection.activeObject = controller;
            EditorGUIUtility.PingObject(controller);
        }
    }
}
