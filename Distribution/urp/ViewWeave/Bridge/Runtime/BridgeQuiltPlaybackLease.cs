#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using ViewWeave;

namespace ViewWeave
{
    [DisallowMultipleComponent]
    public sealed class BridgeQuiltPlaybackLease : MonoBehaviour
    {
        const double PlaybackOwnershipTimeoutSeconds = 10d;

        public enum LeaseState { WaitingForDrain, Playing, Paused, Stopping, Retained, Stopped }

        static BridgeQuiltPlaybackLease active;
        static readonly List<BridgeQuiltPlaybackLease> knownLeases = new List<BridgeQuiltPlaybackLease>();
        ViewWeaveBridgeSession session;
        BridgeWorkerQuiltOutput output;
        ViewWeaveRig rig;
        QuiltRenderer renderer;
        QuiltPlaybackSource source;
        QuiltPlaybackCursor cursor;
        QuiltRendererOperationLease rendererLease;
        BridgePlaybackRecoveryRecord recoveryRecord;
        RenderTexture playbackTexture;
        object sessionToken;
        bool sessionAcquired;
        GraphicsFence tailFence;
        BridgeGpuProofMode gpuProofMode;
        bool hasTailFence;
        object tailReadbackIdentity;
        BridgeEditorPreviewRetention.ReadbackCompletionLatch tailReadbackCompletion;
        AsyncGPUReadbackRequest tailReadback;
        bool transitionProofRequired;
        bool readbackCompleted;
        bool readbackValid;
        bool transitionReadbackIssued;
        bool transitionFlushAttempted;
        bool transitionFlushFailed;
        bool finalQuitPending;
        AsyncGPUReadbackRequest transitionReadback;
        bool resetCompleted;
        bool invalidated;
        bool restoreAutoRender;
        bool cleanupStarted;
        bool handoffSeen;
        double stateDeadline;
        long sessionGeneration;
        double lastRealtime;
        int currentFrame = -1;
        int skippedFrames;
        string statusMessage = "";
        string errorMessage = "";
        QuiltSettingsSnapshot initialSettings;
        QuiltQualityProfile initialProfile;
        QuiltViewRenderMode initialRenderMode;
        string initialRuntimePath;
        string initialApplicationName;
        uint initialDisplayOrdinal;
        QuiltQualityPreset initialQuality;
        MonoBehaviour initialOutputBehaviour;
        Camera initialSourceCamera;
        Camera initialReferenceCamera;
        float initialSourceNearClip;
        float initialSourceFarClip;
        float initialReferenceNearClip;
        float initialReferenceFarClip;
        bool initialFollowReferenceFieldOfView;

        public static BridgeQuiltPlaybackLease Active => active != null && active.State != LeaseState.Stopped ? active : null;
        public LeaseState State { get; private set; }
        public string StatusMessage => statusMessage;
        public string ErrorMessage => errorMessage;
        public QuiltPlaybackInfo Info => source == null ? null : source.Info;
        public int CurrentFrame => currentFrame;
        public int SkippedFrames => skippedFrames;
        public bool IsBusy => State != LeaseState.Stopped && State != LeaseState.Retained;

