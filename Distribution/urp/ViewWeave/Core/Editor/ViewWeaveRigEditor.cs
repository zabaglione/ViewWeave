using System.Collections.Generic;
using UnityEngine;

namespace ViewWeave.Editor
{
    public interface IViewWeaveRigProvider
    {
        bool IsSupported { get; }
        MonoBehaviour AddAdapter(GameObject target, Camera framingCamera);
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
        public static bool TryAddAdapter(GameObject target, Camera framingCamera, out MonoBehaviour adapter)
        {
            adapter = null;
            foreach (IViewWeaveRigProvider provider in providers)
                if (provider.IsSupported) { adapter = provider.AddAdapter(target, framingCamera); return adapter != null; }
            return false;
        }
    }

}
