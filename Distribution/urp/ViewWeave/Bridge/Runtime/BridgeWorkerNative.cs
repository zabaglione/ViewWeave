using System;
using System.Runtime.InteropServices;

namespace ViewWeave
{
    internal static class BridgeWorkerNative
    {
        const string Library = "lookingglass_bridge_shim";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct Status
        {
            public uint state;
            public uint displayIndex;
            public uint width;
            public uint height;
            public float aspect;
            public float viewcone;
            public int quiltWidth;
            public int quiltHeight;
            public int quiltColumns;
            public int quiltRows;
            public uint submitted;
            public uint completed;
            public uint dropped;
            public uint lastErrorCode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string error;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PreviewLeaseIdentityV1
        {
            public uint structSize;
            public uint version;
            public ulong endpointId;
            public ulong leaseGeneration;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct PreviewLeaseStatusV1
        {
            public uint structSize;
            public uint version;
            public uint state;
            public uint reserved0;
            public ulong endpointId;
            public ulong leaseGeneration;
            public int lastResult;
            public uint reserved1;
            public Status workerStatus;
        }

        internal const uint PreviewLeaseStateActive = 0;
        internal const uint PreviewLeaseStateReturning = 1;
        internal const uint PreviewLeaseStateReturnedProven = 2;
        internal const uint PreviewLeaseStateFaulted = 3;
        internal const int PreviewLeaseResultOk = 0;
        internal const int PreviewLeaseResultBusy = 1;
        internal const int PreviewLeaseResultInvalid = -1;
        internal const int PreviewLeaseResultUnsupported = -2;
        internal const int PreviewLeaseResultRestartRequired = -3;
        internal const int PreviewLeaseResultNativeFailure = -4;
        internal const int PreviewLeaseResultStale = -5;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FinalShutdownStatusV1
        {
            public uint structSize;
            public uint version;
            public uint state;
            public uint registeredContexts;
            public uint closedUncollectedContexts;
            public uint hostRunning;
            public uint hostJoined;
            public int lastResult;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PhysicalDisplayInfoV1
        {
            public uint structSize;
            public uint version;
            public uint pixelWidth;
            public uint pixelHeight;
            public float dpi;
            public uint valid;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_begin([MarshalAs(UnmanagedType.LPUTF8Str)] string runtimePath, [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationName, uint displayOrdinal, IntPtr producerDevice);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_acquire_preview_lease_v1(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string runtimePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationName,
            uint displayOrdinal, IntPtr producerDevice, out IntPtr leaseHandle, out PreviewLeaseIdentityV1 identity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_request_preview_lease_return_v1(IntPtr leaseHandle, ref PreviewLeaseIdentityV1 identity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_get_preview_lease_status_v1(IntPtr leaseHandle, ref PreviewLeaseIdentityV1 identity,
            out PreviewLeaseStatusV1 status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_ack_preview_lease_return_v1(IntPtr leaseHandle, ref PreviewLeaseIdentityV1 identity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_poll(IntPtr handle, out Status status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_get_physical_display_v1(IntPtr handle, ref PhysicalDisplayInfoV1 info);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_close(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_reset_transfer(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_release(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_make_draw_data(IntPtr handle, IntPtr texture, uint columns, uint rows, float aspect, float zoom);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_try_make_preview_draw_data_v1(IntPtr leaseHandle,
            ref PreviewLeaseIdentityV1 identity, IntPtr texture, uint columns, uint rows, float aspect, float zoom, out IntPtr drawData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_cancel_draw_data(IntPtr drawData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_render_event_func();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_render_event_id(out int eventId);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_close_all();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_collect_closed();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint lkg_worker_pending_count();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_request_final_shutdown();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_get_final_shutdown_status_v1(ref FinalShutdownStatusV1 status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_wait_final_shutdown_v1(uint timeoutMilliseconds, ref FinalShutdownStatusV1 status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_set_preview_paused(IntPtr handle, uint paused);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_get_preview_pause_state(IntPtr handle, out uint phase);
    }
}