        public static bool TryStart(ViewWeaveBridgeSession target, QuiltPlaybackSource playbackSource, out string error)
        {
            error = string.Empty;
            if (!BridgeGraphicsBackendPolicy.TrySelect(
                BridgeGraphicsBackendRoute.PlaybackWindowsEditorPlay,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                SystemInfo.graphicsDeviceType,
                SystemInfo.renderingThreadingMode,
                out _,
                out error)) return false;
            if (active != null && active.State != LeaseState.Stopped)
            {
                error = active.State == LeaseState.Retained
                    ? "A previous playback texture is retained because GPU completion was not proven; restart the Unity Editor before retrying."
                    : "A quilt playback lease is already active.";
                return false;
            }
            if (HasUnprovenRecoveryRecord())
            {
                error = "A previous playback texture is retained because GPU completion was not proven; restart the Unity Editor before retrying.";
                return false;
            }
            if (target == null || playbackSource == null)
            {
                error = "Select a Bridge session and a validated quilt source.";
                return false;
            }
            BridgeGpuProofMode proofMode = BridgeGraphicsBackendPolicy.SelectCurrentGpuProofMode();
            if (proofMode == BridgeGpuProofMode.Unsupported)
            {
                error = "Quilt playback has no supported GPU ownership proof mode for safe Editor cleanup.";
                return false;
            }
            ViewWeaveRig targetRig = target.GetComponent<ViewWeaveRig>();
            QuiltRenderer targetRenderer = targetRig == null ? null : targetRig.Renderer;
            if (targetRenderer == null || !targetRig.ValidateRig(out error)) return false;
            if (!SourceMatchesRenderer(playbackSource.Info, targetRenderer, out error)) return false;
            if (!QuiltRendererOperationLease.TryAcquire(targetRenderer, QuiltRendererOperationKind.Playback, out QuiltRendererOperationLease coreLease, out error)) return false;

            GameObject host = null;
            BridgeQuiltPlaybackLease lease = null;
            try
            {
                host = new GameObject("ViewWeave Quilt Playback Lease") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host);
                lease = host.AddComponent<BridgeQuiltPlaybackLease>();
            }
            catch (Exception exception)
            {
                if (host != null) DestroyImmediate(host);
                coreLease.Dispose();
                error = Sanitize(exception.Message);
                return false;
            }
            lease.session = target;
            lease.output = target.GetComponent<BridgeWorkerQuiltOutput>();
            lease.rig = targetRig;
            lease.renderer = targetRenderer;
            lease.source = playbackSource;
            lease.rendererLease = coreLease;
            lease.initialSettings = new QuiltSettingsSnapshot(targetRenderer.settings);
            lease.initialOutputBehaviour = targetRenderer.OutputBehaviour;
            lease.initialSourceCamera = targetRenderer.sourceCamera;
            lease.initialReferenceCamera = targetRenderer.referenceCamera;
            lease.initialSourceNearClip = targetRenderer.sourceCamera.nearClipPlane;
            lease.initialSourceFarClip = targetRenderer.sourceCamera.farClipPlane;
            lease.initialReferenceNearClip = targetRenderer.referenceCamera == null ? 0f : targetRenderer.referenceCamera.nearClipPlane;
            lease.initialReferenceFarClip = targetRenderer.referenceCamera == null ? 0f : targetRenderer.referenceCamera.farClipPlane;
            lease.initialFollowReferenceFieldOfView = targetRenderer.followReferenceFieldOfView;
            lease.initialProfile = targetRig.Profile;
            lease.initialRenderMode = targetRenderer.viewRenderMode;
            lease.initialRuntimePath = target.runtimePath;
            lease.initialApplicationName = target.applicationName;
            lease.initialDisplayOrdinal = target.displayOrdinal;
            lease.initialQuality = target.Quality;
            lease.gpuProofMode = proofMode;
            lease.restoreAutoRender = true;
            lease.State = LeaseState.WaitingForDrain;
            lease.statusMessage = "Waiting for the Bridge queue and transfer texture to drain.";
            active = lease;
            knownLeases.Add(lease);

            if (!target.TryAcquirePlayback(lease, lease.OnSessionInvalidated, out lease.sessionGeneration, out error))
            {
                lease.FailBeforePlayback(error);
                return false;
            }
            lease.sessionAcquired = true;
            lease.sessionToken = lease;
            lease.output.PlaybackReaperHandoff += lease.OnReaperHandoff;
            try
            {
                var descriptor = new RenderTextureDescriptor(playbackSource.Info.Layout.QuiltWidth, playbackSource.Info.Layout.QuiltHeight, RenderTextureFormat.ARGB32, 0)
                {
                    msaaSamples = 1,
                    mipCount = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                lease.playbackTexture = new RenderTexture(descriptor) { name = "ViewWeave.Playback.Quilt", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                lease.playbackTexture.Create();
                if (!lease.playbackTexture.IsCreated()) throw new InvalidOperationException("Playback render texture could not be created.");
                lease.recoveryRecord = BridgePlaybackRecoveryRecord.Create(lease.playbackTexture, lease.sessionGeneration);
                lease.recoveryRecord.SetGpuProofMode(lease.gpuProofMode);
                lease.cursor = new QuiltPlaybackCursor(playbackSource.Info.FramesPerSecond, playbackSource.Info.FrameCount);
                lease.SetTailFence();
                lease.lastRealtime = Time.realtimeSinceStartupAsDouble;
                lease.stateDeadline = lease.lastRealtime + PlaybackOwnershipTimeoutSeconds;
                lease.statusMessage = "Waiting for the Bridge transfer drain.";
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.Message);
                lease.FailBeforePlayback(error);
                return false;
            }
        }

        public void PausePlayback()
        {
            if (State != LeaseState.Playing) return;
            cursor.Pause();
            State = LeaseState.Paused;
            statusMessage = "Paused; the last displayed quilt is held.";
        }

        public void ResumePlayback()
        {
            if (State != LeaseState.Paused) return;
            if (!SessionStillOwned())
            {
                BeginCleanup(false, "Playback ownership changed; live rendering was not resumed.");
                return;
            }
            cursor.Resume();
            lastRealtime = Time.realtimeSinceStartupAsDouble;
            State = LeaseState.Playing;
            statusMessage = "Playing.";
        }

        public void StopPlayback()
        {
            if (State == LeaseState.Stopped || State == LeaseState.Retained || cleanupStarted) return;
            BeginCleanup(true, "Stopping playback and restoring the live Bridge session when ownership is unchanged.");
        }

