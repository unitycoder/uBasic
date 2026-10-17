using System.IO;
using UnityEngine;
using UnityEditor;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace Unitycoder.UBasic.EditorTools {

    /// <summary>Imports .ubas files as TextAssets and compiles them on import.
    ///
    /// This is the cheapest tooling win available: every save gets a real parse
    /// and type check with a line number, so mistakes surface without entering
    /// play mode. Results land on the asset's importer inspector rather than
    /// the console, so a clean project stays quiet.</summary>
    [ScriptedImporter(1, "ubas")]
    public class UBasicImporter : ScriptedImporter {

        public override void OnImportAsset(AssetImportContext ctx) {
            string src = File.ReadAllText(ctx.assetPath);

            TextAsset asset = new TextAsset(src);
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);

            try {
                Chunk c = Compiler.Compile(src);
                ctx.LogImportWarning(string.Format(
                    "uBasic OK: {0} instructions, {1} globals, {2} procedures, {3} arrays.",
                    c.Code.Count, c.GlobalTypes.Count, c.Funcs.Count, c.Arrays.Count));
            } catch (UBasicError e) {
                ctx.LogImportError("uBasic compile error in " +
                    Path.GetFileName(ctx.assetPath) + ": " + e.Message);
            }
        }
    }
}
