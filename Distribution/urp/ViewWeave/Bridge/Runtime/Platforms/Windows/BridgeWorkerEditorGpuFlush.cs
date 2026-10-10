using System;
using UnityEngine;

namespace ViewWeave
{
    internal static class BridgeWorkerEditorGpuFlush
    {
        static bool synchronousWaitActive;
        static int flushAttempts;
        static int successfulFlushes;

        internal static bool SynchronousWaitActive => synchronousWaitActive;
        internal static int FlushAttempts => flushAttempts;
        internal static int SuccessfulFlushes => successfulFlushes;

        internal static void BeginSynchronousWait()
        {
            synchronousWaitActive = true;
        }

        internal static void EndSynchronousWait()
        {
            synchronousWaitActive = false;
        }

        internal static bool SubmitIfNeeded(ref bool attempted)
        {
            return SubmitIfNeeded(ref attempted, null);
        }

        internal static bool SubmitIfNeeded(ref bool attempted, Action flushAction)
        {
            if (!synchronousWaitActive || attempted) return false;
            attempted = true;
            flushAttempts++;
            if (flushAction == null) GL.Flush();
            else flushAction();
            successfulFlushes++;
            return true;
        }
    }
}