        internal void PumpOnce()
        {
            PumpTransitionReadback();
            if (State == LeaseState.Retained)
            {
                if (CanReleaseTexture())
                {
                    State = LeaseState.Stopping;
                    CompleteCleanup(false);
                }
                return;
            }

            if (State == LeaseState.WaitingForDrain)
            {
                if (!SessionStillOwned()) { BeginCleanup(false, "Bridge playback lease was invalidated before playback began."); return; }
                if (BridgePlaybackCleanupPolicy.DeadlineExpiredWithoutProof(Time.realtimeSinceStartupAsDouble, stateDeadline, false))
                {
                    Retain("Bridge queue drain deadline expired without a transfer reset proof; playback resources remain retained.");
                    return;
                }
                if (!TailProofPassed()) return;
                if (!output.TryResetTransferForPlayback(sessionToken))
                {
                    if (!string.IsNullOrEmpty(output.LastError)) BeginCleanup(false, "Bridge transfer reset failed: " + output.LastError);
                    return;
                }
                State = LeaseState.Playing;
                statusMessage = "Playing.";
                lastRealtime = Time.realtimeSinceStartupAsDouble;
            }

            if (State == LeaseState.Playing)
            {
                if (!SessionStillOwned()) { BeginCleanup(false, "Bridge session or Renderer settings changed during playback."); return; }
                double now = Time.realtimeSinceStartupAsDouble;
                double delta = Math.Max(0d, now - lastRealtime);
                lastRealtime = now;
                QuiltPlaybackStep step = cursor.Advance(delta);
                if (step.HasFrame)
                {
                    currentFrame = step.FrameIndex;
                    skippedFrames += step.SkippedFrameCount;
                    SubmitFrame(step.FrameIndex);
                }
                if (step.IsEndOfStream && BridgePlaybackCleanupPolicy.ShouldStopOnCursorEnd(source.Info.IsSequence))
                    BeginCleanup(true, "Playback reached end of sequence.");
            }

            if (State == LeaseState.Stopping) PollCleanup();
        }

        void SubmitFrame(int index)
        {
            Texture2D decoded = null;
            try
            {
                decoded = source.LoadFrame(index);
                Graphics.Blit(decoded, playbackTexture);
                bool submitted = output.TrySubmitPlayback(sessionToken, playbackTexture, source.Info.Layout);
                SetTailFence();
                if (!submitted)
                {
                    string reason = string.IsNullOrEmpty(output.LastError) ? "Bridge rejected a playback frame." : "Bridge rejected a playback frame: " + output.LastError;
                    if (!string.IsNullOrEmpty(output.LastError)) session.NotifyPlaybackFailure(sessionToken, reason);
                    BeginCleanup(string.IsNullOrEmpty(output.LastError), reason);
                    return;
                }
                recoveryRecord.SetFrame(index, skippedFrames);
                statusMessage = "Playing frame " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " + source.Info.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; skipped " + skippedFrames.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            }
            catch (Exception exception)
            {
                BeginCleanup(true, "Playback frame failed: " + Sanitize(exception.Message));
            }
            finally
            {
                if (decoded != null) Destroy(decoded);
            }
        }

        void BeginCleanup(bool restoreLive, string message)
        {
            if (cleanupStarted || State == LeaseState.Stopped || State == LeaseState.Retained) return;
            cleanupStarted = true;
            restoreAutoRender = restoreLive && !invalidated;
            State = LeaseState.Stopping;
            stateDeadline = Time.realtimeSinceStartupAsDouble + PlaybackOwnershipTimeoutSeconds;
            statusMessage = message;
            if (output != null) output.ClearPlaybackTexture(sessionToken, playbackTexture);
            try { SetTailFence(); }
            catch (Exception exception)
            {
                Retain("Could not create the final playback graphics fence: " + Sanitize(exception.Message));
            }
        }

