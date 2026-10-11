using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ViewWeave.Editor
{
    /// <summary>Acquires authoring geometry without creating an output or preview.</summary>
    [InitializeOnLoad]
    public static class BridgeEditorDisplayDiscovery
    {
        sealed class Request
        {
            internal ViewWeaveRig rig;
            internal MonoBehaviour output;
            internal string runtime, application, status = "Waiting for Bridge metadata";
            internal uint ordinal;
            internal bool attempted;
            internal BridgeDisplaySnapshot snapshot;
            internal BridgeWorkerQuiltOutput liveOutput;
            internal Action<long, BridgeDisplaySnapshot, string> liveCompleted;
            internal bool Matches(QuiltRenderer renderer) => rig != null && renderer != null &&
                BridgeDisplayGeometryStore.TryConfiguration(renderer, out MonoBehaviour current, out string path, out string app, out uint index) &&
                current == output && (path ?? "") == runtime && (app ?? "") == application && index == ordinal;
        }
        static readonly Dictionary<ViewWeaveRig, Request> requests = new Dictionary<ViewWeaveRig, Request>();
        static bool scanQueued, pumpRegistered;
        internal static bool AutomaticRequestsEnabled = true;

        static BridgeEditorDisplayDiscovery()
        {
            QueueScan();
            EditorApplication.hierarchyChanged += QueueScan;
            EditorSceneManager.sceneOpened += (_, __) => QueueScan();
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) Cancel();
                else if (state == PlayModeStateChange.EnteredEditMode) QueueScan();
            };
        }

        static void QueueScan()
        {
            if (scanQueued) return;
            scanQueued = true;
            EditorApplication.delayCall += () =>
            {
                scanQueued = false;
                foreach (ViewWeaveRig rig in Resources.FindObjectsOfTypeAll<ViewWeaveRig>())
                    EnsureRequested(rig.GetComponent<QuiltRenderer>());
            };
        }

        internal static void EnsureRequested(QuiltRenderer renderer)
        {
            if (!AutomaticRequestsEnabled || EditorApplication.isPlayingOrWillChangePlaymode || renderer == null ||
                !renderer.gameObject.scene.isLoaded || EditorUtility.IsPersistent(renderer) ||
                ((renderer.hideFlags | renderer.gameObject.hideFlags) & HideFlags.DontSave) != 0 ||
                EditorSceneManager.IsPreviewSceneObject(renderer) || PrefabStageUtility.GetPrefabStage(renderer.gameObject) != null) return;
            ViewWeaveRig rig = renderer.GetComponent<ViewWeaveRig>();
            if (rig == null || !BridgeDisplayGeometryStore.TryConfiguration(renderer, out MonoBehaviour output, out string path, out string app, out uint ordinal)) return;
            bool supported = BridgeDisplayDiscoveryNative.IsTestAdapter || (Application.platform == RuntimePlatform.OSXEditor ? output is BridgeMetalQuiltOutput :
                Application.platform == RuntimePlatform.WindowsEditor && output is BridgeWorkerQuiltOutput);
            if (!supported) return;
            if (requests.TryGetValue(rig, out Request previous) && previous.Matches(renderer)) return;
            if (previous != null) { BridgeDisplayDiscovery.Cancel(previous.output); Detach(previous); }
            requests[rig] = new Request { rig = rig, output = output, runtime = path ?? "", application = app ?? "", ordinal = ordinal };
            if (!pumpRegistered) { pumpRegistered = true; EditorApplication.update += Pump; }
        }

        static void Pump()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                BridgeDisplayDiscovery.IsBusy || BridgeMetalEditorPreview.IsActive || BridgeMetalEditorPreview.IsStopping ||
                BridgeEditorPreview.IsActive || BridgeEditorPreview.IsStopping) return;
            var dead = new List<ViewWeaveRig>();
            foreach (var pair in requests) if (pair.Key == null) dead.Add(pair.Key);
            foreach (var rig in dead) requests.Remove(rig);
            foreach (Request request in requests.Values)
            {
                if (request.attempted) continue;
                request.attempted = true;
                if (!request.Matches(request.rig == null ? null : request.rig.Renderer)) continue;
                TryRefresh(request.rig, out _);
                return;
            }
            EditorApplication.update -= Pump; pumpRegistered = false;
        }

        internal static void PumpForTests() => Pump();

        public static bool TryRefresh(ViewWeaveRig rig, out string error)
        {
            error = string.Empty;
            QuiltRenderer renderer = rig == null ? null : rig.GetComponent<QuiltRenderer>();
            if (renderer != null && (!renderer.gameObject.scene.isLoaded || EditorUtility.IsPersistent(renderer) ||
                ((renderer.hideFlags | renderer.gameObject.hideFlags) & HideFlags.DontSave) != 0 ||
                EditorSceneManager.IsPreviewSceneObject(renderer) || PrefabStageUtility.GetPrefabStage(renderer.gameObject) != null))
            { error = "Bridge discovery requires a loaded scene Rig outside Prefab Mode."; return false; }
            if (renderer == null || !BridgeDisplayGeometryStore.TryConfiguration(renderer, out MonoBehaviour output,
                    out string path, out string application, out uint ordinal))
            { error = "A configured Bridge Rig is required."; return false; }
            if (BridgeDisplayDiscovery.IsBusy) { error = BridgeDisplayDiscovery.Status; return false; }
            var configuredSession = output.GetComponent<ViewWeaveBridgeSession>();
            if (output is BridgeWorkerQuiltOutput && configuredSession != null &&
                configuredSession.State != ViewWeaveBridgeSession.SessionState.Idle && configuredSession.State != ViewWeaveBridgeSession.SessionState.Rendering)
            { error = "Wait for the Bridge Session to finish connecting, stopping or recovering."; return false; }
            var request = new Request { rig = rig, output = output, runtime = path ?? "", application = application ?? "", ordinal = ordinal, attempted = true };
            if (requests.TryGetValue(rig, out Request prior)) Detach(prior);
            requests[rig] = request;
            if (output is BridgeWorkerQuiltOutput worker &&
                (worker.State != BridgeWorkerQuiltOutput.WorkerState.Closed || BridgeEditorPreview.IsActive))
            {
                bool accepted = BridgeEditorPreview.TryRequestDisplayRefresh(worker, out BridgeWorkerQuiltOutput actual, out error);
                request.status = accepted ? "Reading Bridge device geometry" : error;
                if (accepted)
                {
                    request.liveOutput = actual;
                    long token = actual.LatestDisplayRefreshToken;
                    request.liveCompleted = (completedToken, snapshot, failure) =>
                    {
                        if (completedToken != token) return;
                        Detach(request);
                        if (!request.Matches(renderer) || !requests.TryGetValue(rig, out Request current) || current != request) return;
                        if (!string.IsNullOrEmpty(failure) || actual.RequiresReconnect)
                        { request.status = ConnectionHelp(string.IsNullOrEmpty(failure) ? "Stop and reconnect output before applying changed calibration." : failure); return; }
                        request.snapshot = snapshot;
                        if (snapshot == null || !snapshot.TryGetDisplay(ordinal, out BridgeDisplayProfile profile))
                        { request.status = ConnectionHelp("Selected display was not found."); return; }
                        BridgeDisplayGeometryStore.Remember(rig, output, path, application, ordinal, profile);
                        request.status = "Bridge device geometry acquired";
                    };
                    actual.ConnectedDisplayRefreshCompleted += request.liveCompleted;
                }
                return accepted;
            }
            var metal = output as BridgeMetalQuiltOutput;
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                (metal != null && (metal.IsRunning || metal.IsClosing)) || BridgeMetalQuiltOutput.PendingRetirementCount != 0 ||
                BridgeMetalEditorPreview.IsActive || BridgeMetalEditorPreview.IsStopping)
            { error = "Close output and device preview before requesting idle Bridge metadata."; request.status = error; return false; }
            string resolvedPath = path;
            if (!BridgeDisplayDiscoveryNative.IsTestAdapter && !BridgeRuntimeLocator.TryResolve(path, out resolvedPath, out error))
            { request.status = ConnectionHelp(error); return false; }
            Action<BridgeDisplaySnapshot> completed = snapshot =>
                {
                    if (!request.Matches(renderer) || !requests.TryGetValue(rig, out Request current) || current != request) return;
                    request.snapshot = snapshot;
                    if (!snapshot.TryGetDisplay(ordinal, out BridgeDisplayProfile profile))
                    { request.status = "Connect a Looking Glass display, enable Desktop Mode in Bridge, then press Refresh Connected Displays. The last successful geometry is retained."; return; }
                    BridgeDisplayGeometryStore.Remember(rig, output, path, application, ordinal, profile);
                    request.status = "Bridge device geometry acquired without output or preview";
                };
            Action<string> failed = message => request.status = ConnectionHelp(message);
            bool started = BridgeDisplayDiscoveryNative.IsTestAdapter
                ? BridgeDisplayDiscovery.TryRequestForTests(resolvedPath, application, ordinal, output, completed, failed, out error)
                : BridgeDisplayDiscovery.TryRequest(resolvedPath, application, ordinal, output, completed, failed, out error);
            if (!started) request.status = ConnectionHelp(error);
            return started;
        }

        public static string GetStatus(ViewWeaveRig rig)
        {
            if (rig == null || !requests.TryGetValue(rig, out Request request) || !request.Matches(rig.Renderer)) return "Waiting for initial Bridge device discovery";
            return BridgeDisplayDiscovery.IsBusy ? BridgeDisplayDiscovery.Status : request.status;
        }

        internal static bool HasCurrentRequest(ViewWeaveRig rig) => rig != null &&
            requests.TryGetValue(rig, out Request request) && request.Matches(rig.Renderer);

        internal static BridgeDisplaySnapshot GetSnapshot(ViewWeaveRig rig)
        {
            if (rig == null || !requests.TryGetValue(rig, out Request request) || !request.Matches(rig.Renderer)) return null;
            return request.snapshot ?? (request.output as BridgeWorkerQuiltOutput)?.CurrentConfigurationDisplaySnapshot;
        }

        static string ConnectionHelp(string detail) => "Connect a Looking Glass display and check Bridge Desktop Mode, then press Refresh Connected Displays. " + detail;

        internal static void Cancel()
        {
            foreach (Request request in requests.Values)
            { BridgeDisplayDiscovery.Cancel(request.liveOutput != null ? request.liveOutput : request.output); Detach(request); }
            requests.Clear();
            EditorApplication.update -= Pump; pumpRegistered = false;
        }

        static void Detach(Request request)
        {
            if (request.liveOutput != null && request.liveCompleted != null)
                request.liveOutput.ConnectedDisplayRefreshCompleted -= request.liveCompleted;
            request.liveCompleted = null;
        }
    }
}
