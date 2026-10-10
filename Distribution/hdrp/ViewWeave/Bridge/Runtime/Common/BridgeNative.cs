using System;
using System.Runtime.InteropServices;

namespace ViewWeave
{
    internal static partial class BridgeNative
    {
        const string Library = "lookingglass_bridge_shim";

        [StructLayout(LayoutKind.Sequential)] internal struct DisplayInfo
        {
            public uint index, width, height;
            public float aspect, viewcone;
            public int quiltWidth, quiltHeight, quiltColumns, quiltRows;
        }


        [StructLayout(LayoutKind.Sequential)] internal struct PhysicalDisplayInfoV1
        {
            public uint structSize, version, pixelWidth, pixelHeight;
            public float dpi;
            public uint valid;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void lkg_bridge_unload();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_initialize([MarshalAs(UnmanagedType.LPUTF8Str)] string app);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void lkg_bridge_uninitialize();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_is_idle();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_last_error(IntPtr utf8, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_is_initialized();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_version(IntPtr utf8, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_get_display_count(out uint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_get_display(uint ordinal, out DisplayInfo info);

        internal static string Version()
        {
            int length = lkg_bridge_version(IntPtr.Zero, 0);
            if (length <= 0) return string.Empty;
            IntPtr ptr = Marshal.AllocHGlobal(length + 1);
            try { lkg_bridge_version(ptr, (uint)(length + 1)); return Marshal.PtrToStringAnsi(ptr) ?? string.Empty; }
            finally { Marshal.FreeHGlobal(ptr); }
        }

    }
}
