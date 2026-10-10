using System;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    public static class BridgeMetalQuiltPlayback
    {
        static BridgeMetalQuiltOutput output;
        static QuiltPlaybackSource source;
        static QuiltPlaybackCursor cursor;
        static QuiltEditorResourceRecord resources;
        static RenderTexture texture;
        static object token;
        static bool paused, stopping, restore;
        static int pendingFrame = -1;
        static double lastTime;
        static QuiltQualityProfile profile;
        static Camera sourceCamera, referenceCamera;
        static QuiltPlaybackRigSnapshot rigSnapshot;
        public static bool IsActive => token != null;
        public static bool IsPaused => IsActive && paused;
        public static bool IsStopping => stopping;
        public static string Status { get; private set; } = "Idle";
        public static string LastError { get; private set; } = string.Empty;
        public static int CurrentFrame { get; private set; } = -1;
        public static int SkippedFrames { get; private set; }
        public static int PendingResourceCount => QuiltEditorResourceRecord.PendingCount;

        static BridgeMetalQuiltPlayback()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += state =>
            { if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode) Invalidate(); };
            AssemblyReloadEvents.beforeAssemblyReload += Invalidate;
            EditorApplication.quitting += Invalidate;
        }

        public static bool TryStart(BridgeMetalQuiltOutput target, QuiltPlaybackSource playback, out string error)
        {
            error = string.Empty;
            if (!Application.isPlaying || Application.platform != RuntimePlatform.OSXEditor || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || !SystemInfo.supportsAsyncGPUReadback)
                return Fail("Metal Quilt Playback requires stable macOS Editor Play mode and asynchronous GPU retirement.", out error);
            if (IsActive || resources != null || QuiltEditorResourceRecord.PendingCount != 0 || BridgeQuiltPlaybackLease.Active != null)
                return Fail("Stop existing playback or preview and wait for GPU retirement.", out error);
            if (target == null || !target.IsRunning || target.rig == null || playback == null)
                return Fail("Select a running Metal output and a validated quilt PNG or completed PNG sequence.", out error);
            if (!QuiltPlaybackCompatibility.SourceMatchesRenderer(playback.Info, target.rig.Renderer, out error)) return Fail(error, out error);
            output = target;
            source = playback;
            token = new object();
            try
            {
                resources = QuiltEditorResourceRecord.Create();
                QuiltLayout layout = source.Info.Layout;
                texture = new RenderTexture(layout.QuiltWidth, layout.QuiltHeight, 0, RenderTextureFormat.ARGB32)
                { name = "ViewWeave Metal Playback Quilt", hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset, filterMode = FilterMode.Point };
                if (!resources.TrackTexture(texture) || !texture.Create()) throw new InvalidOperationException("Playback texture could not be created.");
                if (!output.TryBeginPlayback(token, out error)) throw new InvalidOperationException(error);
                output.PlaybackStopping += Invalidate;
                profile = target.rig.Profile;
                sourceCamera = target.rig.Renderer.sourceCamera;
                referenceCamera = target.rig.Renderer.referenceCamera;
                rigSnapshot = new QuiltPlaybackRigSnapshot(target.rig.Renderer);
                cursor = new QuiltPlaybackCursor(source.Info.FramesPerSecond, source.Info.FrameCount);
                lastTime = EditorApplication.timeSinceStartup;
                paused = stopping = false;
                restore = true;
                CurrentFrame = -1;
                pendingFrame = -1;
                SkippedFrames = 0;
                Status = "Playing";
                LastError = string.Empty;
                return true;
            }
            catch (Exception exception) { Invalidate(); return Fail(exception.Message, out error); }
        }

        static bool Fail(string reason, out string error)
        {
            error = Sanitize(reason);
            if (!IsActive) { LastError = error; Status = "Playback error"; }
            return false;
        }

        public static void TogglePause()
        {
            if (!IsActive || stopping) return;
            paused = !paused;
            if (paused) cursor.Pause(); else cursor.Resume();
            lastTime = EditorApplication.timeSinceStartup;
            Status = paused ? "Paused; the last displayed quilt is held" : "Playing";
        }

        public static void Stop()
        {
            if (!IsActive || stopping) return;
            stopping = true;
            Status = "Stopping playback; waiting for GPU retirement";
            resources?.Stop();
        }

        static bool IsCurrent() => output != null && output.IsPlaybackCurrent(token) && output.rig != null &&
            output.rig.Renderer != null && output.rig.ValidateRig(out _) && !output.rig.AutoRender && !output.rig.Renderer.autoRender &&
            output.rig.Profile.Equals(profile) && output.rig.Renderer.sourceCamera == sourceCamera &&
            output.rig.Renderer.referenceCamera == referenceCamera &&
            rigSnapshot != null && rigSnapshot.Matches(output.rig.Renderer) &&
            QuiltPlaybackCompatibility.SourceMatchesRenderer(source.Info, output.rig.Renderer, out _);

        static void Invalidate()
        {
            // Output Stop must synchronously release the Core lease before restoring its Rig snapshot.
            restore = false;
            resources?.Stop();
            Finish(false);
        }

        static void Finish(bool resume)
        {
            if (output != null)
            {
                output.PlaybackStopping -= Invalidate;
                output.EndPlayback(token, resume);
            }
            output = null;
            source = null;
            cursor = null;
            texture = null;
            token = null;
            pendingFrame = -1;
            paused = stopping = false;
            Status = resources != null ? "Stopping playback; GPU resources retained" : string.IsNullOrEmpty(LastError) ? "Idle" : "Stopped with an error";
        }

        static void Update()
        {
            if (!IsActive)
            {
                if (resources == null && Status.StartsWith("Stopping", StringComparison.Ordinal)) Status = string.IsNullOrEmpty(LastError) ? "Idle" : "Stopped with an error";
                else if (resources != null && !string.IsNullOrEmpty(resources.Failure)) LastError = resources.Failure;
                return;
            }
            if (!Application.isPlaying || EditorApplication.isCompiling || !IsCurrent()) { Invalidate(); return; }
            if (stopping)
            {
                if (resources == null) Finish(restore && IsCurrent());
                else if (!string.IsNullOrEmpty(resources.Failure)) { LastError = resources.Failure; Invalidate(); }
                return;
            }
            if (paused) return;
            try
            {
                double now = EditorApplication.timeSinceStartup;
                QuiltPlaybackStep step = cursor.Advance(Math.Max(0d, now - lastTime));
                lastTime = now;
                if (step.HasFrame)
                {
                    Texture2D decoded = source.LoadFrame(step.FrameIndex);
                    RenderTexture previous = RenderTexture.active;
                    try
                    {
                        resources.MarkRendered();
                        Graphics.Blit(decoded, texture);
                        // Keep the newest decoded frame for retry while the native presentation slot is busy.
                        SkippedFrames += step.SkippedFrameCount + (pendingFrame >= 0 ? 1 : 0);
                        pendingFrame = step.FrameIndex;
                    }
                    finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(decoded); }
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                if (pendingFrame >= 0)
                {
                    if (output.TrySubmitPlayback(token, texture, source.Info.Layout))
                    {
                        CurrentFrame = pendingFrame;
                        pendingFrame = -1;
                        Status = "Playing frame " + (CurrentFrame + 1) + " of " + source.Info.FrameCount + "; skipped " + SkippedFrames;
                    }
                    else if (!string.IsNullOrEmpty(output.LastError)) throw new InvalidOperationException(output.LastError);
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                if (step.IsEndOfStream && source.Info.IsSequence && pendingFrame < 0) Stop();
            }
            catch (Exception exception) { LastError = Sanitize(exception.Message); Invalidate(); }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Unknown Metal playback error";
            var result = new System.Text.StringBuilder();
            foreach (char character in value) if (character >= 32 && character < 127) result.Append(character);
            return result.ToString();
        }
    }
}
