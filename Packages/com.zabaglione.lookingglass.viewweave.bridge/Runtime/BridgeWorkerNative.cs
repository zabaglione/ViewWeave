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

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_begin([MarshalAs(UnmanagedType.LPUTF8Str)] string runtimePath, [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationName, uint displayOrdinal, IntPtr producerDevice);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_poll(IntPtr handle, out Status status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_close(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_reset_transfer(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_release(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_make_draw_data(IntPtr handle, IntPtr texture, uint columns, uint rows, float aspect, float zoom);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int lkg_worker_cancel_draw_data(IntPtr drawData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr lkg_worker_render_event_func();
    }
}
