#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Samples.WindowsHdrpLit
{
    public static class ViewWeaveHdrpLitMaterialInstaller
    {
        [MenuItem("ViewWeave/Samples/Windows HDRP Lit/Create Material Resource")]
        public static void CreateMaterialResource()
        {
            var guids = AssetDatabase.FindAssets("ViewWeaveHdrpLitMaterialInstaller t:MonoScript");
            if (guids.Length == 0)
            {
                Debug.LogError("ViewWeave HDRP sample installer script was not found under Assets");
                return;
            }
            string scriptPath = null;
            foreach (var guid in guids)
            {
                var candidate = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                if (candidate.StartsWith("Assets/", System.StringComparison.Ordinal))
                {
                    scriptPath = candidate;
                    break;
                }
            }
            if (scriptPath == null)
            {
                Debug.LogError("ViewWeave HDRP sample installer must be imported under Assets");
                return;
            }
            var sampleRoot = scriptPath.Substring(0, scriptPath.LastIndexOf("/Editor/", System.StringComparison.Ordinal));
            CreateMaterialResourceAt(sampleRoot);
        }

        public static void CreateMaterialResourceAt(string sampleRoot)
        {
            sampleRoot = NormalizeSampleRoot(sampleRoot);
            if (sampleRoot == null)
            {
                Debug.LogError("ViewWeave HDRP sample root must be under Assets");
                return;
            }
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null)
            {
                Debug.LogError("ViewWeave HDRP sample shader unavailable");
                return;
            }

            var resources = sampleRoot + "/Resources";
            var viewWeave = resources + "/ViewWeave";
            if (!AssetDatabase.IsValidFolder(resources))
                AssetDatabase.CreateFolder(sampleRoot, "Resources");
            if (!AssetDatabase.IsValidFolder(viewWeave))
                AssetDatabase.CreateFolder(resources, "ViewWeave");
            var assetPath = viewWeave + "/HdrpLit.mat";

            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                material = new Material(shader) { name = "HdrpLit" };
                AssetDatabase.CreateAsset(material, assetPath);
            }
            else
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }
            AssetDatabase.SaveAssets();
        }

        static string NormalizeSampleRoot(string sampleRoot)
        {
            if (string.IsNullOrEmpty(sampleRoot)) return null;
            sampleRoot = sampleRoot.Replace('\\', '/');
            if (!sampleRoot.StartsWith("Assets/", System.StringComparison.Ordinal)) return null;
            var assetsRoot = System.IO.Path.GetFullPath(Application.dataPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(assetsRoot, sampleRoot.Substring("Assets/".Length)));
            var prefix = assetsRoot + System.IO.Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return null;
            if (!AssetDatabase.IsValidFolder(sampleRoot)) return null;
            return sampleRoot;
        }
    }
}
#endif
