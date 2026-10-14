return new System.Func<string>(() =>
{
    var renderers = UnityEngine.Object.FindObjectsByType<ViewWeave.QuiltRenderer>(UnityEngine.FindObjectsInactive.Include);
    if (renderers.Length != 1) return "restore_failed_renderer_count";
    var renderer = renderers[0];
    var session = renderer.GetComponent<ViewWeave.ViewWeaveBridgeSession>();
    var output = renderer.GetComponent<ViewWeave.BridgeWorkerQuiltOutput>();
    var rig = renderer.GetComponent<ViewWeave.ViewWeaveRig>();
    string expectedScene = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("QXNzZXRzL1NhbXBsZXMvVmlzdWFsIEVmZmVjdCBHcmFwaC8xNy42LjAvTGVhcm5pbmcgVGVtcGxhdGVzL1NjZW5lcy9VUlBfVkZYX0xlYXJuaW5nX1RlbXBsYXRlcy51bml0eQ=="));
    string expectedSession = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("MjYwMzk6MjU2"));
    string logPath = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("RTovTG9va2luZ0dsYXNzU0RLLy5sb2NhbC93aW5kb3dzLXVycC12Zngtb3JpZ2luYWwtcmVtZWR5LTIwMjYxMDEzL3J1bi0yMDI2MTAxM1QxOTI3NTk2NDVaL3dhdGNoZXIuanNvbmw="));
    var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
    if (UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return "restore_failed_editor_playing";
    if (!scene.IsValid() || !scene.isLoaded || scene.path != expectedScene) return "restore_failed_scene_identity";
    if (session == null || output == null || rig == null || session.GetEntityId().ToString() != expectedSession) return "restore_failed_session_identity";
    if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle || output.State != ViewWeave.BridgeWorkerQuiltOutput.WorkerState.Closed ||
        ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 || renderer.sourceCamera == null ||
        renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>() == null || renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>().HistoryCameraCount != 0 ||
        renderer.QuiltTexture != null || renderer.autoRender || rig.AutoRender) return "restore_failed_cleanup_invariants";
    bool expected = true;
    bool before = session.startOnEnable;
    if (before != expected) session.startOnEnable = expected;
    if (session.startOnEnable != expected) return "restore_failed_flag_value";
    string row = "event=postplay_start_on_enable_restored|utc=" + System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
        "|sessionEntityId=" + expectedSession + "|scenePath=" + expectedScene + "|expected=" + expected + "|before=" + before +
        "|after=" + session.startOnEnable + "|sceneSaveCalled=False";
    System.IO.File.AppendAllText(logPath, row + System.Environment.NewLine, System.Text.Encoding.ASCII);
    return "start_on_enable_restored|session=" + expectedSession + "|scene=" + expectedScene + "|value=" + session.startOnEnable;
})();