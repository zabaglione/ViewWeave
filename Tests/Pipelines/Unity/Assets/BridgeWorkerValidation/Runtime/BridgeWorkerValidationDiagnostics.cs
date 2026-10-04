using System;
using System.Runtime.InteropServices;
using ViewWeave;

namespace ViewWeave.BridgeWorkerValidation
{
    public static class BridgeWorkerValidationDiagnostics
    {
        const string NativeLibrary = "lookingglass_bridge_shim";

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lkg_worker_pending_count")]
        static extern uint ReadNativePendingCount();

        public static bool TryReadPendingCounts(out int shutdownPending, out uint nativePending, out string error)
        {
            shutdownPending = -1;
            nativePending = 0;
            error = string.Empty;
            try
            {
                shutdownPending = BridgeWorkerQuiltOutput.ShutdownPendingCount;
                nativePending = ReadNativePendingCount();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }
    }
}
