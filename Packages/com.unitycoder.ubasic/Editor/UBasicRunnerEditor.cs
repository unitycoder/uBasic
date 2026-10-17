using UnityEngine;
using UnityEditor;

namespace Unitycoder.UBasic.EditorTools {

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
