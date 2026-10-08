using System;
using UnityEngine;

namespace ViewWeave
{
    /// <summary>Persists ownership of an idle metadata worker across Editor domain reloads.</summary>
    internal sealed class BridgeDisplayMetadataRecoveryRecord : ScriptableObject
    {
        [SerializeField] long nativeHandle;
        [SerializeField] string nativeModulePath = string.Empty;
        [SerializeField] long nativeModuleBaseAddress;
        [SerializeField] bool closeRequested;
        [SerializeField] bool cancelled;
        [SerializeField] string diagnostic = string.Empty;

        internal IntPtr Handle => new IntPtr(nativeHandle);
        internal BridgeNativeModuleIdentity NativeModuleIdentity =>
            new BridgeNativeModuleIdentity("lookingglass_bridge_shim.dll", new IntPtr(nativeModuleBaseAddress), nativeModulePath);
        internal bool CloseRequested => closeRequested;
        internal bool Cancelled => cancelled;
        internal string Diagnostic => diagnostic;

        internal static BridgeDisplayMetadataRecoveryRecord Create(IntPtr handle, BridgeNativeModuleIdentity identity)
        {
            BridgeDisplayMetadataRecoveryRecord record = CreateInstance<BridgeDisplayMetadataRecoveryRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.nativeHandle = handle.ToInt64();
            record.nativeModulePath = identity.FullPath ?? string.Empty;
            record.nativeModuleBaseAddress = identity.BaseAddress.ToInt64();
            record.diagnostic = "Metadata worker ownership is retained until Bridge cleanup is proven.";
            return record;
        }

        internal void MarkCancelled()
        {
            cancelled = true;
            diagnostic = "Metadata result was cancelled; native cleanup remains pending.";
        }

        internal void MarkCloseRequested()
        {
            closeRequested = true;
            diagnostic = "Metadata worker close was requested; native cleanup remains pending.";
        }

        internal void SetDiagnostic(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                diagnostic = string.Empty;
                return;
            }
            char[] ascii = Array.FindAll(value.ToCharArray(), character => character >= 0x20 && character <= 0x7e);
            diagnostic = new string(ascii);
        }

        internal void ReleaseProvenRecord()
        {
            nativeHandle = 0;
            diagnostic = "Metadata worker cleanup and release were proven.";
            DestroyImmediate(this);
        }
    }
}