        void PollCleanup()
        {
            bool cleanupProofAvailable = CanReleaseTexture();
            if (BridgePlaybackCleanupPolicy.DeadlineExpiredWithoutProof(Time.realtimeSinceStartupAsDouble, stateDeadline, cleanupProofAvailable))
            {
                Retain("Playback cleanup deadline expired without GPU and Bridge ownership proof; resources remain retained.");
                return;
            }
            if (Time.realtimeSinceStartupAsDouble >= stateDeadline && cleanupProofAvailable) { CompleteCleanup(!invalidated); return; }
            bool tailProofPassed = TailProofPassed();
            if (!tailProofPassed) return;
            if (recoveryRecord != null) recoveryRecord.SetTailFencePassed(true);
            if (BridgePlaybackCleanupPolicy.ShouldWaitForFinalQuitProof(finalQuitPending, readbackCompleted, tailProofPassed)) return;
            if (output != null && output.Ready && session != null && session.State == ViewWeaveBridgeSession.SessionState.Rendering)
            {
                if (!resetCompleted)
                {
                    if (!output.TryResetTransferForPlayback(sessionToken))
                    {
                        if (!string.IsNullOrEmpty(output.LastError)) Retain("Bridge transfer reset failed: " + output.LastError);
                        return;
                    }
                    resetCompleted = true;
                    recoveryRecord.SetReadyReset(true);
                }
                CompleteCleanup(!invalidated);
                return;
            }

            if (handoffSeen || (output != null && output.HasReaperHandoff(sessionGeneration)))
            {
                if (recoveryRecord != null) recoveryRecord.SetReaperHandoff(sessionGeneration);
                CompleteCleanup(false);
                return;
            }
            if (output == null || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                Retain("Bridge output closed without a confirmed same-session reaper handoff; playback texture is retained.");
        }

        void CompleteCleanup(bool mayRestore)
        {
            if (!CanReleaseTexture())
            {
                Retain("Playback cleanup lacks a fence, transfer reset, or same-session reaper proof; texture is retained.");
                return;
            }

            bool restore = mayRestore && restoreAutoRender && !invalidated && SessionStillMatchesForRestore();
            if (output != null) output.ClearPlaybackTexture(sessionToken, playbackTexture);
            if (session != null) session.ReleasePlayback(sessionToken);
            if (restore)
            {
                try { rig.AutoRender = true; }
                catch (Exception exception) { errorMessage = "Live Auto Render was not restored: " + Sanitize(exception.Message); }
            }
            else if (restoreAutoRender && !invalidated && string.IsNullOrEmpty(errorMessage))
            {
                errorMessage = "Live Auto Render was not restored because the session or Renderer settings changed.";
            }

            if (output != null) output.PlaybackReaperHandoff -= OnReaperHandoff;
            ReleaseTexture();
            if (rendererLease != null) { rendererLease.Dispose(); rendererLease = null; }
            if (recoveryRecord != null) { DestroyImmediate(recoveryRecord); recoveryRecord = null; }
            State = LeaseState.Stopped;
            statusMessage = restore ? "Playback stopped; live Auto Render resumed." : "Playback stopped; live Auto Render was not resumed.";
            if (active == this) active = null;
            knownLeases.Remove(this);
            if (this != null && gameObject != null) Destroy(gameObject);
        }

        bool CanReleaseTexture()
        {
            return BridgePlaybackCleanupPolicy.CanRelease(
                TailProofPassed(), transitionProofRequired, readbackCompleted && readbackValid,
                output != null && output.Ready, resetCompleted,
                handoffSeen || (output != null && output.HasReaperHandoff(sessionGeneration)));
        }

        bool SessionStillOwned()
        {
            return !invalidated && session != null && output != null && rig != null && renderer != null &&
                session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.Ready &&
                output.SessionGeneration == sessionGeneration && rig.Renderer == renderer && !rig.AutoRender &&
                renderer.isActiveAndEnabled && output.isActiveAndEnabled && SettingsMatch() && SessionConfigurationMatches();
        }

        bool SessionStillMatchesForRestore()
        {
            bool sessionAndRendererActive = RestoreOwnersAreActive(session, renderer);
            bool outputMatches = RendererOutputMatches(renderer, initialOutputBehaviour);
            bool camerasMatch = RendererCamerasMatchForRestore(renderer, initialSourceCamera, initialReferenceCamera,
                initialSourceNearClip, initialSourceFarClip, initialReferenceNearClip, initialReferenceFarClip, initialFollowReferenceFieldOfView);
            return BridgePlaybackCleanupPolicy.HasStableRendererOwnership(sessionAndRendererActive, outputMatches, camerasMatch) &&
                output != null && rig != null &&
                session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.Ready &&
                output.SessionGeneration == sessionGeneration && rig.Renderer == renderer &&
                rig.isActiveAndEnabled && output.isActiveAndEnabled && !rig.AutoRender && SettingsMatch() &&
                session.runtimePath == initialRuntimePath && session.applicationName == initialApplicationName &&
                session.displayOrdinal == initialDisplayOrdinal && session.Quality == initialQuality && rig.Profile.Equals(initialProfile);
        }

        internal static bool RendererCamerasMatchForRestore(QuiltRenderer value, Camera expectedSource, Camera expectedReference,
            float sourceNear, float sourceFar, float referenceNear, float referenceFar, bool followReferenceFieldOfView)
        {
            if (value == null || !ReferenceEquals(value.sourceCamera, expectedSource) || !ReferenceEquals(value.referenceCamera, expectedReference) ||
                value.followReferenceFieldOfView != followReferenceFieldOfView || ReferenceEquals(expectedSource, null) || expectedSource == null ||
                expectedSource.nearClipPlane != sourceNear || expectedSource.farClipPlane != sourceFar)
                return false;
            if (ReferenceEquals(expectedReference, null)) return true;
            return expectedReference != null && expectedReference.nearClipPlane == referenceNear && expectedReference.farClipPlane == referenceFar;
        }

        internal static bool RendererOutputMatches(QuiltRenderer value, MonoBehaviour expectedOutput)
        {
            return value != null && ReferenceEquals(value.OutputBehaviour, expectedOutput);
        }

        internal static bool RestoreOwnersAreActive(ViewWeaveBridgeSession value, QuiltRenderer targetRenderer)
        {
            return value != null && value.isActiveAndEnabled && targetRenderer != null && targetRenderer.isActiveAndEnabled;
        }

        bool SessionConfigurationMatches()
        {
            return session != null && session.runtimePath == initialRuntimePath && session.applicationName == initialApplicationName &&
                session.displayOrdinal == initialDisplayOrdinal && session.Quality == initialQuality && rig != null && rig.Profile.Equals(initialProfile);
        }

        bool SettingsMatch()
        {
            return renderer != null && renderer.settings != null && initialSettings.Matches(renderer.settings) && renderer.viewRenderMode == initialRenderMode;
        }

        void SetTailFence()
        {
            if (gpuProofMode == BridgeGpuProofMode.Unsupported)
            {
                gpuProofMode = BridgeGraphicsBackendPolicy.SelectCurrentGpuProofMode();
                if (recoveryRecord != null) recoveryRecord.SetGpuProofMode(gpuProofMode);
            }
            hasTailFence = false;
            if (recoveryRecord != null)
                recoveryRecord.BeginTailProof();
            if (playbackTexture == null || !playbackTexture.IsCreated()) throw new InvalidOperationException("Playback texture is unavailable for a graphics fence.");
            if (gpuProofMode == BridgeGpuProofMode.ReadbackTail)
            {
                object identity = new object();
                tailReadbackIdentity = identity;
                tailReadbackCompletion = new BridgeEditorPreviewRetention.ReadbackCompletionLatch(identity, 1);
                tailReadback = AsyncGPUReadback.Request(playbackTexture, 0,
                    request => RecordTailReadback(identity, !request.hasError));
                hasTailFence = true;
                if (recoveryRecord != null) recoveryRecord.SetTailFenceIssued(true);
                return;
            }
            if (gpuProofMode != BridgeGpuProofMode.FenceAndReadback)
                throw new InvalidOperationException("No supported playback GPU proof mode is selected.");
            var command = new CommandBuffer { name = "ViewWeave Playback Tail Fence" };
            try
            {
                tailFence = command.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                Graphics.ExecuteCommandBuffer(command);
                hasTailFence = true;
                if (recoveryRecord != null) recoveryRecord.SetTailFenceIssued(true);
            }
            finally { command.Release(); }
        }

        bool FencePassed()
        {
            if (!hasTailFence) return false;
            try { return tailFence.passed; }
            catch (Exception exception)
            {
                Retain("Graphics fence status is unknown: " + Sanitize(exception.Message));
                return false;
            }
        }

        bool TailProofPassed()
        {
            if (gpuProofMode == BridgeGpuProofMode.FenceAndReadback) return FencePassed();
            if (gpuProofMode != BridgeGpuProofMode.ReadbackTail || !hasTailFence || tailReadbackCompletion == null) return false;
            try
            {
                if (tailReadbackCompletion.GetOutcome(0) == BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome.Pending)
                {
                    tailReadback.Update();
                    if (tailReadback.done) RecordTailReadback(tailReadbackIdentity, !tailReadback.hasError);
                }
                BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome outcome = tailReadbackCompletion.GetOutcome(0);
                bool succeeded = outcome == BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome.Succeeded;
                if (recoveryRecord != null)
                {
                    recoveryRecord.SetTailFencePassed(false);
                    recoveryRecord.SetReadback(outcome != BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome.Pending, succeeded);
                }
                if (outcome == BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome.Failed)
                    Retain("Playback graphics queue tail readback failed; source texture remains retained.");
                return succeeded;
            }
            catch (Exception exception)
            {
                Retain("Playback graphics queue tail proof is unknown: " + Sanitize(exception.Message));
                return false;
            }
        }

        void RecordTailReadback(object identity, bool succeeded)
        {
            if (tailReadbackCompletion == null || !ReferenceEquals(identity, tailReadbackIdentity)) return;
            tailReadbackCompletion.TryRecord(identity, 0, succeeded);
        }

        void OnSessionInvalidated(string reason)
        {
            invalidated = true;
            restoreAutoRender = false;
            BeginCleanup(false, string.IsNullOrEmpty(reason) ? "Bridge session invalidated playback." : reason);
        }

        void OnReaperHandoff(long generation)
        {
            if (generation != sessionGeneration) return;
            handoffSeen = true;
            if (recoveryRecord != null) recoveryRecord.SetReaperHandoff(generation);
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && IsBusy) { invalidated = true; BeginCleanup(false, "Application paused during playback."); }
        }

