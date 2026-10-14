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
    string treatmentGraphGuid = "1d49c3213be77a24493c6d2c87bde98d";
    string treatmentGraphPath = "Assets/ViewWeave/ValidatedVFXOverrides/SampleTexture2D.vfx";
    if (treatmentGraphGuid.Length != 32 || treatmentGraphPath != "Assets/ViewWeave/ValidatedVFXOverrides/SampleTexture2D.vfx")
        return "preflight_failed_treatment_identity_configuration";
    int activeEffectCount = 0;
    int selectedTreatmentCount = 0;
    bool selectedTreatmentReady = false;
    UnityEngine.VFX.VisualEffect selectedTreatmentEffect = null;
    for (int i = 0; i < visualEffects.Length; i++)
    {
        var effect = visualEffects[i];
        if (effect == null) continue;
        if (effect.enabled && effect.gameObject.activeInHierarchy) activeEffectCount++;
        if (effect.visualEffectAsset == null) continue;
        string effectPath = UnityEditor.AssetDatabase.GetAssetPath(effect.visualEffectAsset);
        string effectGuid = UnityEditor.AssetDatabase.AssetPathToGUID(effectPath);
        if (effectGuid.Equals(treatmentGraphGuid, System.StringComparison.OrdinalIgnoreCase))
        {
            selectedTreatmentCount++;
            selectedTreatmentEffect = effect;
            selectedTreatmentReady = effectPath == treatmentGraphPath && effect.enabled && effect.gameObject.activeInHierarchy && effect.allowInstancing;
        }
    }
    if (selectedTreatmentCount != 1 || !selectedTreatmentReady) return "preflight_failed_selected_treatment_effect";
    UnityEngine.Object selectedShowcase = null;
    UnityEngine.GameObject selectedShowcaseRoot = null;
    UnityEngine.GameObject selectedShowcaseCurrentPrefab = null;
    UnityEngine.Transform selectedShowcaseAnchor = null;
    System.Reflection.FieldInfo showcaseCurrentPrefabField = null;
    System.Reflection.FieldInfo showcaseSamplesPrefabsField = null;
    string selectedShowcaseRootGuid = "";
    string selectedShowcaseOriginalGuid = "";
    string selectedShowcaseRootPath = "";
    string selectedShowcaseOriginalPath = "";
    string selectedShowcaseRootEntityId = "";
    string selectedShowcaseAnchorEntityId = "";
    int selectedShowcaseSampleIndex = -1;
    if ("automated_camera_sweep" == "automated_camera_sweep")
    {
        System.Type showcaseType = null;
        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            showcaseType = assembly.GetType("SamplesShowcase", false);
            if (showcaseType != null) break;
        }
        if (showcaseType == null) return "preflight_failed_samples_showcase_type_missing";
        showcaseCurrentPrefabField = showcaseType.GetField("currentPrefab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        showcaseSamplesPrefabsField = showcaseType.GetField("samplesPrefabs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        if (showcaseCurrentPrefabField == null || showcaseSamplesPrefabsField == null)
            return "preflight_failed_samples_showcase_public_fields_missing";
        var showcaseObjects = UnityEngine.Resources.FindObjectsOfTypeAll(showcaseType);
        int qualifyingRootCount = 0;
        foreach (var showcase in showcaseObjects)
        {
            var showcaseComponent = showcase as UnityEngine.Component;
            if (showcaseComponent == null || !showcaseComponent.gameObject.scene.IsValid() || showcaseComponent.gameObject.scene.isLoaded == false) continue;
            var currentPrefab = showcaseCurrentPrefabField.GetValue(showcase) as UnityEngine.GameObject;
            var samplesValue = showcaseSamplesPrefabsField.GetValue(showcase) as System.Collections.IEnumerable;
            if (currentPrefab == null || samplesValue == null) continue;
            int sampleIndex = 0;
            foreach (var sampleValue in samplesValue)
            {
                var sampleRoot = sampleValue as UnityEngine.GameObject;
                bool containsSelectedEffect = sampleRoot != null &&
                    (selectedTreatmentEffect.transform == sampleRoot.transform || selectedTreatmentEffect.transform.IsChildOf(sampleRoot.transform));
                if (containsSelectedEffect)
                {
                    qualifyingRootCount++;
                    selectedShowcase = showcase;
                    selectedShowcaseRoot = sampleRoot;
                    selectedShowcaseCurrentPrefab = currentPrefab;
                    selectedShowcaseSampleIndex = sampleIndex;
                }
                sampleIndex++;
            }
        }
        if (qualifyingRootCount != 1 || selectedShowcase == null || selectedShowcaseRoot == null)
        {
            var effectParents = new System.Text.StringBuilder();
            var effectParent = selectedTreatmentEffect.transform;
            int effectParentDepth = 0;
            while (effectParent != null && effectParentDepth < 16)
            {
                if (effectParentDepth > 0) effectParents.Append(">");
                foreach (char value in effectParent.name)
                    effectParents.Append(value >= 32 && value <= 126 && value != '|' ? value : '?');
                effectParents.Append("#").Append(effectParent.GetEntityId().ToString());
                effectParent = effectParent.parent;
                effectParentDepth++;
            }
            return "preflight_failed_samples_showcase_root_not_unique,count=" + qualifyingRootCount +
                ",showcaseCount=" + showcaseObjects.Length + ",effectPath=" + treatmentGraphPath + ",effectGuid=" + treatmentGraphGuid + ",effectParents=" + effectParents;
        }
        var rootSource = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(selectedShowcaseRoot);
        var originalRootSource = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(selectedShowcaseRoot);
        selectedShowcaseRootPath = rootSource == null ? "" : UnityEditor.AssetDatabase.GetAssetPath(rootSource);
        selectedShowcaseOriginalPath = originalRootSource == null ? "" : UnityEditor.AssetDatabase.GetAssetPath(originalRootSource);
        selectedShowcaseRootGuid = string.IsNullOrEmpty(selectedShowcaseRootPath) ? "" : UnityEditor.AssetDatabase.AssetPathToGUID(selectedShowcaseRootPath);
        selectedShowcaseOriginalGuid = string.IsNullOrEmpty(selectedShowcaseOriginalPath) ? "" : UnityEditor.AssetDatabase.AssetPathToGUID(selectedShowcaseOriginalPath);
        int directViewPointCount = 0;
        foreach (var child in selectedShowcaseRoot.GetComponentsInChildren<UnityEngine.Transform>(true))
            if (child != null && child.parent == selectedShowcaseRoot.transform && child.name == "ViewPoint") directViewPointCount++;
        selectedShowcaseAnchor = selectedShowcaseRoot.transform.Find("ViewPoint");
        selectedShowcaseRootEntityId = selectedShowcaseRoot.GetEntityId().ToString();
        selectedShowcaseAnchorEntityId = selectedShowcaseAnchor == null ? "" : selectedShowcaseAnchor.GetEntityId().ToString();
        if (selectedShowcaseRootGuid != "3a08a8ebfe6ab4642b6f075038bdc1c3" ||
            selectedShowcaseOriginalGuid != "7837c49e89b12db43b2a59edba6bff6b" ||
            selectedShowcaseAnchor == null || directViewPointCount != 1)
            return "preflight_failed_samples_showcase_anchor_identity,rootPath=" + selectedShowcaseRootPath + ",rootGuid=" + selectedShowcaseRootGuid +
                ",originalPath=" + selectedShowcaseOriginalPath + ",originalGuid=" + selectedShowcaseOriginalGuid + ",rootEntityId=" + selectedShowcaseRootEntityId +
                ",currentPrefabEntityId=" + selectedShowcaseCurrentPrefab.GetEntityId().ToString() + ",anchorEntityId=" + selectedShowcaseAnchorEntityId +
                ",directViewPointCount=" + directViewPointCount + ",effectGuid=" + treatmentGraphGuid;
    }
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

    string logPath = "E:/LookingGlassSDK/.local/windows-urp-vfx-original-remedy-20261013/run-20261013T192759645Z/watcher.jsonl";
    string batchLogPath = "E:/LookingGlassSDK/.local/windows-urp-vfx-original-remedy-20261013/run-20261013T192759645Z/vfx-batch-sweep.jsonl";
    string stopPath = "E:/LookingGlassSDK/.local/windows-urp-vfx-original-remedy-20261013/run-20261013T192759645Z/stop.flag";
    string baselinePath = "E:/LookingGlassSDK/.local/windows-urp-vfx-original-remedy-20261013/run-20261013T192759645Z/normal-phase-baseline.txt";
    string cameraMotionMode = "automated_camera_sweep";
    if (cameraMotionMode != "manual_pan" && cameraMotionMode != "automated_camera_sweep") return "preflight_failed_camera_motion_mode";
    if (cameraMotionMode == "automated_camera_sweep" && renderer.referenceCamera == null) return "preflight_failed_reference_camera_missing";
    if (System.IO.File.Exists(logPath) || System.IO.File.Exists(batchLogPath) || System.IO.File.Exists(stopPath) || System.IO.File.Exists(baselinePath)) return "preflight_failed_evidence_exists";

    const long byteLimit = 3L * 1024L * 1024L * 1024L;
    const long warmupLimit = 4L * 1024L * 1024L * 1024L;
    const long stabilityRangeLimit = 16L * 1024L * 1024L;
    const double normalSeconds = 5.0;
    const int normalFrameLimit = 6000;
    const double normalSampleInterval = 0.13;
    const double playStartWaitSeconds = 30.0;
    const double connectSeconds = 10.0;
    const double readySeconds = 30.0;
    const long revisionLimit = 10000;
    const double closeSeconds = 10.0;
    const double cameraSweepSettleSeconds = 1.25;
    const double cameraSweepStableSeconds = 0.25;
    const double cameraCenterHoldMinimumSeconds = 0.5;
    const double cameraCenterHoldMaximumSeconds = 3.0;
    const int cameraCenterHoldMinimumCompletedRevisions = 3;
    const float cameraSweepStablePositionTolerance = 0.0005f;
    const float cameraSweepStableAngleTolerance = 0.05f;
    const float cameraSweepYawAmplitudeDegrees = 15.0f;
    const double cameraSweepCycleSeconds = 4.0;
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
    bool readyHistoryStartGate = false;
    bool readyHistoryEndGate = false;
    bool readyBatchStartGate = false;
    bool readyBatchEndGate = false;
    bool limitStopBatchSnapshotAttempted = false;
    bool limitStopBatchSnapshotPassed = false;
    bool cameraSweepPoseCaptured = false;
    bool cameraSweepStarted = false;
    bool cameraSweepRestored = cameraMotionMode == "manual_pan";
    bool cameraSweepLeftVisited = false;
    bool cameraSweepRightVisited = false;
    int cameraSweepStableFrames = 0;
    int cameraSweepPoseChangeCount = 0;
    int cameraSweepRevisionSampleCount = 0;
    int cameraSweepVisibleRevisionCount = 0;
    int cameraSweepLastAppliedFrame = -1;
    int cameraSweepLastUpdatedFrame = -1;
    int cameraSweepStableLastFrame = -1;
    long cameraSweepLastRevision = -1;
    double cameraSweepStableStart = -1.0;
    double cameraSweepStart = -1.0;
    double cameraCenterHoldStart = -1.0;
    double cameraCenterHoldFirstEligibleTime = -1.0;
    double cameraCenterHoldLastEligibleTime = -1.0;
    int cameraCenterHoldCompletedRevisionCount = 0;
    int cameraCenterHoldConsecutiveEligibleRevisionCount = 0;
    long cameraCenterHoldLastRevision = -1;
    bool cameraCenterHoldComplete = cameraMotionMode == "manual_pan";
    string cameraCenterHoldLastVisibility = "not_sampled";
    float cameraSweepYaw = 0.0f;
    float cameraSweepMinimumYaw = 0.0f;
    float cameraSweepMaximumYaw = 0.0f;
    string cameraSweepLastAppliedPose = "";
    float cameraSweepLastAppliedYaw = 0.0f;
    UnityEngine.Transform cameraSweepParent = null;
    UnityEngine.Vector3 cameraSweepOriginalLocalPosition = UnityEngine.Vector3.zero;
    UnityEngine.Quaternion cameraSweepOriginalLocalRotation = UnityEngine.Quaternion.identity;
    UnityEngine.Vector3 cameraSweepOriginalLocalScale = UnityEngine.Vector3.one;
    UnityEngine.Vector3 cameraSweepBaseLocalPosition = UnityEngine.Vector3.zero;
    UnityEngine.Quaternion cameraSweepBaseLocalRotation = UnityEngine.Quaternion.identity;
    string cameraSweepAnchorPath = "";
    UnityEngine.Vector3 cameraSweepPreviousLocalPosition = UnityEngine.Vector3.zero;
    UnityEngine.Quaternion cameraSweepPreviousLocalRotation = UnityEngine.Quaternion.identity;
    int readyHistoryViewCountStart = 0;
    int readyHistoryViewCountEnd = 0;
    int readyActiveVfxCountStart = 0;
    int readyActiveVfxCountEnd = 0;
    int readyUniqueVfxAssetCountStart = 0;
    int readyUniqueVfxAssetCountEnd = 0;
    long[] normalRawSamples = new long[3];
    double[] normalSampleTimes = new double[3];
    int[] normalSampleFrames = new int[3];
    int normalSampleCount = 0;
    long normalStableRawMinimum = 0;
    long normalStableRawMaximum = 0;
    double lastNormalSampleTime = -1.0;
    session.startOnEnable = false;

    System.Func<UnityEngine.Camera, string> cameraPose = camera =>
    {
        if (camera == null) return "null";
        var position = camera.transform.position;
        var rotation = camera.transform.rotation;
        return camera.GetEntityId().ToString() + ";p=" + position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + position.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
            ";q=" + rotation.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + rotation.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + rotation.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + rotation.w.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
            ";fov=" + camera.fieldOfView.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";near=" + camera.nearClipPlane.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";far=" + camera.farClipPlane.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    };
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
            "|sourceCameraPose=" + cameraPose(renderer.sourceCamera) + "|referenceCameraPose=" + cameraPose(renderer.referenceCamera) +
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

    if (cameraMotionMode == "automated_camera_sweep")
        append("camera_anchor_source_captured", "source=SamplesShowcase.samplesPrefabs,currentPrefabCaptured=True,currentPrefabEntityId=" + (selectedShowcaseCurrentPrefab == null ? "" : selectedShowcaseCurrentPrefab.GetEntityId().ToString()) +
            ",sampleIndex=" + selectedShowcaseSampleIndex + ",rootEntityId=" + selectedShowcaseRootEntityId +
            ",treatmentRootPath=" + selectedShowcaseRootPath + ",treatmentRootGuid=" + selectedShowcaseRootGuid +
            ",originalCellPath=" + selectedShowcaseOriginalPath + ",originalCellGuid=" + selectedShowcaseOriginalGuid +
            ",anchorPath=" + selectedShowcaseOriginalPath + "/ViewPoint,anchorEntityId=" + selectedShowcaseAnchorEntityId +
            ",effectGuid=" + treatmentGraphGuid,
            UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), 0);

    System.Func<string> selectedEffectFrustumState = () =>
    {
        var selectedEffect = selectedTreatmentEffect;
        var camera = renderer.referenceCamera;
        if (selectedEffect == null || camera == null) return "effectCount=" + (selectedEffect == null ? "0" : "1") + ",camera=false,renderer=false,visible=false";
        var effectRenderers = selectedEffect.GetComponentsInChildren<UnityEngine.Renderer>(true);
        var activeRenderers = new System.Collections.Generic.List<UnityEngine.Renderer>();
        foreach (var effectRenderer in effectRenderers)
            if (effectRenderer != null && effectRenderer.enabled && effectRenderer.gameObject.activeInHierarchy) activeRenderers.Add(effectRenderer);
        if (activeRenderers.Count == 0) return "effectCount=1,camera=true,renderer=false,visible=false,alive=" + selectedEffect.aliveParticleCount;
        var planes = UnityEngine.GeometryUtility.CalculateFrustumPlanes(camera);
        bool layerVisible = false;
        bool boundsVisible = false;
        var bounds = activeRenderers[0].bounds;
        for (int index = 0; index < activeRenderers.Count; index++)
        {
            var currentBounds = activeRenderers[index].bounds;
            if (index > 0) bounds.Encapsulate(currentBounds);
            bool currentLayerVisible = (camera.cullingMask & (1 << activeRenderers[index].gameObject.layer)) != 0;
            bool currentBoundsVisible = UnityEngine.GeometryUtility.TestPlanesAABB(planes, currentBounds);
            if (currentLayerVisible) layerVisible = true;
            if (currentLayerVisible && currentBoundsVisible) boundsVisible = true;
        }
        bool visible = selectedEffect.enabled && selectedEffect.gameObject.activeInHierarchy && layerVisible && boundsVisible && selectedEffect.aliveParticleCount > 0;
        var center = bounds.center;
        var size = bounds.size;
        return "effectCount=1,camera=true,renderer=true,layerVisible=" + layerVisible + ",boundsVisible=" + boundsVisible +
            ",visible=" + visible + ",alive=" + selectedEffect.aliveParticleCount +
            ",boundsCenter=" + center.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + center.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + center.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
            ",boundsSize=" + size.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + size.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + size.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    };

    System.Action<string> restoreCameraSweep = reason =>
    {
        if (cameraMotionMode == "manual_pan" || cameraSweepRestored) return;
        var camera = renderer.referenceCamera;
        if (!cameraSweepPoseCaptured || camera == null || camera.transform.parent != cameraSweepParent)
        {
            append("camera_sweep_restore_failed", "pose_not_captured_or_parent_changed," + reason, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
            return;
        }
        camera.transform.localPosition = cameraSweepOriginalLocalPosition;
        camera.transform.localRotation = cameraSweepOriginalLocalRotation;
        cameraSweepRestored = camera.transform.localPosition == cameraSweepOriginalLocalPosition &&
            UnityEngine.Quaternion.Angle(camera.transform.localRotation, cameraSweepOriginalLocalRotation) <= 0.001f &&
            camera.transform.localScale == cameraSweepOriginalLocalScale;
        append(cameraSweepRestored ? "camera_sweep_restored" : "camera_sweep_restore_failed",
            "reason=" + reason + ",leftVisited=" + cameraSweepLeftVisited + ",rightVisited=" + cameraSweepRightVisited +
            ",anchorPath=" + cameraSweepAnchorPath +
            ",poseChanges=" + cameraSweepPoseChangeCount + ",revisionSamples=" + cameraSweepRevisionSampleCount +
            ",visibleRevisionSamples=" + cameraSweepVisibleRevisionCount + ",minYaw=" + cameraSweepMinimumYaw.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            ",maxYaw=" + cameraSweepMaximumYaw.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            ",centerHoldComplete=" + cameraCenterHoldComplete + ",centerHoldElapsedSeconds=" + (cameraCenterHoldStart < 0.0 ? 0.0 : (cameraSweepStart >= cameraCenterHoldStart ? cameraSweepStart - cameraCenterHoldStart : UnityEditor.EditorApplication.timeSinceStartup - cameraCenterHoldStart)).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            ",centerHoldCompletedRevisions=" + cameraCenterHoldCompletedRevisionCount + ",centerHoldVisibleRevisions=" + cameraCenterHoldConsecutiveEligibleRevisionCount,
            UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
    };

    System.Func<double, bool> updateCameraSweep = now =>
    {
        if (cameraMotionMode == "manual_pan") return true;
        var camera = renderer.referenceCamera;
        if (camera == null)
        {
            append("camera_sweep_precondition_failed", "reference_camera_missing", UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            return false;
        }
        var transform = camera.transform;
        if (!cameraSweepStarted)
        {
            if (now - readyStart < cameraSweepSettleSeconds) return true;
            var localPosition = transform.localPosition;
            var localRotation = transform.localRotation;
            bool stable = cameraSweepStableLastFrame >= 0 && UnityEngine.Vector3.Distance(localPosition, cameraSweepPreviousLocalPosition) <= cameraSweepStablePositionTolerance &&
                UnityEngine.Quaternion.Angle(localRotation, cameraSweepPreviousLocalRotation) <= cameraSweepStableAngleTolerance;
            if (!stable)
            {
                cameraSweepStableStart = now;
                cameraSweepStableFrames = 1;
            }
            else if (UnityEngine.Time.frameCount != cameraSweepStableLastFrame) cameraSweepStableFrames++;
            cameraSweepPreviousLocalPosition = localPosition;
            cameraSweepPreviousLocalRotation = localRotation;
            cameraSweepStableLastFrame = UnityEngine.Time.frameCount;
            if (cameraSweepStableFrames < 3 || now - cameraSweepStableStart < cameraSweepStableSeconds) return true;
            cameraSweepParent = transform.parent;
            cameraSweepOriginalLocalPosition = transform.localPosition;
            cameraSweepOriginalLocalRotation = transform.localRotation;
            cameraSweepOriginalLocalScale = transform.localScale;
            cameraSweepPoseCaptured = true;
            var showcaseComponent = selectedShowcase as UnityEngine.Component;
            var runtimeCurrentPrefab = selectedShowcase == null || showcaseCurrentPrefabField == null ? null : showcaseCurrentPrefabField.GetValue(selectedShowcase) as UnityEngine.GameObject;
            var runtimeSamples = selectedShowcase == null || showcaseSamplesPrefabsField == null ? null : showcaseSamplesPrefabsField.GetValue(selectedShowcase) as System.Collections.IEnumerable;
            int runtimeSampleReferenceMatches = 0;
            int runtimeEffectContainingRootMatches = 0;
            int runtimeSampleIndex = 0;
            bool runtimeSelectedSampleIndexMatches = false;
            if (runtimeSamples != null)
                foreach (var sampleValue in runtimeSamples)
                {
                    var sampleRoot = sampleValue as UnityEngine.GameObject;
                    if (object.ReferenceEquals(sampleRoot, selectedShowcaseRoot))
                    {
                        runtimeSampleReferenceMatches++;
                        if (runtimeSampleIndex == selectedShowcaseSampleIndex) runtimeSelectedSampleIndexMatches = true;
                    }
                    if (sampleRoot != null && selectedTreatmentEffect != null &&
                        (selectedTreatmentEffect.transform == sampleRoot.transform || selectedTreatmentEffect.transform.IsChildOf(sampleRoot.transform))) runtimeEffectContainingRootMatches++;
                    runtimeSampleIndex++;
                }
            int runtimeDirectAnchorCount = 0;
            if (selectedShowcaseRoot != null)
                foreach (var child in selectedShowcaseRoot.GetComponentsInChildren<UnityEngine.Transform>(true))
                    if (child != null && child.parent == selectedShowcaseRoot.transform && child.name == "ViewPoint") runtimeDirectAnchorCount++;
            string runtimeEffectPath = selectedTreatmentEffect == null || selectedTreatmentEffect.visualEffectAsset == null ? "" : UnityEditor.AssetDatabase.GetAssetPath(selectedTreatmentEffect.visualEffectAsset);
            string runtimeEffectGuid = string.IsNullOrEmpty(runtimeEffectPath) ? "" : UnityEditor.AssetDatabase.AssetPathToGUID(runtimeEffectPath);
            bool rootReferenceValid = selectedShowcaseRoot != null && runtimeSampleReferenceMatches == 1 && runtimeEffectContainingRootMatches == 1 && runtimeSelectedSampleIndexMatches;
            bool effectDescendantValid = selectedTreatmentEffect != null && selectedShowcaseRoot != null &&
                (selectedTreatmentEffect.transform == selectedShowcaseRoot.transform || selectedTreatmentEffect.transform.IsChildOf(selectedShowcaseRoot.transform));
            bool anchorReferenceValid = selectedShowcaseAnchor != null && selectedShowcaseRoot != null &&
                selectedShowcaseAnchor.parent == selectedShowcaseRoot.transform && selectedShowcaseRoot.transform.Find("ViewPoint") == selectedShowcaseAnchor && runtimeDirectAnchorCount == 1;
            bool effectAssetValid = runtimeEffectPath == treatmentGraphPath && runtimeEffectGuid == treatmentGraphGuid;
            if (showcaseComponent == null || selectedShowcaseRoot == null || selectedShowcaseAnchor == null ||
                !rootReferenceValid || !effectDescendantValid || !anchorReferenceValid || !effectAssetValid ||
                selectedShowcaseRootGuid != "3a08a8ebfe6ab4642b6f075038bdc1c3" || selectedShowcaseOriginalGuid != "7837c49e89b12db43b2a59edba6bff6b")
            {
                var hierarchy = new System.Text.StringBuilder();
                var current = selectedTreatmentEffect == null ? null : selectedTreatmentEffect.transform;
                int hierarchyDepth = 0;
                while (current != null && hierarchyDepth < 16)
                {
                    if (hierarchyDepth > 0) hierarchy.Append(">");
                    foreach (char value in current.name)
                        hierarchy.Append(value >= 32 && value <= 126 && value != '|' ? value : '?');
                    hierarchy.Append("#").Append(current.GetEntityId().ToString());
                    current = current.parent;
                    hierarchyDepth++;
                }
                string cameraParent = transform.parent == null ? "null" : transform.parent.name + "#" + transform.parent.GetEntityId().ToString();
                string reason = "showcaseFound=" + (showcaseComponent != null) + ",currentPrefabSameAsCaptured=" + object.ReferenceEquals(runtimeCurrentPrefab, selectedShowcaseCurrentPrefab) +
                    ",samplesArrayMatches=" + runtimeSampleReferenceMatches + ",effectContainingRootMatches=" + runtimeEffectContainingRootMatches + ",sampleIndex=" + selectedShowcaseSampleIndex + ",sampleIndexMatches=" + runtimeSelectedSampleIndexMatches +
                    ",rootEntityId=" + (selectedShowcaseRoot == null ? "" : selectedShowcaseRoot.GetEntityId().ToString()) +
                    ",rootPath=" + selectedShowcaseRootPath + ",rootGuid=" + selectedShowcaseRootGuid + ",originalCellPath=" + selectedShowcaseOriginalPath + ",originalCellGuid=" + selectedShowcaseOriginalGuid +
                    ",effectDescendant=" + effectDescendantValid + ",directViewPointCount=" + runtimeDirectAnchorCount + ",anchorParentSame=" + (selectedShowcaseAnchor != null && selectedShowcaseRoot != null && selectedShowcaseAnchor.parent == selectedShowcaseRoot.transform) +
                    ",anchorEntityId=" + (selectedShowcaseAnchor == null ? "" : selectedShowcaseAnchor.GetEntityId().ToString()) +
                    ",effectPath=" + runtimeEffectPath + ",effectGuid=" + runtimeEffectGuid + ",effectHierarchy=" + hierarchy +
                    ",referenceCamera=" + camera.GetEntityId().ToString() + ",referenceCameraParent=" + cameraParent;
                append("camera_anchor_precondition_failed", reason, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
                return false;
            }
            string coldVisibility = selectedEffectFrustumState();
            append("camera_anchor_source_observed", "source=SamplesShowcase.samplesPrefabs,currentPrefabCaptured=True,currentPrefabSameAsCaptured=" + object.ReferenceEquals(runtimeCurrentPrefab, selectedShowcaseCurrentPrefab) +
                ",sampleIndex=" + selectedShowcaseSampleIndex + ",rootEntityId=" + selectedShowcaseRootEntityId + ",anchorEntityId=" + selectedShowcaseAnchorEntityId +
                ",treatmentRootGuid=" + selectedShowcaseRootGuid + ",treatmentRootPath=" + selectedShowcaseRootPath + ",originalCellGuid=" + selectedShowcaseOriginalGuid +
                ",originalCellPath=" + selectedShowcaseOriginalPath + ",anchorPath=ViewPoint,preAnchorEligibility=" + coldVisibility,
                UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            transform.SetPositionAndRotation(selectedShowcaseAnchor.position, selectedShowcaseAnchor.rotation);
            cameraSweepBaseLocalPosition = transform.localPosition;
            cameraSweepBaseLocalRotation = transform.localRotation;
            cameraSweepAnchorPath = selectedShowcaseOriginalPath + "/ViewPoint";
            cameraSweepStarted = true;
            cameraCenterHoldStart = now;
            cameraCenterHoldLastRevision = renderer.QuiltRevision;
            cameraCenterHoldLastVisibility = coldVisibility;
            cameraSweepLastUpdatedFrame = -1;
            cameraSweepLastRevision = readyStartRevision;
            append("camera_anchor_applied", "source=SamplesShowcase.samplesPrefabs,treatmentRootGuid=" + selectedShowcaseRootGuid + ",originalCellGuid=" + selectedShowcaseOriginalGuid + ",sampleIndex=" + selectedShowcaseSampleIndex + ",rootEntityId=" + selectedShowcaseRootEntityId + ",anchorEntityId=" + selectedShowcaseAnchorEntityId + ",anchorPath=" + cameraSweepAnchorPath + ",anchorWorldPose=" + cameraPose(camera) + ",previousLocalPose=" + cameraSweepOriginalLocalPosition.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + cameraSweepOriginalLocalPosition.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + cameraSweepOriginalLocalPosition.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",currentPrefabSameAsCaptured=" + object.ReferenceEquals(runtimeCurrentPrefab, selectedShowcaseCurrentPrefab), UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            append("camera_center_hold_started", "mode=automated_camera_sweep,minimumSeconds=" + cameraCenterHoldMinimumSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",maximumSeconds=" + cameraCenterHoldMaximumSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",minimumCompletedRevisions=" + cameraCenterHoldMinimumCompletedRevisions + ",startRevision=" + renderer.QuiltRevision +
                ",referenceCamera=" + cameraPose(camera), UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
        }
        if (transform.parent != cameraSweepParent)
        {
            append("camera_sweep_precondition_failed", "reference_camera_parent_changed,camera=" + camera.GetEntityId().ToString() +
                ",expectedParent=" + (cameraSweepParent == null ? "null" : cameraSweepParent.GetEntityId().ToString()) +
                ",actualParent=" + (transform.parent == null ? "null" : transform.parent.GetEntityId().ToString()),
                UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            return false;
        }
        if (!cameraCenterHoldComplete)
        {
            long centerRevision = renderer.QuiltRevision;
            if (centerRevision > cameraCenterHoldLastRevision && centerRevision > readyStartRevision)
            {
                cameraCenterHoldLastRevision = centerRevision;
                if (renderer.LastRenderedViewCount == 12)
                {
                    cameraCenterHoldCompletedRevisionCount++;
                    string centerVisibility = selectedEffectFrustumState();
                    bool centerEligible = centerVisibility.IndexOf(",visible=True,", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        centerVisibility.EndsWith(",visible=True", System.StringComparison.OrdinalIgnoreCase);
                    cameraCenterHoldLastVisibility = centerVisibility;
                    if (centerEligible)
                    {
                        if (cameraCenterHoldFirstEligibleTime < 0.0) cameraCenterHoldFirstEligibleTime = now;
                        cameraCenterHoldLastEligibleTime = now;
                        cameraCenterHoldConsecutiveEligibleRevisionCount++;
                    }
                    else
                    {
                        cameraCenterHoldFirstEligibleTime = -1.0;
                        cameraCenterHoldLastEligibleTime = -1.0;
                        cameraCenterHoldConsecutiveEligibleRevisionCount = 0;
                    }
                    double eligibleSpan = cameraCenterHoldFirstEligibleTime < 0.0 ? 0.0 : cameraCenterHoldLastEligibleTime - cameraCenterHoldFirstEligibleTime;
                    append("camera_center_hold_revision", "revision=" + centerRevision + ",frame=" + UnityEngine.Time.frameCount +
                        ",elapsedSeconds=" + (now - cameraCenterHoldStart).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        ",completedRevisionCount=" + cameraCenterHoldCompletedRevisionCount + ",visibleRevisionCount=" + cameraCenterHoldConsecutiveEligibleRevisionCount +
                        ",eligibleSpanSeconds=" + eligibleSpan.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",visibilityEligible=" + centerEligible + "," + centerVisibility,
                        UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
                    if (cameraCenterHoldCompletedRevisionCount >= cameraCenterHoldMinimumCompletedRevisions &&
                        cameraCenterHoldConsecutiveEligibleRevisionCount >= cameraCenterHoldMinimumCompletedRevisions &&
                        eligibleSpan >= cameraCenterHoldMinimumSeconds &&
                        now - cameraCenterHoldStart < cameraCenterHoldMaximumSeconds)
                    {
                        cameraCenterHoldComplete = true;
                        double holdElapsed = now - cameraCenterHoldStart;
                        cameraSweepStart = now;
                        cameraSweepLastRevision = centerRevision;
                        append("camera_center_hold_completed", "elapsedSeconds=" + holdElapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                            ",completedRevisionCount=" + cameraCenterHoldCompletedRevisionCount + ",visibleRevisionCount=" + cameraCenterHoldConsecutiveEligibleRevisionCount +
                            ",eligibleSpanSeconds=" + eligibleSpan.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",lastVisibility=" + centerVisibility,
                            UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
                        append("camera_sweep_started", "mode=automated_camera_sweep,centerHoldSeconds=" + holdElapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                            ",centerHoldCompletedRevisions=" + cameraCenterHoldCompletedRevisionCount + ",centerHoldVisibleRevisions=" + cameraCenterHoldConsecutiveEligibleRevisionCount +
                            ",centerHoldEligibleSpanSeconds=" + eligibleSpan.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",referenceCamera=" + cameraPose(camera),
                            UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
                        return true;
                    }
                }
            }
            double centerElapsed = now - cameraCenterHoldStart;
            if (centerElapsed >= cameraCenterHoldMaximumSeconds)
            {
                double eligibleSpan = cameraCenterHoldFirstEligibleTime < 0.0 ? 0.0 : cameraCenterHoldLastEligibleTime - cameraCenterHoldFirstEligibleTime;
                append("camera_center_hold_failed", "elapsedSeconds=" + centerElapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    ",completedRevisionCount=" + cameraCenterHoldCompletedRevisionCount + ",visibleRevisionCount=" + cameraCenterHoldConsecutiveEligibleRevisionCount +
                    ",eligibleSpanSeconds=" + eligibleSpan.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",lastVisibility=" + cameraCenterHoldLastVisibility,
                    UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
                return false;
            }
            return true;
        }
        if (renderer.QuiltRevision > cameraSweepLastRevision && renderer.QuiltRevision > readyStartRevision && renderer.LastRenderedViewCount == 12 && cameraSweepLastAppliedFrame >= 0)
        {
            string visibility = selectedEffectFrustumState();
            bool visible = visibility.IndexOf(",visible=True,", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                visibility.EndsWith(",visible=True", System.StringComparison.OrdinalIgnoreCase);
            cameraSweepRevisionSampleCount++;
            if (visible) cameraSweepVisibleRevisionCount++;
            cameraSweepLastRevision = renderer.QuiltRevision;
            var observedRelativeRotation = UnityEngine.Quaternion.Inverse(cameraSweepBaseLocalRotation) * transform.localRotation;
            float observedLocalYaw = UnityEngine.Mathf.DeltaAngle(0.0f, observedRelativeRotation.eulerAngles.y);
            append("camera_sweep_revision_sample", "mode=automated_camera_sweep,revisionObservedFrame=" + UnityEngine.Time.frameCount +
                ",lastAppliedYaw=" + cameraSweepLastAppliedYaw.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",lastAppliedPose=" + cameraSweepLastAppliedPose + ",cameraPoseAtRevisionObservation=" + cameraPose(camera) +
                ",localYawAtRevisionObservation=" + observedLocalYaw.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",poseChangeCount=" + cameraSweepPoseChangeCount + ",visibilityEligible=" + visible + "," + visibility,
                UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
        }
        if (UnityEngine.Time.frameCount != cameraSweepLastUpdatedFrame)
        {
            double phase = (now - cameraSweepStart) % cameraSweepCycleSeconds;
            double halfCycle = cameraSweepCycleSeconds * 0.5;
            float yaw = phase < halfCycle
                ? UnityEngine.Mathf.Lerp(-cameraSweepYawAmplitudeDegrees, cameraSweepYawAmplitudeDegrees, (float)(phase / halfCycle))
                : UnityEngine.Mathf.Lerp(cameraSweepYawAmplitudeDegrees, -cameraSweepYawAmplitudeDegrees, (float)((phase - halfCycle) / halfCycle));
            cameraSweepYaw = yaw;
            if (yaw < -1.0f) cameraSweepLeftVisited = true;
            if (yaw > 1.0f) cameraSweepRightVisited = true;
            if (cameraSweepPoseChangeCount == 0 || UnityEngine.Quaternion.Angle(transform.localRotation, cameraSweepBaseLocalRotation * UnityEngine.Quaternion.AngleAxis(yaw, UnityEngine.Vector3.up)) > 0.05f)
                cameraSweepPoseChangeCount++;
            transform.localPosition = cameraSweepBaseLocalPosition;
            transform.localRotation = cameraSweepBaseLocalRotation * UnityEngine.Quaternion.AngleAxis(yaw, UnityEngine.Vector3.up);
            cameraSweepMinimumYaw = System.Math.Min(cameraSweepMinimumYaw, yaw);
            cameraSweepMaximumYaw = System.Math.Max(cameraSweepMaximumYaw, yaw);
            cameraSweepLastAppliedYaw = yaw;
            cameraSweepLastAppliedPose = cameraPose(camera);
            cameraSweepLastAppliedFrame = UnityEngine.Time.frameCount;
            cameraSweepLastUpdatedFrame = UnityEngine.Time.frameCount;
        }
        return true;
    };

    System.Func<string, bool> captureHistoryAA = label =>
    {
        try
        {
            var source = renderer.sourceCamera == null ? null : renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>();
            if (source == null) { append("history_aa_snapshot", label + ",source_guard_missing", UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline); return false; }
            var historyField = typeof(ViewWeave.URP.UrpQuiltCamera).GetField("historyRenderer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object historyRenderer = historyField == null ? null : historyField.GetValue(source);
            var viewsField = historyRenderer == null ? null : historyRenderer.GetType().GetField("views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var views = viewsField == null ? null : viewsField.GetValue(historyRenderer) as System.Collections.IDictionary;
            int layoutViews = output.Profile.IsValid ? output.Profile.Columns * output.Profile.Rows : -1;
            int renderedViews = renderer.LastRenderedViewCount;
            var renderedField = typeof(ViewWeave.QuiltRenderer).GetField("renderedViewIndices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var renderedIndices = renderedField == null ? null : renderedField.GetValue(renderer) as int[];
            if (views == null || layoutViews != 48 || renderedViews != 12 || views.Count != 12 || renderedIndices == null || renderedIndices.Length != 12)
            {
                append("history_aa_snapshot", label + ",layoutViews=" + layoutViews + ",renderedViews=" + renderedViews + ",mapCount=" + (views == null ? -1 : views.Count) + ",selectedIndexCount=" + (renderedIndices == null ? -1 : renderedIndices.Length), UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
                return false;
            }
            var entries = new System.Collections.Generic.List<System.Collections.DictionaryEntry>();
            foreach (System.Collections.DictionaryEntry entry in views) entries.Add(entry);
            entries.Sort((left, right) => System.Convert.ToInt32(left.Key, System.Globalization.CultureInfo.InvariantCulture).CompareTo(System.Convert.ToInt32(right.Key, System.Globalization.CultureInfo.InvariantCulture)));
            var ids = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            var historyIndices = new System.Collections.Generic.HashSet<int>();
            var selectedIndices = new System.Collections.Generic.HashSet<int>(renderedIndices);
            var modes = new System.Collections.Generic.List<string>();
            bool allTaa = true;
            foreach (System.Collections.DictionaryEntry entry in entries)
            {
                int logicalView = System.Convert.ToInt32(entry.Key, System.Globalization.CultureInfo.InvariantCulture);
                object view = entry.Value;
                var cameraField = view == null ? null : view.GetType().GetField("camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var historyCamera = cameraField == null ? null : cameraField.GetValue(view) as UnityEngine.Camera;
                var data = historyCamera == null ? null : historyCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                string id = historyCamera == null ? "missing" : historyCamera.GetEntityId().ToString();
                string mode = data == null ? "missing" : data.antialiasing.ToString();
                if (historyCamera == null || data == null || logicalView < 0 || logicalView >= layoutViews || !historyIndices.Add(logicalView) || !ids.Add(id) || mode != "TemporalAntiAliasing") allTaa = false;
                modes.Add(logicalView + ":" + id + ":" + mode);
            }
            bool indicesMatch = historyIndices.SetEquals(selectedIndices);
            append("history_aa_snapshot", label + ",layoutViews=" + layoutViews + ",renderedViews=" + renderedViews + ",mapCount=" + views.Count + ",viewCount=" + entries.Count + ",distinctCameraCount=" + ids.Count + ",indicesMatch=" + indicesMatch + ",logicalIndices=" + System.String.Join(";", historyIndices) + ",selectedIndices=" + System.String.Join(";", selectedIndices) + ",allTemporalAA=" + allTaa + ",modes=" + System.String.Join(";", modes), UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            if (label == "ready_start") readyHistoryViewCountStart = entries.Count;
            if (label == "ready_end") readyHistoryViewCountEnd = entries.Count;
            return allTaa && entries.Count == 12 && ids.Count == 12 && historyIndices.Count == 12 && indicesMatch;
        }
        catch (System.Exception exception)
        {
            append("history_aa_snapshot", label + ",exception=" + exception.GetType().Name, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            return false;
        }
    };

    System.Func<string, bool> captureBatchSweep = label =>
    {
        try
        {
            var currentEffects = UnityEngine.Object.FindObjectsByType<UnityEngine.VFX.VisualEffect>(UnityEngine.FindObjectsInactive.Include);
            var active = new System.Collections.Generic.List<UnityEngine.VFX.VisualEffect>();
            foreach (var effect in currentEffects)
                if (effect != null && effect.enabled && effect.gameObject.activeInHierarchy && effect.visualEffectAsset != null) active.Add(effect);
            var grouped = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<UnityEngine.VFX.VisualEffect>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var effect in active)
            {
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(effect.visualEffectAsset);
                string guid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
                if (System.String.IsNullOrEmpty(guid)) continue;
                System.Collections.Generic.List<UnityEngine.VFX.VisualEffect> group;
                if (!grouped.TryGetValue(guid, out group)) { group = new System.Collections.Generic.List<UnityEngine.VFX.VisualEffect>(); grouped.Add(guid, group); }
                group.Add(effect);
            }
            var method = typeof(UnityEngine.VFX.VFXManager).GetMethod("GetBatchedEffectInfo", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(UnityEngine.VFX.VisualEffectAsset) }, null);
            bool allAvailable = method != null && active.Count == 32 && grouped.Count == 32;
            bool selectedGate = false;
            bool selectedAllowInstancing = false;
            long selectedGpu = -1, selectedCpu = -1, selectedCapacity = -1, selectedBatchCapacity = -1, selectedUnbatched = -1, selectedActiveInstances = -1;
            foreach (var pair in grouped)
            {
                var effect = pair.Value[0];
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(effect.visualEffectAsset);
                string assetName = effect.visualEffectAsset.name;
                bool componentFlagsMatch = true;
                for (int componentIndex = 1; componentIndex < pair.Value.Count; componentIndex++)
                    if (pair.Value[componentIndex].allowInstancing != effect.allowInstancing) componentFlagsMatch = false;
                bool selected = pair.Key.Equals(treatmentGraphGuid, System.StringComparison.OrdinalIgnoreCase);
                string infoType = "unavailable", totalGpu = "unavailable", totalCpu = "unavailable", activeBatch = "unavailable", inactiveBatch = "unavailable", activeInstances = "unavailable", unbatched = "unavailable", totalCapacity = "unavailable", maxBatchCapacity = "unavailable", failureText = "";
                if (method == null) failureText = "api-unavailable";
                else
                {
                    object info = null;
                    try
                    {
                        info = method.Invoke(null, new object[] { effect.visualEffectAsset });
                        if (info == null) failureText = "returned-null";
                        else
                        {
                            var infoTypeValue = info.GetType();
                            infoType = infoTypeValue.FullName ?? infoTypeValue.Name;
                            System.Func<string, string> read = fieldName =>
                            {
                                var property = infoTypeValue.GetProperty(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (property != null && property.GetIndexParameters().Length == 0) return System.Convert.ToString(property.GetValue(info, null), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                                var field = infoTypeValue.GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                return field == null ? "unavailable" : System.Convert.ToString(field.GetValue(info), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                            };
                            totalGpu = read("totalGPUSizeInBytes"); totalCpu = read("totalCPUSizeInBytes");
                            activeBatch = read("activeBatchCount"); inactiveBatch = read("inactiveBatchCount"); activeInstances = read("activeInstanceCount");
                            unbatched = read("unbatchedInstanceCount"); totalCapacity = read("totalInstanceCapacity"); maxBatchCapacity = read("maxInstancePerBatchCapacity");
                            if (totalGpu == "unavailable" || totalCpu == "unavailable" || activeBatch == "unavailable" || inactiveBatch == "unavailable" || activeInstances == "unavailable" || unbatched == "unavailable" || totalCapacity == "unavailable" || maxBatchCapacity == "unavailable") failureText = "one-or-more-fields-unavailable";
                        }
                    }
                    catch (System.Exception exception) { failureText = exception.GetType().Name; }
                    finally { var disposable = info as System.IDisposable; if (disposable != null) try { disposable.Dispose(); } catch { failureText = "dispose-failed"; } }
                }
                if (failureText.Length > 0) allAvailable = false;
                System.IO.File.AppendAllText(batchLogPath,
                    "event=batch_info|snapshot=" + label + "|guid=" + pair.Key + "|asset=" + assetName + "|path=" + assetPath + "|components=" + pair.Value.Count +
                    "|allowInstancing=" + effect.allowInstancing + "|componentFlagsMatch=" + componentFlagsMatch + "|infoType=" + infoType + "|totalGPUSizeInBytes=" + totalGpu + "|totalCPUSizeInBytes=" + totalCpu +
                    "|activeBatchCount=" + activeBatch + "|inactiveBatchCount=" + inactiveBatch + "|activeInstanceCount=" + activeInstances + "|unbatchedInstanceCount=" + unbatched + "|totalInstanceCapacity=" + totalCapacity + "|maxInstancePerBatchCapacity=" + maxBatchCapacity + "|error=" + failureText + System.Environment.NewLine,
                    System.Text.Encoding.ASCII);
                if (selected)
                {
                    selectedAllowInstancing = effect.allowInstancing;
                    long.TryParse(totalGpu, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out selectedGpu);
                    long.TryParse(totalCpu, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out selectedCpu);
                    long.TryParse(totalCapacity, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out selectedCapacity);
                    long.TryParse(maxBatchCapacity, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out selectedBatchCapacity);
                    long.TryParse(unbatched, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out selectedUnbatched);
                    long.TryParse(activeInstances, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out selectedActiveInstances);
                    bool allowTrue = pair.Value.Count == 1 && selectedAllowInstancing;
                    selectedGate = allowTrue && componentFlagsMatch && selectedGpu >= 0 && selectedCpu >= 0 && selectedCapacity == 1 && selectedBatchCapacity == 1 && selectedUnbatched == 1 && selectedActiveInstances > 0 && failureText.Length == 0;
                }
            }
            if (label == "ready_start") { readyActiveVfxCountStart = active.Count; readyUniqueVfxAssetCountStart = grouped.Count; readyBatchStartGate = allAvailable && selectedGate; }
            if (label == "ready_end") { readyActiveVfxCountEnd = active.Count; readyUniqueVfxAssetCountEnd = grouped.Count; readyBatchEndGate = allAvailable && selectedGate; }
            append("batch_sweep", label + ",activeComponents=" + active.Count + ",uniqueAssets=" + grouped.Count + ",allAvailable=" + allAvailable + ",selectedTreatmentGuid=" + treatmentGraphGuid + ",selectedTreatmentPath=" + treatmentGraphPath + ",selectedTreatmentAllowInstancing=" + selectedAllowInstancing + ",selectedTreatmentGate=" + selectedGate + ",selectedGPU=" + selectedGpu + ",selectedCPU=" + selectedCpu + ",selectedCapacity=" + selectedCapacity + ",selectedBatchCapacity=" + selectedBatchCapacity + ",selectedUnbatched=" + selectedUnbatched + ",selectedActiveInstances=" + selectedActiveInstances,
                UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            return active.Count == 32 && grouped.Count == 32 && allAvailable && selectedGate;
        }
        catch (System.Exception exception)
        {
            append("batch_sweep", label + ",exception=" + exception.GetType().Name, UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
            return false;
        }
    };

    System.Action<string> captureStopBatchSnapshot = reason =>
    {
        if (limitStopBatchSnapshotAttempted) return;
        bool shouldCapture = reason.IndexOf("limit", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("external_private_limit_or_timeout", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("fault", System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (!shouldCapture) return;
        limitStopBatchSnapshotAttempted = true;
        limitStopBatchSnapshotPassed = captureBatchSweep("limit_stop_before_cleanup");
        append("limit_stop_batch_snapshot", "reason=" + reason + ",snapshotPassed=" + limitStopBatchSnapshotPassed +
            ",snapshotAttempted=" + limitStopBatchSnapshotAttempted,
            UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver(), sessionRawBaseline);
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
        captureStopBatchSnapshot(reason);
        restoreCameraSweep(reason);
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
        if (stage == 6 || stage == 7) return;
        captureStopBatchSnapshot(reason);
        restoreCameraSweep(reason);
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
                if (now - playStart >= normalSeconds || now - sessionStart >= 0.75)
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
                    append("connecting_limit", raw <= 0 ? "raw_counter_unavailable" : "raw_growth_limit_3gib_from_normal_baseline", raw, sessionRawBaseline);
                    requestSessionStop(raw <= 0 ? "raw_counter_unavailable" : "connecting_raw_growth_limit_3gib");
                    return;
                }
                if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(renderer.LastAutoRenderError) || session.State == ViewWeave.ViewWeaveBridgeSession.SessionState.Fault)
                {
                    append("connecting_fault", "error_present", raw, sessionRawBaseline);
                    requestSessionStop("bridge_connecting_fault");
                    return;
                }
                if (session.State == ViewWeave.ViewWeaveBridgeSession.SessionState.Rendering && output.Ready && output.Profile.IsValid &&
                    renderer.QuiltRevision > playStartRevision && renderer.LastRenderedViewCount == 12)
                {
                    bool historyStart = captureHistoryAA("ready_start");
                    bool batchStart = captureBatchSweep("ready_start");
                    if (!historyStart || !batchStart)
                    {
                        append("ready_start_evidence_gate_failed", "historyAA=" + historyStart + ",batchSweep=" + batchStart, raw, sessionRawBaseline);
                        requestSessionStop("ready_start_evidence_gate_failed");
                        return;
                    }
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
                    append("ready_limit", raw <= 0 ? "raw_counter_unavailable" : "raw_growth_limit_3gib_from_normal_baseline", raw, sessionRawBaseline);
                    requestSessionStop(raw <= 0 ? "raw_counter_unavailable" : "ready_raw_growth_limit_3gib");
                    return;
                }
                if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(renderer.LastAutoRenderError) || session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Rendering || !output.Ready)
                {
                    append("ready_fault", "session_or_render_error", raw, sessionRawBaseline);
                    requestSessionStop("bridge_ready_fault");
                    return;
                }
                if (!updateCameraSweep(now))
                {
                    append("camera_sweep_update_failed", "camera_or_anchor_resolution_failed", raw, sessionRawBaseline);
                    requestSessionStop("camera_sweep_update_failed");
                    return;
                }
                if (UnityEngine.Time.frameCount != lastSampleFrame && (UnityEngine.Time.frameCount % 6 == 0 || delta >= byteLimit))
                {
                    append("bridge_ready_sample", "", raw, sessionRawBaseline);
                    lastSampleFrame = UnityEngine.Time.frameCount;
                }
                if (now - readyStart >= readySeconds || renderer.QuiltRevision - readyStartRevision >= revisionLimit)
                {
                    bool historyEnd = captureHistoryAA("ready_end");
                    bool batchEnd = captureBatchSweep("ready_end");
                    if (!historyEnd || !batchEnd)
                    {
                        append("ready_end_evidence_gate_failed", "historyAA=" + historyEnd + ",batchSweep=" + batchEnd, raw, sessionRawBaseline);
                        requestSessionStop("ready_end_evidence_gate_failed");
                    }
                    else requestSessionStop("bounded_ready_window_complete");
                    return;
                }
                return;
            }

            if (stage == 5)
            {
                if (raw > 0 && raw - sessionRawBaseline >= byteLimit)
                {
                    append("closing_limit", "raw_growth_limit_3gib_from_normal_baseline", raw, sessionRawBaseline);
                    captureStopBatchSnapshot("closing_raw_growth_limit_3gib");
                    finishPlay("closing_raw_growth_limit_3gib");
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
                append("watcher_unsubscribed", "watch_complete", raw, sessionRawBaseline == 0 ? playRawBaseline : sessionRawBaseline);
                stage = 7;
                UnityEditor.EditorApplication.update -= tick;
                UnityEditor.EditorApplication.isPlaying = false;
            }
        }
        catch (System.Exception exception)
        {
            try { restoreCameraSweep("watcher_exception"); } catch { }
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
