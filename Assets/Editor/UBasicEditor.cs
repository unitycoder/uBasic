using System.IO;
using UnityEditor;
using UnityEngine;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace UBasic.EditorTools {

    /// <summary>Imports .ubas files as TextAssets and compiles them on import.
    ///
    /// This is the cheapest tooling win available: every save gets a real parse
    /// and type check with a line number in the console, so you find mistakes
    /// without entering play mode.</summary>
    [ScriptedImporter(1, "ubas")]
    public class UBasicImporter : ScriptedImporter {

        public override void OnImportAsset(AssetImportContext ctx) {
            string src = File.ReadAllText(ctx.assetPath);

            TextAsset asset = new TextAsset(src);
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);

            try {
                Chunk c = Compiler.Compile(src);
                // Surfaced in the importer inspector, not the console, so a
                // clean project stays quiet.
                ctx.LogImportWarning(string.Format(
                    "uBasic OK: {0} instructions, {1} globals, {2} procedures, {3} arrays.",
                    c.Code.Count, c.GlobalTypes.Count, c.Funcs.Count, c.Arrays.Count));
            } catch (UBasicError e) {
                ctx.LogImportError("uBasic compile error in " +
                    Path.GetFileName(ctx.assetPath) + ": " + e.Message);
            }
        }
    }

    [CustomEditor(typeof(UBasicRunner))]
    public class UBasicRunnerEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            UBasicRunner r = (UBasicRunner)target;

            EditorGUILayout.Space();
            if (GUILayout.Button("Recompile && Restart", GUILayout.Height(26)))
                r.Restart();

            if (r.IsFaulted && !string.IsNullOrEmpty(r.FaultMessage))
                EditorGUILayout.HelpBox(r.FaultMessage, MessageType.Error);
            else if (Application.isPlaying)
                EditorGUILayout.HelpBox("Running.", MessageType.Info);
        }
    }
}