        void OnDestroy()
        {
            if (State != LeaseState.Stopped)
            {
                invalidated = true;
                PrepareForEditorTransition("Playback owner was destroyed; completing an Editor GPU ownership handshake.");
                if (State != LeaseState.Stopped && State != LeaseState.Retained)
                    Retain("Playback owner was destroyed before GPU completion could be proven; texture and renderer guard remain retained.");
            }
        }

        void OnDisable()
        {
            if (State == LeaseState.Stopped || State == LeaseState.Retained) return;
            invalidated = true;
            restoreAutoRender = false;
            BeginCleanup(false, "Playback owner was disabled; the Editor cleanup pump will finish the ownership proof.");
        }

        void FailBeforePlayback(string reason)
        {
            invalidated = true;
            if (output != null) output.PlaybackReaperHandoff -= OnReaperHandoff;
            if (sessionAcquired && session != null) session.ReleasePlayback(sessionToken ?? this);
            if (sessionAcquired && rig != null && session != null && session.State == ViewWeaveBridgeSession.SessionState.Rendering && output != null && output.Ready)
            {
                try { rig.AutoRender = true; } catch { }
            }
            if (playbackTexture != null)
            {
                if (playbackTexture.IsCreated()) playbackTexture.Release();
                DestroyImmediate(playbackTexture);
                playbackTexture = null;
            }
            if (rendererLease != null) { rendererLease.Dispose(); rendererLease = null; }
            if (recoveryRecord != null) { DestroyImmediate(recoveryRecord); recoveryRecord = null; }
            State = LeaseState.Stopped;
            errorMessage = Sanitize(reason);
            statusMessage = "Playback did not start.";
            if (active == this) active = null;
            knownLeases.Remove(this);
            DestroyImmediate(gameObject);
        }

