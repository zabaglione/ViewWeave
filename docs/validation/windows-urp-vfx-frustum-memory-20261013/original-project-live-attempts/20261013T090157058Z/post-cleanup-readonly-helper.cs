    const string outputPath = "E:/LookingGlassSDK/.local/windows-urp-vfx-live-play-20261013/run-20261013T090157058Z/post-run-readonly.json";
    var sb = new System.Text.StringBuilder(32768);
    System.Func<string, string> q = value =>
    {
        if (value == null) return "null";
        var escaped = new System.Text.StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') escaped.Append("\\\"");
            else if (c == '\\') escaped.Append("\\\\");
            else if (c == '\n') escaped.Append("\\n");
            else if (c == '\r') escaped.Append("\\r");
            else if (c == '\t') escaped.Append("\\t");
            else if (c < 32 || c > 126) escaped.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else escaped.Append(c);
        }
        return "\"" + escaped.ToString() + "\"";
    };
    System.Func<float, string> f = value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    System.Func<string, string> safeError = value =>
    {
        if (string.IsNullOrEmpty(value)) return "null";
        if (value.Length > 240) value = value.Substring(0, 240);
        var safe = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++) safe.Append(value[i] >= 32 && value[i] <= 126 ? value[i] : '?');
        return q(safe.ToString());
    };
    System.Func<UnityEngine.Vector3, string> v3 = value => "[" + f(value.x) + "," + f(value.y) + "," + f(value.z) + "]";
    System.Func<UnityEngine.Camera, string> cameraJson = camera =>
    {
        if (camera == null) return "null";
        var data = camera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        return "{\"name\":" + q(camera.name) + ",\"enabled\":" + camera.enabled + ",\"activeInHierarchy\":" + camera.gameObject.activeInHierarchy +",\"position\":" + v3(camera.transform.position) + ",\"forward\":" + v3(camera.transform.forward) + ",\"fieldOfView\":" + f(camera.fieldOfView) + ",\"aspect\":" + f(camera.aspect) + ",\"near\":" + f(camera.nearClipPlane) + ",\"far\":" + f(camera.farClipPlane) + ",\"allowHDR\":" + camera.allowHDR + ",\"allowMSAA\":" + (data == null ? "null" : camera.allowMSAA.ToString()) + ",\"renderPostProcessing\":" + (data == null ? "null" : data.renderPostProcessing.ToString()) + ",\"antialiasing\":" + (data == null ? "null" : q(data.antialiasing.ToString())) + "}";
    };

    var renderers = UnityEngine.Object.FindObjectsByType<ViewWeave.QuiltRenderer>(UnityEngine.FindObjectsInactive.Include);
    if (renderers.Length != 1) throw new System.InvalidOperationException("Unexpected QuiltRenderer count: " + renderers.Length);
    var renderer = renderers[0];
    var rig = renderer.GetComponent<ViewWeave.ViewWeaveRig>();
    var session = renderer.GetComponent<ViewWeave.ViewWeaveBridgeSession>();
    var output = renderer.GetComponent<ViewWeave.BridgeWorkerQuiltOutput>();
    var urpCamera = renderer.sourceCamera == null ? null : renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>();
    var activeCameraList = UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(UnityEngine.FindObjectsInactive.Exclude);
    var planes = new System.Collections.Generic.Dictionary<UnityEngine.Camera, UnityEngine.Plane[]>();
    for (int i = 0; i < activeCameraList.Length; i++)
        if (activeCameraList[i] != null && activeCameraList[i].enabled)
            planes[activeCameraList[i]] = UnityEngine.GeometryUtility.CalculateFrustumPlanes(activeCameraList[i]);

    var visualEffects = UnityEngine.Object.FindObjectsByType<UnityEngine.VFX.VisualEffect>(UnityEngine.FindObjectsInactive.Include);
    var activeEffects = new System.Collections.Generic.List<UnityEngine.VFX.VisualEffect>();
    var uniqueAssets = new System.Collections.Generic.List<UnityEngine.VFX.VisualEffectAsset>();
    var activeCountByAsset = new System.Collections.Generic.Dictionary<UnityEngine.VFX.VisualEffectAsset, int>();
    for (int i = 0; i < visualEffects.Length; i++)
    {
        var effect = visualEffects[i];
        if (effect == null || !effect.enabled || !effect.gameObject.activeInHierarchy || effect.visualEffectAsset == null) continue;
        activeEffects.Add(effect);
        var asset = effect.visualEffectAsset;
        int count;
        activeCountByAsset.TryGetValue(asset, out count);
        activeCountByAsset[asset] = count + 1;
        if (!uniqueAssets.Contains(asset)) uniqueAssets.Add(asset);
    }

    sb.Append("{\"capturedUtc\":").Append(q(System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
    sb.Append(",\"editor\":{\"isPlaying\":").Append(UnityEditor.EditorApplication.isPlaying);
    sb.Append(",\"frame\":").Append(UnityEngine.Time.frameCount);
    sb.Append(",\"rawGraphicsBytes\":").Append(UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver());
    sb.Append(",\"sessionState\":").Append(q(session == null ? "missing" : session.State.ToString()));
    sb.Append(",\"workerState\":").Append(q(output == null ? "missing" : output.State.ToString()));
    sb.Append(",\"shutdownPending\":").Append(ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount);
    sb.Append(",\"historyCameraCount\":").Append(urpCamera == null ? -1 : urpCamera.HistoryCameraCount);
    sb.Append(",\"quiltTexture\":").Append(renderer.QuiltTexture == null ? "null" : "{\"width\":" + renderer.QuiltTexture.width + ",\"height\":" + renderer.QuiltTexture.height + ",\"format\":" + q(renderer.QuiltTexture.graphicsFormat.ToString()) + ",\"created\":" + renderer.QuiltTexture.IsCreated() + "}");
    sb.Append(",\"autoRender\":").Append(renderer.autoRender);
    sb.Append(",\"rigAutoRender\":").Append(rig != null && rig.AutoRender);
    sb.Append(",\"startOnEnable\":").Append(session != null && session.startOnEnable);
    sb.Append(",\"lastAutoRenderErrorPresent\":").Append(renderer.LastAutoRenderError != null && renderer.LastAutoRenderError.Length != 0);
    sb.Append(",\"lastAutoRenderError\":").Append(safeError(renderer.LastAutoRenderError));
    sb.Append(",\"sessionErrorPresent\":").Append(session != null && session.LastError != null && session.LastError.Length != 0);
    sb.Append(",\"sessionError\":").Append(safeError(session == null ? null : session.LastError));
    sb.Append(",\"qualityPipeline\":").Append(q(UnityEngine.QualitySettings.renderPipeline == null ? "null" : UnityEngine.QualitySettings.renderPipeline.GetType().FullName));
    sb.Append(",\"currentPipeline\":").Append(q(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null ? "null" : UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().FullName));
    sb.Append(",\"tileTaaDuringReady\":\"not-recorded-by-as-run-watcher; post-cleanup history is destroyed\"");
    sb.Append(",\"quilt\":{\"viewRenderMode\":").Append(q(renderer.viewRenderMode.ToString()));
    sb.Append(",\"settings\":{\"width\":").Append(renderer.settings.quiltWidth).Append(",\"height\":").Append(renderer.settings.quiltHeight).Append(",\"columns\":").Append(renderer.settings.columns).Append(",\"rows\":").Append(renderer.settings.rows).Append(",\"views\":").Append(renderer.settings.views).Append(",\"cameraDistance\":").Append(f(renderer.settings.cameraDistance)).Append(",\"fieldOfView\":").Append(f(renderer.settings.fieldOfView)).Append(",\"viewCone\":").Append(f(renderer.settings.viewCone)).Append("}");
    sb.Append(",\"sourceCamera\":").Append(cameraJson(renderer.sourceCamera));
    sb.Append(",\"referenceCamera\":").Append(cameraJson(renderer.referenceCamera)).Append("}");
    sb.Append(",\"packages\":{");
    var corePackage = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ViewWeave.QuiltRenderer).Assembly);
    var urpPackage = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityEngine.Rendering.Universal.UniversalAdditionalCameraData).Assembly);
    var vfxPackage = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.unity.visualeffectgraph");
    System.Func<UnityEditor.PackageManager.PackageInfo, string> packageJson = package => package == null ? "null" : "{\"name\":" + q(package.name) + ",\"version\":" + q(package.version) + ",\"resolvedPath\":" + q(package.resolvedPath) + "}";
    sb.Append("\"core\":").Append(packageJson(corePackage)).Append(",\"urp\":").Append(packageJson(urpPackage)).Append(",\"vfxGraph\":").Append(packageJson(vfxPackage)).Append("}");
    sb.Append(",\"camerasEnabled\":[");
    bool first = true;
    foreach (var camera in planes.Keys)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append(cameraJson(camera));
    }
    sb.Append("]");
    sb.Append(",\"activeEffects\":[");
    for (int i = 0; i < activeEffects.Count; i++)
    {
        if (i != 0) sb.Append(',');
        var effect = activeEffects[i];
        var asset = effect.visualEffectAsset;
        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
        string assetGuid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
        var effectRenderer = effect.GetComponent<UnityEngine.Renderer>();
        sb.Append("{\"gameObject\":").Append(q(effect.gameObject.name));
        sb.Append(",\"assetName\":").Append(q(asset.name));
        sb.Append(",\"assetPath\":").Append(q(assetPath));
        sb.Append(",\"assetGuid\":").Append(q(assetGuid));
        sb.Append(",\"worldPosition\":").Append(v3(effect.transform.position));
        sb.Append(",\"activeInHierarchy\":").Append(effect.gameObject.activeInHierarchy);
        sb.Append(",\"rendererEnabled\":").Append(effectRenderer != null && effectRenderer.enabled);
        sb.Append(",\"aliveParticleCount\":").Append(effect.aliveParticleCount);
        sb.Append(",\"culled\":").Append(effect.culled);
        if (effectRenderer != null)
        {
            var bounds = effectRenderer.bounds;
            sb.Append(",\"boundsCenter\":").Append(v3(bounds.center)).Append(",\"boundsSize\":").Append(v3(bounds.size));
            sb.Append(",\"cameraFrustumIntersectionsPostPose\":[");
            bool firstCamera = true;
            foreach (var cameraPlanes in planes)
            {
                if (!firstCamera) sb.Append(',');
                firstCamera = false;
                sb.Append("{\"camera\":").Append(q(cameraPlanes.Key.name)).Append(",\"intersects\":").Append(UnityEngine.GeometryUtility.TestPlanesAABB(cameraPlanes.Value, bounds)).Append("}");
            }
            sb.Append(']');
        }
        else sb.Append(",\"boundsAvailable\":false");
        sb.Append('}');
    }
    sb.Append("]");
    sb.Append(",\"uniqueActiveAssets\":[");
    for (int i = 0; i < uniqueAssets.Count; i++)
    {
        if (i != 0) sb.Append(',');
        var asset = uniqueAssets[i];
        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
        string assetGuid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
        sb.Append("{\"name\":").Append(q(asset.name)).Append(",\"path\":").Append(q(assetPath)).Append(",\"guid\":").Append(q(assetGuid));
        sb.Append(",\"activeComponentCount\":").Append(activeCountByAsset[asset]);
        try
        {
            var info = UnityEngine.VFX.VFXManager.GetBatchedEffectInfo(asset);
            sb.Append(",\"batchInfoAvailable\":true,\"batchInfo\":{");
            sb.Append("\"totalGPUSizeInBytes\":").Append(info.totalGPUSizeInBytes);
            sb.Append(",\"totalCPUSizeInBytes\":").Append(info.totalCPUSizeInBytes);
            sb.Append(",\"activeBatchCount\":").Append(info.activeBatchCount);
            sb.Append(",\"inactiveBatchCount\":").Append(info.inactiveBatchCount);
            sb.Append(",\"activeInstanceCount\":").Append(info.activeInstanceCount);
            sb.Append(",\"totalInstanceCapacity\":").Append(info.totalInstanceCapacity);
            sb.Append(",\"maxInstancePerBatchCapacity\":").Append(info.maxInstancePerBatchCapacity);
            sb.Append(",\"unbatchedInstanceCount\":").Append(info.unbatchedInstanceCount).Append("}");
        }
        catch (System.Exception exception)
        {
            sb.Append(",\"batchInfoAvailable\":false,\"batchInfoErrorType\":").Append(q(exception.GetType().Name));
        }
        sb.Append('}');
    }
    sb.Append("]}");
    string json = sb.ToString();
    System.IO.File.WriteAllText(outputPath, json, new System.Text.UTF8Encoding(false));

