using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LookingGlass.Community.Editor
{
    public interface IViewWeaveRigProvider
    {
        bool IsSupported { get; }
        MonoBehaviour AddAdapter(GameObject target);
    }

    public static class ViewWeaveRigProviderRegistry
    {
        static readonly List<IViewWeaveRigProvider> providers = new List<IViewWeaveRigProvider>();
        public static void Register(IViewWeaveRigProvider provider) { if (provider != null && !providers.Contains(provider)) providers.Add(provider); }
        public static bool HasSupportedProvider()
        {
            foreach (IViewWeaveRigProvider provider in providers) if (provider.IsSupported) return true;
            return false;
        }
        public static bool TryAddAdapter(GameObject target, out MonoBehaviour adapter)
        {
            adapter = null;
            foreach (IViewWeaveRigProvider provider in providers)
                if (provider.IsSupported) { adapter = provider.AddAdapter(target); return adapter != null; }
            return false;
        }
    }

    public static class ViewWeaveRigMenu
    {
        [MenuItem("GameObject/ViewWeave/Quilt Rig", false, 10)]
        static void CreateRig(MenuCommand command)
        {
            if (!ViewWeaveRigProviderRegistry.HasSupportedProvider())
            {
                Debug.LogError("ViewWeave Quilt Rig requires a supported URP or HDRP render pipeline.");
                return;
            }
            GameObject parent = command.context as GameObject;
            GameObject target = new GameObject("ViewWeave Quilt Rig");
            if (parent != null) GameObjectUtility.SetParentAndAlign(target, parent);
            Undo.RegisterCreatedObjectUndo(target, "Create ViewWeave Quilt Rig");
            ViewWeaveRig rig = target.AddComponent<ViewWeaveRig>();
            if (!ViewWeaveRigProviderRegistry.TryAddAdapter(target, out MonoBehaviour adapter))
            {
                Undo.DestroyObjectImmediate(target);
                Debug.LogError("ViewWeave Quilt Rig requires a supported URP or HDRP render pipeline.");
                return;
            }
            rig.SetGuard(adapter);
            if (!rig.ConfigureComponents())
            {
                Undo.DestroyObjectImmediate(target);
                Debug.LogError("ViewWeave Quilt Rig configuration failed.");
                return;
            }
            Selection.activeGameObject = target;
        }
    }
}