        void Retain(string reason)
        {
            if (State == LeaseState.Retained) return;
            State = LeaseState.Retained;
            invalidated = true;
            restoreAutoRender = false;
            errorMessage = Sanitize(reason);
            statusMessage = "Playback resources retained because GPU completion could not be proven. Restart the Unity Editor before retrying.";
            if (recoveryRecord != null) recoveryRecord.SetDiagnostic(errorMessage);
            Debug.LogError("[ViewWeave] " + errorMessage);
        }

        void ReleaseTexture()
        {
            if (playbackTexture == null) return;
            if (RenderTexture.active == playbackTexture) RenderTexture.active = null;
            if (playbackTexture.IsCreated()) playbackTexture.Release();
            Destroy(playbackTexture);
            playbackTexture = null;
        }

        internal void PrepareForEditorTransition(string reason)
        {
            if (State == LeaseState.Stopped) return;
            invalidated = true;
            restoreAutoRender = false;
            cleanupStarted = true;
            transitionProofRequired = true;
            State = LeaseState.Stopping;
            stateDeadline = Time.realtimeSinceStartupAsDouble + PlaybackOwnershipTimeoutSeconds;
            statusMessage = reason;
            if (output != null) output.ClearPlaybackTexture(sessionToken, playbackTexture);
            try { if (!hasTailFence) SetTailFence(); }
            catch (Exception exception) { Retain("Could not create the Editor transition fence: " + Sanitize(exception.Message)); return; }
            if (recoveryRecord == null)
            {
                Retain("Playback has no durable recovery record; source texture remains retained.");
                return;
            }
            try
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(playbackTexture, mipIndex: 0, x: 0, width: 1, y: 0, height: 1, z: 0, depth: 1, dstFormat: TextureFormat.RGBA32);
                request.WaitForCompletion();
                readbackCompleted = request.done;
                readbackValid = request.done && !request.hasError;
                recoveryRecord.SetReadback(readbackCompleted, readbackValid);
                if (!readbackValid || !TailProofPassed())
                {
                    Retain("Playback readback or final fence did not complete successfully during Editor transition.");
                    return;
                }
                recoveryRecord.SetTailFencePassed(true);
                if (output != null && output.Ready && output.TryResetTransferForPlayback(sessionToken))
                {
                    resetCompleted = true;
                    recoveryRecord.SetReadyReset(true);
                    CompleteCleanup(false);
                    return;
                }
                if (output != null && output.HasReaperHandoff(sessionGeneration))
                {
                    handoffSeen = true;
                    recoveryRecord.SetReaperHandoff(sessionGeneration);
                    CompleteCleanup(false);
                    return;
                }
                recoveryRecord.SetDiagnostic("Editor transition wait completed, but Bridge transfer reset or same-generation reaper handoff is still required.");
            }
            catch (Exception exception)
            {
                readbackCompleted = false;
                readbackValid = false;
                recoveryRecord.SetReadback(false, false);
                Retain("Editor transition GPU readback failed: " + Sanitize(exception.Message));
            }
        }

