using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    internal static class BridgeNative
    {
        const string Library = "lookingglass_bridge_shim";

        [StructLayout(LayoutKind.Sequential)] internal struct DisplayInfo
        {
            public uint index, width, height;
            public float aspect, viewcone;
            public int quiltWidth, quiltHeight, quiltColumns, quiltRows;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)] internal struct SessionCloseStatusV1
        {
            public uint structSize;
            public uint version;
            public ulong sessionId;
            public uint state;
            public int lastResult;
            public uint reserved0;
            public uint reserved1;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void lkg_bridge_unload();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_initialize([MarshalAs(UnmanagedType.LPUTF8Str)] string app);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void lkg_bridge_uninitialize();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_is_idle();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_last_error(IntPtr utf8, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint lkg_bridge_successful_draw_count();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_is_initialized();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_version(IntPtr utf8, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_get_display_count(out uint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_get_display(uint ordinal, out DisplayInfo info);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong lkg_bridge_create_d3d11_session(IntPtr device, uint display);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_destroy_session(ulong session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_get_session_close_status_v1(ulong session, ref SessionCloseStatusV1 status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_ack_session_close_v1(ulong session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr lkg_bridge_render_event_func();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr lkg_bridge_make_draw_data(ulong session, IntPtr texture, uint columns, uint rows, float aspect, float zoom);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr lkg_bridge_make_close_data(ulong session);

        internal static string Version()
        {
            int length = lkg_bridge_version(IntPtr.Zero, 0);
            if (length <= 0) return string.Empty;
            IntPtr ptr = Marshal.AllocHGlobal(length + 1);
            try { lkg_bridge_version(ptr, (uint)(length + 1)); return Marshal.PtrToStringAnsi(ptr) ?? string.Empty; }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        internal static int QueueDraw(ulong session, RenderTexture texture, QuiltLayout layout, float aspect, float zoom)
        {
            if (session == 0 || texture == null || !texture.IsCreated()) return -1;
            IntPtr data = lkg_bridge_make_draw_data(session, texture.GetNativeTexturePtr(), (uint)layout.Columns, (uint)layout.Rows, aspect, zoom);
            if (data == IntPtr.Zero) return -2;
            var command = new CommandBuffer { name = "ViewWeave Bridge Draw" };
            command.IssuePluginEventAndData(lkg_bridge_render_event_func(), 0, data);
            Graphics.ExecuteCommandBuffer(command);
            command.Release();
            return 0;
        }

        internal static CommandBuffer QueueClose(ulong session, out bool submitted)
        {
            submitted = false;
            IntPtr data = lkg_bridge_make_close_data(session);
            if (data == IntPtr.Zero) return null;
            var command = new CommandBuffer { name = "ViewWeave Bridge Close" };
            try
            {
                command.IssuePluginEventAndData(lkg_bridge_render_event_func(), 1, data);
                Graphics.ExecuteCommandBuffer(command);
                submitted = true;
            }
            catch
            {
                // Execution may have partially submitted the close event; keep the command and
                // session resources until the native close ledger resolves the outcome.
            }
            return command;
        }
    }
}
