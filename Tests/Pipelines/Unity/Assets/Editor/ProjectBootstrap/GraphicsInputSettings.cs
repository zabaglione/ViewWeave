#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    public static class GraphicsInputSettings
    {
        public static void Apply()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            Object playerSettings = null;
            foreach (Object asset in assets)
            {
                if (asset != null && asset.GetType().Name == "PlayerSettings")
                {
                    playerSettings = asset;
                    break;
                }
            }
            if (playerSettings == null) throw new System.InvalidOperationException("PlayerSettings asset is unavailable");
            SerializedObject settings = new SerializedObject(playerSettings);
            SerializedProperty property = settings.FindProperty("activeInputHandler");
            if (property == null) property = settings.FindProperty("m_ActiveInputHandler");
            if (property == null) throw new System.InvalidOperationException("PlayerSettings active input handler property is unavailable");
            property.intValue = 1;
            settings.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