        internal void PrepareForFinalQuit(string reason)
        {
            if (State == LeaseState.Stopped || transitionReadbackIssued || transitionProofRequired) return;
            invalidated = true;
            restoreAutoRender = false;
            cleanupStarted = true;
            transitionProofRequired = true;
            finalQuitPending = true;
            State = LeaseState.Stopping;
            stateDeadline = Time.realtimeSinceStartupAsDouble + PlaybackOwnershipTimeoutSeconds;
            statusMessage = reason;
            if (output != null) output.ClearPlaybackTexture(sessionToken, playbackTexture);
            try
            {
                if (!hasTailFence) SetTailFence();
                if (recoveryRecord == null)
                {
                    Retain("Playback has no durable recovery record; source texture remains retained.");
                    return;
                }
                if (playbackTexture == null)
                {
                    Retain("Playback source texture is unavailable; GPU completion remains unproven.");
                    return;
                }
                transitionReadback = AsyncGPUReadback.Request(playbackTexture, mipIndex: 0, x: 0, width: 1,
                    y: 0, height: 1, z: 0, depth: 1, dstFormat: TextureFormat.RGBA32);
                transitionReadbackIssued = true;
                transitionFlushAttempted = false;
                transitionFlushFailed = false;
            }
            catch (Exception exception)
            {
                readbackCompleted = false;
                readbackValid = false;
                if (recoveryRecord != null) recoveryRecord.SetReadback(false, false);
                Retain("Final quit GPU readback could not be started: " + Sanitize(exception.Message));
            }
        }

