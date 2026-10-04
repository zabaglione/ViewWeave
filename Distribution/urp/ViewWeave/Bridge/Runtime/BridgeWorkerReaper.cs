using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    internal sealed class BridgeWorkerReaper : MonoBehaviour
    {
        static BridgeWorkerReaper instance;
        sealed class Lease
        {
            internal IntPtr handle;
            internal RenderTexture texture;
            internal CommandBuffer command;
            internal bool warned;
            internal bool needsClose;
        }

        readonly List<Lease> leases = new List<Lease>();
        internal static int PendingCount => instance == null ? 0 : instance.leases.Count;

        internal static void Enqueue(IntPtr handle, RenderTexture texture, CommandBuffer command, bool needsClose)
        {
            if (handle == IntPtr.Zero) return;
            BridgeWorkerReaper reaper = Ensure();
            foreach (Lease lease in reaper.leases) if (lease.handle == handle) return;
            reaper.leases.Add(new Lease { handle = handle, texture = texture, command = command, needsClose = needsClose });
        }

        static BridgeWorkerReaper Ensure()
        {
            if (instance != null) return instance;
            var host = new GameObject("ViewWeave Bridge Worker Reaper");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            instance = host.AddComponent<BridgeWorkerReaper>();
            return instance;
        }

        void Update()
        {
            for (int i = leases.Count - 1; i >= 0; --i)
            {
                Lease lease = leases[i];
                try
                {
                    if (lease.needsClose)
                    {
                        int closeResult = BridgeWorkerNative.lkg_worker_close(lease.handle);
                        if (closeResult != 0) { Warn(lease, "worker close is still pending"); continue; }
                        lease.needsClose = false;
                    }
                    if (BridgeWorkerNative.lkg_worker_poll(lease.handle, out BridgeWorkerNative.Status status) != 0)
                    {
                        int closeResult = BridgeWorkerNative.lkg_worker_close(lease.handle);
                        if (closeResult != 0) Warn(lease, "worker poll/close is still pending");
                        continue;
                    }
                    if (status.state == 2)
                    {
                        int closeResult = BridgeWorkerNative.lkg_worker_close(lease.handle);
                        if (closeResult != 0) Warn(lease, "worker close is still pending");
                    }
                    if (status.state != 2 && status.state != 4) continue;
                    int result = BridgeWorkerNative.lkg_worker_release(lease.handle);
                    if (result == -2) continue;
                    if (result != 0) { Warn(lease, "worker release returned a pending error"); continue; }
                    DestroyLease(lease);
                    leases.RemoveAt(i);
                }
                catch (DllNotFoundException) { Warn(lease, "worker release ABI is unavailable"); }
                catch (EntryPointNotFoundException) { Warn(lease, "worker release entry point is unavailable"); }
                catch (Exception error)
                {
                    Warn(lease, "reaper retry failed: " + Sanitize(error.Message));
                }
            }
            if (leases.Count == 0)
            {
                instance = null;
                Destroy(gameObject);
            }
        }

        static void DestroyLease(Lease lease)
        {
            if (lease.command != null) lease.command.Release();
            if (lease.texture == null) return;
            if (RenderTexture.active == lease.texture) RenderTexture.active = null;
            if (lease.texture.IsCreated()) lease.texture.Release();
            Destroy(lease.texture);
        }

        static void Warn(Lease lease, string message)
        {
            if (lease.warned) return;
            lease.warned = true;
            Debug.LogWarning("[BridgeWorker] " + Sanitize(message));
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new List<char>(value.Length);
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c);
            return new string(chars.ToArray());
        }
    }
}
