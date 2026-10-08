using System;
using System.Runtime.InteropServices;

namespace ViewWeave
{
    internal interface IBridgeDisplayDiscoveryNativeAdapter
    {
        IntPtr BeginMetadata(string runtimePath, string applicationName);
        int RequestDisplayRefresh(IntPtr handle, out ulong requestedGeneration);
        int GetDisplaySnapshot(IntPtr handle, ref BridgeDisplayDiscoveryNative.SnapshotStatusV1 status,
            BridgeDisplayDiscoveryNative.SnapshotItemV1[] items, uint capacity, out uint requiredCount);
        int Close(IntPtr handle);
        int EndMetadata(IntPtr handle);
    }

    internal static class BridgeDisplayDiscoveryNative
    {
        const string Library = "lookingglass_bridge_shim";
        internal const int MaximumDisplays = 256;
        static readonly IBridgeDisplayDiscoveryNativeAdapter defaultAdapter = new PInvokeAdapter();
        internal static IBridgeDisplayDiscoveryNativeAdapter Adapter { get; private set; } = defaultAdapter;
        internal static bool IsTestAdapter { get; private set; }

        internal static void InstallTestAdapter(IBridgeDisplayDiscoveryNativeAdapter adapter)
        {
            Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            IsTestAdapter = true;
        }

        internal static void ResetAdapter()
        {
            Adapter = defaultAdapter;
            IsTestAdapter = false;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayInfo
        {
            public uint index;
            public uint width;
            public uint height;
            public float aspect;
            public float viewcone;
            public int quiltWidth;
            public int quiltHeight;
            public int quiltColumns;
            public int quiltRows;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct SnapshotStatusV1
        {
            public uint structSize;
            public uint version;
            public uint state;
            public uint itemCount;
            public ulong snapshotGeneration;
            public ulong requestedGeneration;
            public uint pending;
            public uint cleanupFinished;
            public int result;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string error;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SnapshotItemV1
        {
            public uint structSize;
            public uint version;
            public uint displayOrdinal;
            public uint reserved0;
            public DisplayInfo profile;
            public PhysicalDisplayInfoV1 physical;
        }

        sealed class PInvokeAdapter : IBridgeDisplayDiscoveryNativeAdapter
        {
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lkg_worker_begin_metadata_v1")]
            static extern IntPtr BeginMetadataNative([MarshalAs(UnmanagedType.LPUTF8Str)] string runtimePath,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationName);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lkg_worker_request_display_refresh_v1")]
            static extern int RequestDisplayRefreshNative(IntPtr handle, out ulong requestedGeneration);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lkg_worker_get_display_snapshot_v1")]
            static extern int GetDisplaySnapshotNative(IntPtr handle, ref SnapshotStatusV1 status,
                [In, Out] SnapshotItemV1[] items, uint capacity, out uint requiredCount);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lkg_worker_end_metadata_v1")]
            static extern int EndMetadataNative(IntPtr handle);

            public IntPtr BeginMetadata(string runtimePath, string applicationName) => BeginMetadataNative(runtimePath, applicationName);
            public int RequestDisplayRefresh(IntPtr handle, out ulong requestedGeneration) => RequestDisplayRefreshNative(handle, out requestedGeneration);
            public int GetDisplaySnapshot(IntPtr handle, ref SnapshotStatusV1 status, SnapshotItemV1[] items,
                uint capacity, out uint requiredCount) => GetDisplaySnapshotNative(handle, ref status, items, capacity, out requiredCount);
            public int Close(IntPtr handle) => BridgeWorkerNative.lkg_worker_close(handle);
            public int EndMetadata(IntPtr handle) => EndMetadataNative(handle);
        }
    }
}
