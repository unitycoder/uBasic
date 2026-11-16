using System.IO;
using UnityEngine;
using UnityEditor;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace Unitycoder.UBasic.EditorTools {

    /// <summary>Imports .ubas and .bas files as TextAssets and compiles them on import.
    ///
    /// This is the cheapest tooling win available: every save gets a real parse
    /// and type check with a line number, so mistakes surface without entering
    /// play mode. Results land on the asset's importer inspector rather than
    /// the console, so a clean project stays quiet.
    ///
    /// A .BAS saved by DOS QBasic is not UTF-8: its box-drawing and block
    /// characters are code page 437 bytes. Those files are decoded as CP437
    /// here, so the TextAsset holds the right Unicode characters.</summary>
    [ScriptedImporter(2, new[] { "ubas", "bas" })]
    public class UBasicImporter : ScriptedImporter {

        public override void OnImportAsset(AssetImportContext ctx) {
            byte[] raw = File.ReadAllBytes(ctx.assetPath);
            string src;
            try {
                src = new System.Text.UTF8Encoding(false, true).GetString(raw);
                if (src.Length > 0 && src[0] == '\uFEFF') src = src.Substring(1);
            } catch (System.ArgumentException) {
                src = Cp437.Decode(raw);          // not valid UTF-8: a DOS file
            }

            TextAsset asset = new TextAsset(src);
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);

            try {
                Chunk c = Compiler.Compile(src);
                ctx.LogImportWarning(string.Format(
                    "uBasic OK ({4} dialect): {0} instructions, {1} globals, {2} procedures, {3} arrays.",
                    c.Code.Count, c.GlobalTypes.Count, c.Funcs.Count, c.Arrays.Count,
                    c.QBasic ? "QBasic" : "uBasic"));
            } catch (UBasicError e) {
                ctx.LogImportError("uBasic compile error in " +
                    Path.GetFileName(ctx.assetPath) + ": " + e.Message);
            }
        }
    }
}
