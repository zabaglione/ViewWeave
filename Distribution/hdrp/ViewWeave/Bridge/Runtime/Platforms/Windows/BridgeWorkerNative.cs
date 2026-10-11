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

        [StructLayout(LayoutKind.Sequential)]
        internal struct WorkerOutputOptionsV1
        {
            public uint structSize;
            public uint version;
            public uint subpixelMode;
            public uint linearColorSpace;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SubpixelStatusV1
        {
            public uint structSize;
            public uint version;
            public uint valid;
            public uint requestedMode;
            public uint effectiveMode;
            public uint cellPatternMode;
            public uint cellCount;
            public uint nativeWidth;
            public uint nativeHeight;
            public uint backend;
            public uint renderCompleted;
            public uint presentAccepted;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WorkerDrawOptionsV1
        {
            public uint structSize;
            public uint version;
            public uint sourceEncoding;
            public uint reserved0;
        }

        internal const uint WorkerOutputOptionsVersion = 1;
        internal const uint SubpixelStatusVersion = 1;
        internal const uint WorkerDrawOptionsVersion = 1;
        internal const uint WorkerSourceEncodingUnorm = 0;
        internal const uint WorkerSourceEncodingSrgb = 1;
        internal const uint SubpixelBackendNativeD3D11 = 1;
        internal const uint InvalidSubpixelMode = uint.MaxValue;

        internal static bool HasStableSubpixelAbiLayout()
        {
            return Marshal.SizeOf(typeof(WorkerOutputOptionsV1)) == 16 &&
                   Marshal.OffsetOf(typeof(WorkerOutputOptionsV1), nameof(WorkerOutputOptionsV1.structSize)).ToInt32() == 0 &&
                   Marshal.OffsetOf(typeof(WorkerOutputOptionsV1), nameof(WorkerOutputOptionsV1.version)).ToInt32() == 4 &&
                   Marshal.OffsetOf(typeof(WorkerOutputOptionsV1), nameof(WorkerOutputOptionsV1.subpixelMode)).ToInt32() == 8 &&
                   Marshal.OffsetOf(typeof(WorkerOutputOptionsV1), nameof(WorkerOutputOptionsV1.linearColorSpace)).ToInt32() == 12 &&
                   Marshal.SizeOf(typeof(WorkerDrawOptionsV1)) == 16 &&
                   Marshal.OffsetOf(typeof(WorkerDrawOptionsV1), nameof(WorkerDrawOptionsV1.sourceEncoding)).ToInt32() == 8 &&
                   Marshal.OffsetOf(typeof(WorkerDrawOptionsV1), nameof(WorkerDrawOptionsV1.reserved0)).ToInt32() == 12 &&
                   Marshal.SizeOf(typeof(SubpixelStatusV1)) == 48 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.structSize)).ToInt32() == 0 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.version)).ToInt32() == 4 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.valid)).ToInt32() == 8 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.requestedMode)).ToInt32() == 12 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.effectiveMode)).ToInt32() == 16 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.cellPatternMode)).ToInt32() == 20 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.cellCount)).ToInt32() == 24 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.nativeWidth)).ToInt32() == 28 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.nativeHeight)).ToInt32() == 32 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.backend)).ToInt32() == 36 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.renderCompleted)).ToInt32() == 40 &&
                   Marshal.OffsetOf(typeof(SubpixelStatusV1), nameof(SubpixelStatusV1.presentAccepted)).ToInt32() == 44;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_begin_with_output_v1(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string runtimePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationName,
            uint displayOrdinal, IntPtr producerDevice, ref WorkerOutputOptionsV1 options);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_acquire_preview_lease_with_output_v1(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string runtimePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationName,
            uint displayOrdinal, IntPtr producerDevice, ref WorkerOutputOptionsV1 options,
            out IntPtr leaseHandle, ref PreviewLeaseIdentityV1 identity);
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
        internal static extern int lkg_worker_get_subpixel_status_v1(IntPtr handle, ref SubpixelStatusV1 status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_close(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_reset_transfer(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_release(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_try_make_preview_draw_data_with_options_v1(IntPtr leaseHandle,
            ref PreviewLeaseIdentityV1 identity, IntPtr texture, uint columns, uint rows, float aspect, float zoom,
            ref WorkerDrawOptionsV1 options, out IntPtr drawData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_cancel_draw_data(IntPtr drawData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_make_draw_data_with_options_v1(IntPtr handle, IntPtr texture,
            uint columns, uint rows, float aspect, float zoom, ref WorkerDrawOptionsV1 options);
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
