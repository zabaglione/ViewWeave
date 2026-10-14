return new System.Func<string>(() =>
{
    var renderers = UnityEngine.Object.FindObjectsByType<ViewWeave.QuiltRenderer>(UnityEngine.FindObjectsInactive.Include);
    if (renderers.Length != 1) return "preflight_failed_renderer_count";
    var renderer = renderers[0];
    var rig = renderer.GetComponent<ViewWeave.ViewWeaveRig>();
    var session = renderer.GetComponent<ViewWeave.ViewWeaveBridgeSession>();
    var output = renderer.GetComponent<ViewWeave.BridgeWorkerQuiltOutput>();
    if (rig == null || session == null || output == null) return "preflight_failed_bridge_components";
    var visualEffects = UnityEngine.Object.FindObjectsByType<UnityEngine.VFX.VisualEffect>(UnityEngine.FindObjectsInactive.Include);
    int activeEffectCount = 0;
    for (int i = 0; i < visualEffects.Length; i++)
        if (visualEffects[i] != null && visualEffects[i].enabled && visualEffects[i].gameObject.activeInHierarchy) activeEffectCount++;
    if (UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
        return "preflight_failed_already_playing";
    if (renderer.autoRender || rig.AutoRender) return "preflight_failed_autorender_not_off";
    if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle || output.State != ViewWeave.BridgeWorkerQuiltOutput.WorkerState.Closed)
        return "preflight_failed_session_not_idle";
    if (renderer.LastAutoRenderError != null && renderer.LastAutoRenderError.Length != 0)
        return "preflight_failed_existing_render_error";
    long prePlayRawBaseline = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
    if (prePlayRawBaseline <= 0)
        return "preflight_failed_raw_graphics_unavailable";

    string logPath = "__WATCH_LOG_PATH__";
    string stopPath = "__STOP_FLAG_PATH__";
    string baselinePath = "__BASELINE_PATH__";
    if (System.IO.File.Exists(logPath) || System.IO.File.Exists(stopPath) || System.IO.File.Exists(baselinePath)) return "preflight_failed_evidence_exists";

    const long byteLimit = 512L * 1024L * 1024L;
    const long warmupLimit = 4L * 1024L * 1024L * 1024L;
    const long stabilityRangeLimit = 16L * 1024L * 1024L;
    const double normalSeconds = 2.0;
    const int normalFrameLimit = 30;
    const double normalSampleInterval = 0.13;
    const double playStartWaitSeconds = 30.0;
    const double connectSeconds = 10.0;
    const double readySeconds = 2.0;
    const long revisionLimit = 3;
    const double closeSeconds = 10.0;
    double playStart = 0.0;
    double sessionStart = 0.0;
    double readyStart = 0.0;
    double closeStart = 0.0;
    long playRawBaseline = 0;
    long sessionRawBaseline = 0;
    long readyStartRevision = 0;
    long playStartRevision = 0;
    uint readyStartSubmissions = 0;
    uint readyStartCompleted = 0;
    int playStartFrame = 0;
    int readyStartFrame = 0;
    int stopFrame = 0;
    int lastSampleFrame = -1;
    int stage = 0;
    double registeredTime = UnityEditor.EditorApplication.timeSinceStartup;
    bool oldStartOnEnable = session.startOnEnable;
    ViewWeave.QuiltQualityProfile oldRigProfile = rig.Profile;
    bool startFlagChanged = oldStartOnEnable;
    bool stopRequested = false;
    bool logWriteFailed = false;
    long[] normalRawSamples = new long[3];
    double[] normalSampleTimes = new double[3];
    int[] normalSampleFrames = new int[3];
    int normalSampleCount = 0;
    long normalStableRawMinimum = 0;
    long normalStableRawMaximum = 0;
    double lastNormalSampleTime = -1.0;
    session.startOnEnable = false;

    System.Action<string, string, long, long> append = (kind, reason, raw, baseline) =>
    {
        try
        {
            var profile = output.Profile;
            string row = "event=" + kind + "|utc=" + System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
            "|frame=" + UnityEngine.Time.frameCount + "|editorTime=" + UnityEditor.EditorApplication.timeSinceStartup.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            "|sessionState=" + session.State + "|workerState=" + output.State + "|ready=" + output.Ready +
            "|profileValid=" + profile.IsValid + "|profileSize=" + profile.Width + "x" + profile.Height +
            "|profileQuilt=" + profile.QuiltWidth + "x" + profile.QuiltHeight + "|profileGrid=" + profile.Columns + "x" + profile.Rows +
            "|profileViews=" + (profile.Columns * profile.Rows) + "|profileViewcone=" + profile.Viewcone.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            "|quality=" + session.Quality + "|revision=" + renderer.QuiltRevision + "|rawGraphicsBytes=" + raw +
            "|rigProfile=" + rig.Profile.QuiltWidth + "x" + rig.Profile.QuiltHeight + "," + rig.Profile.Columns + "x" + rig.Profile.Rows + ",views=" + rig.Profile.Views +
            "|rigProfileOriginal=" + oldRigProfile.QuiltWidth + "x" + oldRigProfile.QuiltHeight + "," + oldRigProfile.Columns + "x" + oldRigProfile.Rows + ",views=" + oldRigProfile.Views +
            "|rawBaselineBytes=" + baseline + "|rawDeltaBytes=" + (raw - baseline) +
            "|submissions=" + output.Submissions + "|completed=" + output.Completed +
            "|readyFrameDelta=" + (stage >= 3 ? UnityEngine.Time.frameCount - readyStartFrame : 0) +
            "|readySubmissionDelta=" + (stage >= 3 ? output.Submissions - readyStartSubmissions : 0) +
            "|readyCompletedDelta=" + (stage >= 3 ? output.Completed - readyStartCompleted : 0) +
            "|presentAccepted=" + output.SubpixelRenderCompleted + "|presentAcceptedCount=" + output.SubpixelPresentAccepted +
            "|autoRender=" + renderer.autoRender + "|rigAutoRender=" + rig.AutoRender +
            "|startOnEnable=" + session.startOnEnable + "|startOnEnableOriginal=" + oldStartOnEnable +
            "|visualEffectComponents=" + visualEffects.Length + "|activeVisualEffects=" + activeEffectCount +
            "|lastAutoErrorPresent=" + (!string.IsNullOrEmpty(renderer.LastAutoRenderError)) +
            "|sessionErrorPresent=" + (!string.IsNullOrEmpty(session.LastError)) +
            "|shutdownPending=" + ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount +
            "|historyCameras=" + (renderer.sourceCamera == null || renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>() == null ? -1 : renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>().HistoryCameraCount) +
            "|texture=" + (renderer.QuiltTexture == null ? "null" : renderer.QuiltTexture.width + "x" + renderer.QuiltTexture.height + "," + renderer.QuiltTexture.graphicsFormat + ",created=" + renderer.QuiltTexture.IsCreated()) +
            "|reason=" + reason;
            System.IO.File.AppendAllText(logPath, row + System.Environment.NewLine, System.Text.Encoding.ASCII);
        }
        catch { logWriteFailed = true; }
    };

    System.Action<string> restoreStartFlag = reason =>
    {
        if (!startFlagChanged) return;
        session.startOnEnable = oldStartOnEnable;
        startFlagChanged = false;
        append("start_on_enable_restored", reason, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
    };

    System.Action<string> requestSessionStop = reason =>
    {
        if (stopRequested) return;
        stopRequested = true;
        try
        {
            if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle)
                session.StopSession();
        }
        catch (System.Exception exception)
        {
            append("session_stop_exception", exception.GetType().Name, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
        }
        closeStart = UnityEditor.EditorApplication.timeSinceStartup;
        stopFrame = UnityEngine.Time.frameCount;
        stage = 5;
        append("session_stop_requested", reason, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
    };

    System.Action<string> finishPlay = reason =>
    {
        if (stage >= 6) return;
        try
        {
            if (rig.AutoRender) rig.AutoRender = false;
            if (renderer.autoRender) renderer.autoRender = false;
        }
        catch (System.Exception exception)
        {
            append("autorender_disable_exception", exception.GetType().Name, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
        }
        restoreStartFlag(reason);
        stage = 6;
        stopFrame = UnityEngine.Time.frameCount;
        append("play_stop_requested", reason, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
    };

    UnityEditor.EditorApplication.CallbackFunction tick = null;
    tick = () =>
    {
        try
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            long raw = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();

            if (stage == 0)
            {
                if (System.IO.File.Exists(stopPath) || now - registeredTime >= playStartWaitSeconds)
                {
                    bool wasPlaying = UnityEditor.EditorApplication.isPlaying;
                    string cancelReason = System.IO.File.Exists(stopPath) ? "cancelled_before_play" : "play_start_timeout";
                    append("play_start_cancelled", cancelReason, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), 0);
                    restoreStartFlag(cancelReason);
                    stage = 7;
                    UnityEditor.EditorApplication.update -= tick;
                    if (wasPlaying) UnityEditor.EditorApplication.isPlaying = false;
                    return;
                }
                if (!UnityEditor.EditorApplication.isPlaying) return;
                if (raw <= 0)
                {
                    append("play_start_limit", "raw_counter_unavailable", raw, 0);
                    restoreStartFlag("raw_counter_unavailable_at_play_start");
                    stage = 7;
                    UnityEditor.EditorApplication.update -= tick;
                    UnityEditor.EditorApplication.isPlaying = false;
                    return;
                }
                stage = 1;
                playStart = now;
                playRawBaseline = raw;
                playStartRevision = renderer.QuiltRevision;
                playStartFrame = UnityEngine.Time.frameCount;
                append("play_started_normal_only", "startOnEnable_temporarily_disabled", raw, playRawBaseline);
                return;
            }

            if (stage >= 7 && stage != 8 && stage != 9) return;
            if (!UnityEditor.EditorApplication.isPlaying)
            {
                if (rig.AutoRender) rig.AutoRender = false;
                if (renderer.autoRender) renderer.autoRender = false;
                restoreStartFlag("unexpected_play_exit");
                append("play_ended_unexpectedly", "external_play_stop", raw, sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
                stage = 7;
                UnityEditor.EditorApplication.update -= tick;
                return;
            }

            if (System.IO.File.Exists(stopPath))
            {
                if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle && stage < 5)
                    requestSessionStop("external_private_limit_or_timeout");
                else if (stage < 5)
                    finishPlay("external_private_limit_or_timeout");
            }

            if (logWriteFailed)
            {
                if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle && stage < 5)
                    requestSessionStop("watch_log_write_failed");
                else if (stage < 5)
                    finishPlay("watch_log_write_failed");
            }

            if (stage == 1)
            {
                if (now - playStart >= normalSeconds || UnityEngine.Time.frameCount - playStartFrame >= normalFrameLimit)
                {
                    append("normal_phase_stability_timeout", normalSampleCount == 3 ? "raw_range_unstable" : "insufficient_distinct_samples", raw, prePlayRawBaseline);
                    finishPlay("normal_phase_not_stable_within_bound");
                    return;
                }
                if (raw <= 0 || raw - prePlayRawBaseline >= warmupLimit)
                {
                    append("normal_only_limit", raw <= 0 ? "raw_counter_unavailable" : "preplay_raw_growth_limit_4gib", raw, prePlayRawBaseline);
                    if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle) requestSessionStop("normal_only_limit");
                    else finishPlay(raw <= 0 ? "raw_counter_unavailable" : "normal_only_raw_growth_limit");
                    return;
                }
                if (UnityEngine.Time.frameCount != lastSampleFrame && (lastNormalSampleTime < 0.0 || now - lastNormalSampleTime >= normalSampleInterval))
                {
                    lastSampleFrame = UnityEngine.Time.frameCount;
                    lastNormalSampleTime = now;
                    if (normalSampleCount < 3)
                    {
                        normalRawSamples[normalSampleCount] = raw;
                        normalSampleTimes[normalSampleCount] = now;
                        normalSampleFrames[normalSampleCount] = UnityEngine.Time.frameCount;
                        normalSampleCount++;
                    }
                    else
                    {
                        normalRawSamples[0] = normalRawSamples[1]; normalRawSamples[1] = normalRawSamples[2]; normalRawSamples[2] = raw;
                        normalSampleTimes[0] = normalSampleTimes[1]; normalSampleTimes[1] = normalSampleTimes[2]; normalSampleTimes[2] = now;
                        normalSampleFrames[0] = normalSampleFrames[1]; normalSampleFrames[1] = normalSampleFrames[2]; normalSampleFrames[2] = UnityEngine.Time.frameCount;
                    }
                    append("normal_only_sample", "", raw, prePlayRawBaseline);
                }
                bool normalRawStable = normalSampleCount == 3 && normalSampleTimes[2] - normalSampleTimes[0] >= 0.25 && normalSampleFrames[0] != normalSampleFrames[1] && normalSampleFrames[1] != normalSampleFrames[2] && System.Math.Max(normalRawSamples[0], System.Math.Max(normalRawSamples[1], normalRawSamples[2])) - System.Math.Min(normalRawSamples[0], System.Math.Min(normalRawSamples[1], normalRawSamples[2])) <= stabilityRangeLimit;
                if (normalRawStable)
                {
                    if (renderer.QuiltRevision != playStartRevision || renderer.QuiltTexture != null)
                    {
                        finishPlay("quilt_changed_during_normal_only");
                        return;
                    }
                    normalStableRawMinimum = System.Math.Min(normalRawSamples[0], System.Math.Min(normalRawSamples[1], normalRawSamples[2]));
                    normalStableRawMaximum = System.Math.Max(normalRawSamples[0], System.Math.Max(normalRawSamples[1], normalRawSamples[2]));
                    stage = 8;
                    sessionStart = now;
                    append("normal_phase_stability_candidate", "raw_stable_waiting_private_ack", raw, prePlayRawBaseline);
                    return;
                }
                return;
            }

            if (stage == 8)
            {
                if (now - playStart >= normalSeconds || UnityEngine.Time.frameCount - playStartFrame >= normalFrameLimit || now - sessionStart >= 0.75)
                {
                    append("normal_phase_private_ack_timeout", "baseline_handshake_timeout", raw, prePlayRawBaseline);
                    finishPlay("normal_phase_private_baseline_timeout");
                    return;
                }
                if (raw <= 0 || raw - prePlayRawBaseline >= warmupLimit)
                {
                    append("normal_phase_baseline_limit", raw <= 0 ? "raw_counter_unavailable" : "preplay_raw_growth_limit_4gib", raw, prePlayRawBaseline);
                    finishPlay("normal_phase_baseline_limit");
                    return;
                }
                long candidateMinimum = System.Math.Min(normalStableRawMinimum, raw);
                long candidateMaximum = System.Math.Max(normalStableRawMaximum, raw);
                if (candidateMaximum - candidateMinimum > stabilityRangeLimit)
                {
                    append("normal_phase_raw_unstable_during_ack", "candidate_range_exceeded_16mib", raw, prePlayRawBaseline);
                    finishPlay("normal_phase_raw_unstable_during_ack");
                    return;
                }
                normalStableRawMinimum = candidateMinimum;
                normalStableRawMaximum = candidateMaximum;
                if (renderer.QuiltRevision != playStartRevision || renderer.QuiltTexture != null)
                {
                    finishPlay("quilt_changed_before_phase_baseline");
                    return;
                }
                if (System.IO.File.Exists(baselinePath))
                {
                    string[] fields = System.IO.File.ReadAllText(baselinePath).Trim().Split('|');
                    long phasePrivateBaseline;
                    if (fields.Length < 6 || fields[0] != "stable" || !long.TryParse(fields[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out phasePrivateBaseline) || phasePrivateBaseline <= 0)
                    {
                        append("normal_phase_private_unstable", "external_private_samples_not_stable", raw, prePlayRawBaseline);
                        finishPlay("normal_phase_private_not_stable");
                        return;
                    }
                    sessionRawBaseline = raw;
                    sessionStart = now;
                    append("normal_phase_baseline_fixed", fields[1] + ",privateRange=" + fields[5], raw, sessionRawBaseline);
                    try
                    {
                        bool started = session.StartSession();
                        append("bridge_start_called", started ? "accepted" : "rejected", raw, sessionRawBaseline);
                        if (!started)
                        {
                            requestSessionStop("bridge_start_rejected");
                            return;
                        }
                        stage = 2;
                    }
                    catch (System.Exception exception)
                    {
                        append("bridge_start_exception", exception.GetType().Name, raw, sessionRawBaseline);
                        requestSessionStop("bridge_start_exception");
                        return;
                    }
                    return;
                }
                if (now - playStart >= normalSeconds || UnityEngine.Time.frameCount - playStartFrame >= normalFrameLimit || now - sessionStart >= 0.75)
                {
                    append("normal_phase_private_ack_timeout", "baseline_handshake_timeout", raw, prePlayRawBaseline);
                    finishPlay("normal_phase_private_baseline_timeout");
                    return;
                }
                return;
            }

            if (stage >= 2 && stage < 5 && (raw <= 0 || raw - prePlayRawBaseline >= warmupLimit))
            {
                append("session_preplay_raw_limit", raw <= 0 ? "raw_counter_unavailable" : "preplay_raw_growth_limit_4gib", raw, prePlayRawBaseline);
                requestSessionStop("session_preplay_raw_limit");
                return;
            }

            if (stage == 2)
            {
                long delta = raw - sessionRawBaseline;
                if (raw <= 0 || delta >= byteLimit)
                {
                    append("connecting_limit", raw <= 0 ? "raw_counter_unavailable" : "raw_growth_limit", raw, sessionRawBaseline);
                    requestSessionStop(raw <= 0 ? "raw_counter_unavailable" : "connecting_raw_growth_limit");
                    return;
                }
                if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(renderer.LastAutoRenderError) || session.State == ViewWeave.ViewWeaveBridgeSession.SessionState.Fault)
                {
                    append("connecting_fault", "error_present", raw, sessionRawBaseline);
                    requestSessionStop("bridge_connecting_fault");
                    return;
                }
                if (session.State == ViewWeave.ViewWeaveBridgeSession.SessionState.Rendering && output.Ready && output.Profile.IsValid)
                {
                    stage = 3;
                    readyStart = now;
                    readyStartRevision = renderer.QuiltRevision;
                    readyStartFrame = UnityEngine.Time.frameCount;
                    readyStartSubmissions = output.Submissions;
                    readyStartCompleted = output.Completed;
                    append("bridge_ready_measurement_started", "ready_profile_valid", raw, sessionRawBaseline);
                    return;
                }
                if (now - sessionStart >= connectSeconds)
                {
                    append("bridge_connect_timeout", "connecting_timeout", raw, sessionRawBaseline);
                    requestSessionStop("bridge_connect_timeout");
                    return;
                }
                if (UnityEngine.Time.frameCount != lastSampleFrame && UnityEngine.Time.frameCount % 12 == 0)
                {
                    append("bridge_connecting_sample", "", raw, sessionRawBaseline);
                    lastSampleFrame = UnityEngine.Time.frameCount;
                }
                return;
            }

            if (stage == 3)
            {
                long delta = raw - sessionRawBaseline;
                if (raw <= 0 || delta >= byteLimit)
                {
                    append("ready_limit", raw <= 0 ? "raw_counter_unavailable" : "raw_growth_limit", raw, sessionRawBaseline);
                    requestSessionStop(raw <= 0 ? "raw_counter_unavailable" : "ready_raw_growth_limit");
                    return;
                }
                if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(renderer.LastAutoRenderError) || session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Rendering || !output.Ready)
                {
                    append("ready_fault", "session_or_render_error", raw, sessionRawBaseline);
                    requestSessionStop("bridge_ready_fault");
                    return;
                }
                if (UnityEngine.Time.frameCount != lastSampleFrame && (UnityEngine.Time.frameCount % 6 == 0 || delta >= byteLimit))
                {
                    append("bridge_ready_sample", "", raw, sessionRawBaseline);
                    lastSampleFrame = UnityEngine.Time.frameCount;
                }
                if (now - readyStart >= readySeconds || renderer.QuiltRevision - readyStartRevision >= revisionLimit)
                {
                    requestSessionStop("bounded_ready_window_complete");
                    return;
                }
                return;
            }

            if (stage == 5)
            {
                if (raw > 0 && raw - sessionRawBaseline >= byteLimit)
                {
                    append("closing_limit", "raw_growth_limit", raw, sessionRawBaseline);
                    finishPlay("closing_raw_growth_limit");
                    return;
                }
                if (session.State == ViewWeave.ViewWeaveBridgeSession.SessionState.Idle && ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
                {
                    append("bridge_closed_idle", "shutdown_pending_zero", raw, sessionRawBaseline);
                    stage = 9;
                    stopFrame = UnityEngine.Time.frameCount;
                    return;
                }
                if (now - closeStart >= closeSeconds)
                {
                    append("bridge_close_timeout", "shutdown_not_idle", raw, sessionRawBaseline);
                    finishPlay("bridge_close_timeout");
                    return;
                }
                if (UnityEngine.Time.frameCount != lastSampleFrame && UnityEngine.Time.frameCount % 12 == 0)
                {
                    append("bridge_closing_sample", "", raw, sessionRawBaseline);
                    lastSampleFrame = UnityEngine.Time.frameCount;
                }
                return;
            }

            if (stage == 9 && UnityEngine.Time.frameCount > stopFrame)
            {
                bool profileMatches = rig.Profile.QuiltWidth == oldRigProfile.QuiltWidth && rig.Profile.QuiltHeight == oldRigProfile.QuiltHeight &&
                    rig.Profile.Columns == oldRigProfile.Columns && rig.Profile.Rows == oldRigProfile.Rows && rig.Profile.Views == oldRigProfile.Views &&
                    rig.Profile.Aspect == oldRigProfile.Aspect && rig.Profile.ViewCone == oldRigProfile.ViewCone;
                if (!profileMatches)
                {
                    try
                    {
                        rig.ApplyProfile(oldRigProfile);
                        append("original_profile_restored", "profile_fields_restored_in_memory", raw, sessionRawBaseline);
                    }
                    catch (System.Exception exception)
                    {
                        append("original_profile_restore_failed", exception.GetType().Name, raw, sessionRawBaseline);
                    }
                }
                else append("original_profile_restored", "profile_already_matches", raw, sessionRawBaseline);
                finishPlay("bridge_closed_idle_profile_restore_checked");
                return;
            }

            if (stage == 6 && UnityEngine.Time.frameCount > stopFrame)
            {
                append("play_stop_requested", "watch_complete", raw, sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
                stage = 7;
                UnityEditor.EditorApplication.update -= tick;
                UnityEditor.EditorApplication.isPlaying = false;
            }
        }
        catch (System.Exception exception)
        {
            try { if (rig.AutoRender) rig.AutoRender = false; } catch { }
            try { if (renderer.autoRender) renderer.autoRender = false; } catch { }
            try { if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle) session.StopSession(); } catch { }
            try { session.startOnEnable = oldStartOnEnable; } catch { }
            try { UnityEditor.EditorApplication.update -= tick; } catch { }
            try { UnityEditor.EditorApplication.isPlaying = false; } catch { }
            stage = 7;
        }
    };

    UnityEditor.EditorApplication.update += tick;
    append("watcher_registered", "startOnEnable_temporarily_suppressed", prePlayRawBaseline, prePlayRawBaseline);
    return "watcher_registered";
})();