        void PumpTransitionReadback()
        {
            if (!transitionReadbackIssued || readbackCompleted || transitionFlushFailed) return;
            try
            {
                BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref transitionFlushAttempted);
            }
            catch (Exception exception)
            {
                transitionFlushFailed = true;
                readbackCompleted = false;
                readbackValid = false;
                if (recoveryRecord != null) recoveryRecord.SetReadback(false, false);
                Retain("Final quit GPU command submission failed: " + Sanitize(exception.Message));
                return;
            }
            try
            {
                transitionReadback.Update();
                if (!transitionReadback.done) return;
                readbackCompleted = true;
                readbackValid = !transitionReadback.hasError;
                if (recoveryRecord != null) recoveryRecord.SetReadback(true, readbackValid);
                if (!readbackValid) Retain("Final quit GPU readback failed; playback resources remain retained.");
            }
            catch (Exception exception)
            {
                readbackCompleted = false;
                readbackValid = false;
                if (recoveryRecord != null) recoveryRecord.SetReadback(false, false);
                Retain("Final quit GPU readback update failed: " + Sanitize(exception.Message));
            }
        }

        internal void TryCompleteAfterBridgeShutdown()
        {
            if (State != LeaseState.Stopping && State != LeaseState.Retained) return;
            if (State == LeaseState.Retained && recoveryRecord != null && recoveryRecord.HasCompleteProof)
            {
                State = LeaseState.Stopping;
            }
            if (output != null && output.HasReaperHandoff(sessionGeneration)) OnReaperHandoff(sessionGeneration);
            bool tailProofPassed = TailProofPassed();
            if (BridgePlaybackCleanupPolicy.ShouldWaitForFinalQuitProof(finalQuitPending, readbackCompleted, tailProofPassed)) return;
            if (CanReleaseTexture()) CompleteCleanup(false);
            else if (State != LeaseState.Retained) Retain("Bridge shutdown did not provide a safe same-generation handoff; playback texture remains retained.");
        }

        internal static void PrepareForEditorTransitionAll(string reason)
        {
            for (int i = knownLeases.Count - 1; i >= 0; --i)
            {
                BridgeQuiltPlaybackLease lease = knownLeases[i];
                if (!ReferenceEquals(lease, null)) lease.PrepareForEditorTransition(reason);
            }
        }

        internal static void PrepareForFinalQuitAll(string reason)
        {
            for (int i = knownLeases.Count - 1; i >= 0; --i)
            {
                BridgeQuiltPlaybackLease lease = knownLeases[i];
                if (!ReferenceEquals(lease, null)) lease.PrepareForFinalQuit(reason);
            }
        }

        internal static bool FinalQuitCleanupComplete
        {
            get
            {
                foreach (BridgeQuiltPlaybackLease lease in knownLeases)
                    if (!ReferenceEquals(lease, null)) return false;
                BridgePlaybackRecoveryRecord[] records = Resources.FindObjectsOfTypeAll<BridgePlaybackRecoveryRecord>();
                foreach (BridgePlaybackRecoveryRecord record in records)
                    if (record != null) return false;
                return true;
            }
        }

        internal static void CompleteAfterBridgeShutdownAll()
        {
            for (int i = knownLeases.Count - 1; i >= 0; --i)
            {
                BridgeQuiltPlaybackLease lease = knownLeases[i];
                if (ReferenceEquals(lease, null)) { knownLeases.RemoveAt(i); continue; }
                lease.TryCompleteAfterBridgeShutdown();
            }
        }

        internal static void PumpAll()
        {
            for (int i = knownLeases.Count - 1; i >= 0; --i)
            {
                BridgeQuiltPlaybackLease lease = knownLeases[i];
                if (ReferenceEquals(lease, null)) { knownLeases.RemoveAt(i); continue; }
                try { lease.PumpOnce(); }
                catch (Exception exception) { lease.Retain("Playback cleanup pump failed: " + Sanitize(exception.Message)); }
            }
        }

        internal static void RecoverRetainedRecords()
        {
            BridgePlaybackRecoveryRecord[] records = Resources.FindObjectsOfTypeAll<BridgePlaybackRecoveryRecord>();
            foreach (BridgePlaybackRecoveryRecord record in records)
            {
                if (record == null) continue;
                if (!record.HasCompleteProof) record.TryReproveAfterDomainReload();
                if (record.HasCompleteProof)
                {
                    record.ReleaseProvenSource();
                    DestroyImmediate(record);
                }
                else
                {
                    record.SetDiagnostic("Previous Editor domain could not prove playback GPU completion. Resource remains retained; restart the Unity Editor before retrying.");
                    Debug.LogError("[ViewWeave] A quilt playback recovery record has unproven GPU ownership. Restart the Unity Editor before retrying.");
                }
            }
        }

        static bool HasUnprovenRecoveryRecord()
        {
            BridgePlaybackRecoveryRecord[] records = Resources.FindObjectsOfTypeAll<BridgePlaybackRecoveryRecord>();
            bool hasUnprovenRecord = false;
            foreach (BridgePlaybackRecoveryRecord record in records)
            {
                if (record == null) continue;
                if (!record.HasCompleteProof)
                {
                    hasUnprovenRecord = true;
                    continue;
                }
                record.ReleaseProvenSource();
                DestroyImmediate(record);
            }
            return hasUnprovenRecord;
        }

        static bool SourceMatchesRenderer(QuiltPlaybackInfo info, QuiltRenderer value, out string error)
        {
            error = string.Empty;
            QuiltLayout layout = info.Layout;
            QuiltSettings settings = value.settings;
            if (layout.QuiltWidth != settings.quiltWidth || layout.QuiltHeight != settings.quiltHeight || layout.Columns != settings.columns || layout.Rows != settings.rows || layout.ViewCount != settings.views || Math.Abs(layout.AspectRatio - settings.viewAspect) > 0.0001f)
            {
                error = "Quilt source layout must match the active Bridge Renderer dimensions, grid, view count, and aspect.";
                return false;
            }
            Camera sourceCamera = value.sourceCamera;
            if (info.HasCameraGeometry && (sourceCamera == null || Math.Abs(info.Geometry.fieldOfView - settings.fieldOfView) > 0.001f || Math.Abs(info.Geometry.cameraDistance - settings.cameraDistance) > 0.001f || Math.Abs(info.Geometry.viewCone - settings.viewCone) > 0.001f || Math.Abs(info.Geometry.nearClip - sourceCamera.nearClipPlane) > 0.001f || Math.Abs(info.Geometry.farClip - sourceCamera.farClipPlane) > 0.001f))
            {
                error = "Quilt source camera geometry does not match the active Renderer lens settings.";
                return false;
            }
            return true;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }

        readonly struct QuiltSettingsSnapshot
        {
            readonly int width, height, columns, rows, views;
            readonly float aspect, fieldOfView, distance, cone;
            internal QuiltSettingsSnapshot(QuiltSettings settings)
            {
                width = settings.quiltWidth; height = settings.quiltHeight; columns = settings.columns; rows = settings.rows; views = settings.views;
                aspect = settings.viewAspect; fieldOfView = settings.fieldOfView; distance = settings.cameraDistance; cone = settings.viewCone;
            }
            internal bool Matches(QuiltSettings settings)
            {
                return settings != null && width == settings.quiltWidth && height == settings.quiltHeight && columns == settings.columns && rows == settings.rows && views == settings.views &&
                    aspect == settings.viewAspect && fieldOfView == settings.fieldOfView && distance == settings.cameraDistance && cone == settings.viewCone;
            }
        }
    }

    internal static class BridgePlaybackCleanupPolicy
    {
        internal static bool ShouldWaitForFinalQuitProof(bool finalQuitPending, bool readbackCompleted, bool fencePassed)
        {
            return finalQuitPending && (!readbackCompleted || !fencePassed);
        }

        internal static bool DeadlineExpiredWithoutProof(double now, double deadline, bool proofAvailable)
        {
            return now >= deadline && !proofAvailable;
        }

        internal static bool ShouldStopOnCursorEnd(bool isSequence) { return isSequence; }

        internal static bool HasStableRendererOwnership(bool sessionAndRendererActive, bool outputMatches, bool camerasMatch)
        {
            return sessionAndRendererActive && outputMatches && camerasMatch;
        }

        internal static bool CanRelease(bool tailFencePassed, bool editorTransition, bool readbackValid, bool outputReady, bool transferResetSucceeded, bool reaperHandoffForGeneration)
        {
            if (!tailFencePassed || (editorTransition && !readbackValid)) return false;
            return outputReady ? transferResetSucceeded : reaperHandoffForGeneration;
        }
    }
}
#endif
