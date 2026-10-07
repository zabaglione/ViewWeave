#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Cinemachine;
using StarterAssets;

/// <summary>
/// Runtime-only diagnostic for the URP Sample Terminal-to-Oasis transition.
/// Copy into Assets/Editor/UrpSampleEvaluation only after the native GPU run is done.
/// Arm before entering the Oasis portal; this helper never starts a transition.
/// </summary>
public static class OasisPortalPhysicsProbe
{
    const string OutputName = "portal-physics-probe.txt";
    const int TailFrames = 8;
    const int FollowFrames = 180;
    const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static readonly List<string> Tail = new List<string>(TailFrames);
    static string outputPath;
    static SceneTransitionManager manager;
    static int lastUnityFrame = -1;
    static int completedFrame = -1;
    static string sourceFpcId;
    static Vector3 transitionStartPlayer;
    static Vector3 transitionStartMain;
    static Vector3 transitionStartScreen;
    static Vector3 transitionStartOffset;
    static Vector3 transitionStartMainRelative;
    static string transitionStartMediaType;
    static bool transitionPoseCached;
    static bool armed;
    static bool sawOasisTransition;
    static bool collecting;

    // Back up any existing output file before arming; Arm overwrites the previous run.
    public static string Arm()
    {
        Cancel();
        if (!EditorApplication.isPlaying)
            return "ERROR|Play Mode is required";

        manager = UnityEngine.Object.FindObjectsOfType<SceneTransitionManager>(true).FirstOrDefault();
        if (manager == null)
            return "ERROR|SceneTransitionManager not found";

        outputPath = Path.Combine(Application.dataPath, ".local/urp-oasis-diagnostic/" + OutputName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath,
            "Oasis portal physics probe; ASCII fields separated by pipe.\n" +
            "Rows: PRE|frame snapshot; COMPLETE|first observed post-Teleport state; PHYSICS|same callback geometry queries; POST|following frames.\n",
            Encoding.ASCII);

        var playerObject = GameObject.Find("PlayerCapsule");
        var fpc = playerObject != null ? playerObject.GetComponent<FirstPersonController>() : null;
        sourceFpcId = fpc != null ? fpc.GetEntityId().ToString() : "null";
        if (fpc == null)
            return "ERROR|PlayerCapsule FirstPersonController not found";

        armed = true;
        sawOasisTransition = false;
        collecting = false;
        transitionPoseCached = false;
        completedFrame = -1;
        lastUnityFrame = -1;
        Tail.Clear();
        EditorApplication.update += OnEditorUpdate;
        var mediaLoader = GetField(manager, "m_MediaSceneLoader");
        return "ARMED|path=" + outputPath + "|playerFpcId=" + sourceFpcId +
            "|mediaLoader=" + (mediaLoader != null ? mediaLoader.GetType().Name : "null");
    }

    public static string Cancel()
    {
        EditorApplication.update -= OnEditorUpdate;
        bool wasArmed = armed;
        armed = false;
        collecting = false;
        manager = null;
        return wasArmed ? "CANCELLED" : "IDLE";
    }

    static void OnEditorUpdate()
    {
        if (!armed || !EditorApplication.isPlaying)
        {
            Cancel();
            return;
        }

        int frame = Time.frameCount;
        if (frame == lastUnityFrame)
            return;
        lastUnityFrame = frame;

        if (manager == null)
            manager = UnityEngine.Object.FindObjectsOfType<SceneTransitionManager>(true).FirstOrDefault();
        if (manager == null)
            return;

        var current = GetField(manager, "currentScene") as SceneMetaData;
        var screen = GetField(manager, "screenScene") as SceneMetaData;
        bool inTransition = GetBool(manager, "InTransition");
        bool inTerminal = GetBool(manager, "InTerminal");
        string currentName = SceneName(current);
        string screenName = SceneName(screen);

        if (!collecting)
        {
            if (inTransition && screenName == "OasisScene")
            {
                if (!transitionPoseCached)
                {
                    CacheTransitionPose(manager);
                    transitionPoseCached = true;
                }
                sawOasisTransition = true;
                PushTail(CaptureFrame("PRE", frame, manager, current, screen));
            }

            if (sawOasisTransition && !inTransition && !inTerminal && currentName == "OasisScene")
            {
                collecting = true;
                completedFrame = frame;
                WriteLines(Tail);
                Tail.Clear();
                WriteLine(CaptureFrame("COMPLETE", frame, manager, current, screen));
                string verification = VerifyTeleportBranch(manager);
                WriteLine(verification);
                if (verification.Contains("|normalTeleportAccepted=True|"))
                {
                    WriteLine(CapturePhysics(manager, current, screen));
                    collecting = true;
                    completedFrame = frame;
                }
                else
                {
                    WriteLine((GetField(manager, "m_MediaSceneLoader") != null ? "MEDIA_COMPLETE" : "TELEPORT_REJECTED") +
                        "|frame=" + frame + "|physicsSkipped=true");
                    WriteLine("END|frame=" + frame + "|followFrames=0|result=branch_rejected");
                    armed = false;
                    EditorApplication.update -= OnEditorUpdate;
                }
            }
            return;
        }

        WriteLine(CaptureFrame("POST", frame, manager, current, screen));
        if (frame - completedFrame >= FollowFrames)
        {
            WriteLine("END|frame=" + frame + "|followFrames=" + (frame - completedFrame));
            armed = false;
            collecting = false;
            EditorApplication.update -= OnEditorUpdate;
            Debug.Log("Oasis portal physics probe complete; diagnostic file written.");
        }
    }

    static string CaptureFrame(string kind, int frame, SceneTransitionManager stm, SceneMetaData current, SceneMetaData screen)
    {
        var player = GameObject.Find("PlayerCapsule");
        var cc = player != null ? player.GetComponent<CharacterController>() : null;
        var fpc = player != null ? player.GetComponent<FirstPersonController>() : null;
        var mainCamera = SceneTransitionManager.GetMainCamera();
        var camera = mainCamera != null ? mainCamera.GetComponent<Camera>() : null;
        var brain = mainCamera != null ? mainCamera.GetComponent<CinemachineBrain>() : null;
        var screenObject = GameObject.FindGameObjectWithTag("ScreenCamera");
        var screenCamera = screenObject != null ? screenObject.GetComponent<Camera>() : null;
        var offsetCamera = screenObject != null ? screenObject.GetComponent<OffsetCamera>() : null;
        var loader = GetField(stm, "m_Loader") as SceneLoader;
        var playerManager = player != null && player.transform.parent != null ? player.transform.parent.GetComponent<PlayerManager>() : null;
        var playerPosition = player != null ? player.transform.position : Vector3.zero;
        var mainPosition = mainCamera != null ? mainCamera.transform.position : Vector3.zero;
        var screenPosition = screenObject != null ? screenObject.transform.position : Vector3.zero;
        var spawn = screen != null && screen.SpawnTransform != null ? screen.SpawnTransform.position : Vector3.zero;
        int actualLayerMask = fpc != null ? fpc.GroundLayers.value : 0;
        return string.Join("|", new[]
        {
            kind, frame.ToString(CultureInfo.InvariantCulture), F(Time.time),
            currentNameFor(current), screenNameFor(screen), inTransitionFor(stm).ToString(),
            inTerminalFor(stm).ToString(), GetBool(stm, "CoolingOff").ToString(),
            player != null ? player.GetEntityId().ToString() : "null",
            fpc != null ? fpc.GetEntityId().ToString() : "null",
            fpc != null && fpc.GetEntityId().ToString() == sourceFpcId ? "fpcIdMatch=true" : "fpcIdMatch=false",
            player != null && player.transform.parent != null ? player.transform.parent.name : "null", V(playerPosition),
            cc != null ? cc.enabled.ToString() : "false", cc != null ? cc.isGrounded.ToString() : "false",
            cc != null ? V(cc.velocity) : "null", fpc != null ? fpc.enabled.ToString() : "false",
            fpc != null ? fpc.Grounded.ToString() : "false", fpc != null ? F(GetPrivateFloat(fpc, "_verticalVelocity")) : "null",
            actualLayerMask.ToString(CultureInfo.InvariantCulture), fpc != null ? F(fpc.Gravity) : "null",
            fpc != null ? fpc.GroundedOffset.ToString("R", CultureInfo.InvariantCulture) : "null",
            fpc != null ? fpc.GroundedRadius.ToString("R", CultureInfo.InvariantCulture) : "null",
            cc != null ? F(cc.height) : "null", cc != null ? F(cc.radius) : "null",
            cc != null ? V(cc.center) : "null", cc != null ? F(cc.skinWidth) : "null",
            cc != null ? V(cc.transform.lossyScale) : "null", camera != null ? V(mainPosition) : "null",
            camera != null ? Q(camera.transform.rotation) : "null", brain != null ? brain.enabled.ToString() : "null",
            screenCamera != null ? V(screenPosition) : "null",
            screenCamera != null ? Q(screenCamera.transform.rotation) : "null",
            offsetCamera != null ? V(offsetCamera.GetOffset()) : "null",
            player != null ? V(playerPosition - mainPosition) : "null", V(spawn),
            loader != null ? loader.SceneName : "null",
            loader != null && loader.ReferencePoint != null ? V(loader.ReferencePoint.position) : "null",
            GetBool(stm, "m_DisableCharacterForTime").ToString(), F(GetPrivateFloat(stm, "m_DisabledCharacterTime")),
            playerManager != null ? GetBool(playerManager, "m_FlythroughWhenIdle").ToString() : "null",
            playerManager != null && playerManager.FlythroughDirector != null ? playerManager.FlythroughDirector.state.ToString() : "null",
            screen != null && screen.SequenceDirector != null ? screen.SequenceDirector.state.ToString() : "null",
            SceneManager.GetActiveScene().name,
            GetField(stm, "m_MediaSceneLoader") != null ? GetField(stm, "m_MediaSceneLoader").GetType().Name : "null",
            Time.timeScale.ToString("R", CultureInfo.InvariantCulture)
        });
    }

    static void CacheTransitionPose(SceneTransitionManager stm)
    {
        var player = GameObject.Find("PlayerCapsule");
        var main = SceneTransitionManager.GetMainCamera();
        var screen = GameObject.FindGameObjectWithTag("ScreenCamera");
        var offset = screen != null ? screen.GetComponent<OffsetCamera>() : null;
        transitionStartPlayer = player != null ? player.transform.position : Vector3.zero;
        transitionStartMain = main != null ? main.transform.position : Vector3.zero;
        transitionStartScreen = screen != null ? screen.transform.position : Vector3.zero;
        transitionStartOffset = offset != null ? offset.GetOffset() : Vector3.zero;
        transitionStartMainRelative = transitionStartMain - transitionStartPlayer;
        var mediaLoader = GetField(stm, "m_MediaSceneLoader");
        transitionStartMediaType = mediaLoader != null ? mediaLoader.GetType().Name : "null";
    }

    static string VerifyTeleportBranch(SceneTransitionManager stm)
    {
        var player = GameObject.Find("PlayerCapsule");
        var main = SceneTransitionManager.GetMainCamera();
        var screen = GameObject.FindGameObjectWithTag("ScreenCamera");
        var offset = screen != null ? screen.GetComponent<OffsetCamera>() : null;
        var fpc = player != null ? player.GetComponent<FirstPersonController>() : null;
        var current = GetField(stm, "currentScene") as SceneMetaData;
        var mediaLoader = GetField(stm, "m_MediaSceneLoader");
        var disableController = GetBool(stm, "m_DisableCharacterForTime");
        float disableTimer = GetPrivateFloat(stm, "m_DisabledCharacterTime");
        Vector3 playerPosition = player != null ? player.transform.position : Vector3.zero;
        Vector3 mainPosition = main != null ? main.transform.position : Vector3.zero;
        Vector3 mainRelativeAfter = mainPosition - playerPosition;
        Vector3 offsetAfter = offset != null ? offset.GetOffset() : Vector3.zero;
        Vector3 expectedPlayer = transitionStartScreen - (transitionStartMain - transitionStartPlayer);
        float expectedError = Vector3.Distance(playerPosition, expectedPlayer);
        float movement = Vector3.Distance(playerPosition, transitionStartPlayer);
        bool activeOasis = SceneManager.GetActiveScene().name == "OasisScene";
        bool currentOasis = current != null && SceneName(current) == "OasisScene";
        bool inTerminal = GetBool(stm, "InTerminal");
        bool mainRelativeMatches = main != null && Vector3.Distance(mainRelativeAfter, transitionStartMainRelative) < 0.1f;
        bool offsetFlipped = offset != null && Vector3.Distance(offsetAfter, -transitionStartOffset) < 0.01f;
        bool normalTeleportAccepted = mediaLoader == null && activeOasis && currentOasis && !inTerminal && movement > 1f &&
            expectedError < 0.1f && mainRelativeMatches && offsetFlipped && disableController && disableTimer > 0f && fpc != null && !fpc.enabled;
        return (mediaLoader == null ? "TELEPORT_COMPLETE" : "MEDIA_COMPLETE") + "|frame=" + Time.frameCount +
            "|selected=" + (mediaLoader == null ? "Teleport" : "CinemachineTeleport") +
            "|mediaLoaderAtStart=" + (string.IsNullOrEmpty(transitionStartMediaType) ? "not_seen" : transitionStartMediaType) +
            "|mediaLoader=" + (mediaLoader != null ? mediaLoader.GetType().Name : "null") +
            "|current=" + (current != null ? SceneName(current) : "null") +
            "|inTerminal=" + inTerminal +
            "|startPlayer=" + V(transitionStartPlayer) +
            "|startMain=" + V(transitionStartMain) +
            "|startMainRelative=" + V(transitionStartMainRelative) +
            "|startScreen=" + V(transitionStartScreen) +
            "|expectedPlayer=" + V(expectedPlayer) +
            "|actualPlayer=" + V(playerPosition) +
            "|mainRelativeAfter=" + V(mainRelativeAfter) +
            "|mainRelativeMatches=" + mainRelativeMatches +
            "|movement=" + F(movement) +
            "|expectedError=" + F(expectedError) +
            "|offsetStart=" + V(transitionStartOffset) +
            "|offsetAfter=" + V(offsetAfter) +
            "|offsetFlipped=" + offsetFlipped +
            "|activeScene=" + SceneManager.GetActiveScene().name +
            "|disableController=" + disableController +
            "|disableTimer=" + F(disableTimer) +
            "|fpcEnabled=" + (fpc != null && fpc.enabled) +
            "|normalTeleportAccepted=" + normalTeleportAccepted + "|";
    }

    static string CapturePhysics(SceneTransitionManager stm, SceneMetaData current, SceneMetaData screen)
    {
        var player = GameObject.Find("PlayerCapsule");
        if (player == null)
            return "PHYSICS|ERROR|PlayerCapsule missing";
        var cc = player.GetComponent<CharacterController>();
        var fpc = player.GetComponent<FirstPersonController>();
        if (cc == null || fpc == null)
            return "PHYSICS|ERROR|CharacterController or FirstPersonController missing";

        // Match the exact FirstPersonController GroundedCheck point and mask.
        var rootPosition = player.transform.position;
        var groundCenter = new Vector3(rootPosition.x, rootPosition.y - fpc.GroundedOffset, rootPosition.z);
        var groundMask = fpc.GroundLayers;
        Physics.SyncTransforms();

        // Convert CharacterController dimensions to a world-space upright capsule.
        var scale = player.transform.lossyScale;
        float radius = cc.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float height = Mathf.Max(cc.height * Mathf.Abs(scale.y), radius * 2f);
        var center = player.transform.TransformPoint(cc.center);
        var axis = player.transform.up;
        float halfSegment = Mathf.Max(0f, height * 0.5f - radius);
        var capA = center + axis * halfSegment;
        var capB = center - axis * halfSegment;

        var sb = new StringBuilder(8192);
        sb.Append("PHYSICS|frame=").Append(Time.frameCount)
            .Append("|stmPlayerId=").Append(cc.GetEntityId().ToString())
            .Append("|fpcId=").Append(fpc.GetEntityId().ToString())
            .Append("|fpcIdMatch=").Append(fpc.GetEntityId().ToString() == sourceFpcId)
            .Append("|groundMask=").Append(groundMask.value)
            .Append("|groundCenter=").Append(V(groundCenter))
            .Append("|groundRadius=").Append(F(fpc.GroundedRadius))
            .Append("|groundCheck=").Append(Physics.CheckSphere(groundCenter, fpc.GroundedRadius, groundMask, QueryTriggerInteraction.Ignore))
            .Append("|ccRoot=").Append(V(rootPosition))
            .Append("|ccWorldCenter=").Append(V(center))
            .Append("|ccCapsuleA=").Append(V(capA))
            .Append("|ccCapsuleB=").Append(V(capB))
            .Append("|ccWorldRadius=").Append(F(radius))
            .Append("|ccWorldHeight=").Append(F(height))
            .Append("|ccScale=").Append(V(scale));

        AppendOverlapSphere(sb, "groundSphere", groundCenter, fpc.GroundedRadius, groundMask, player.transform);
        AppendOverlapSphere(sb, "groundSphereAll", groundCenter, fpc.GroundedRadius, Physics.AllLayers, player.transform);
        AppendCapsule(sb, "playerCapsuleGroundMask", capA, capB, radius, groundMask, cc, player.transform, rootPosition, player.transform.rotation);
        AppendCapsule(sb, "playerCapsuleAll", capA, capB, radius, Physics.AllLayers, cc, player.transform, rootPosition, player.transform.rotation);

        var oasis = screen != null && SceneName(screen) == "OasisScene" ? screen : current;
        var spawn = oasis != null && oasis.SpawnTransform != null ? oasis.SpawnTransform.position : Vector3.zero;
        sb.Append("|spawn=").Append(V(spawn));
        AppendRaySet(sb, "spawnDown", spawn, Vector3.down, 30f, groundMask, oasis);
        AppendRaySet(sb, "spawnUp", spawn + Vector3.up * 50f, Vector3.down, 100f, groundMask, oasis);
        AppendRaySet(sb, "spawnDownAll", spawn, Vector3.down, 30f, Physics.AllLayers, oasis);
        AppendRaySet(sb, "spawnUpAll", spawn + Vector3.up * 50f, Vector3.down, 100f, Physics.AllLayers, oasis);

        // Candidate player origin places the capsule feet just above each upward-ray surface.
        var upwardHits = Physics.RaycastAll(spawn + Vector3.up * 50f, Vector3.down, 100f, Physics.AllLayers, QueryTriggerInteraction.Ignore)
            .OrderBy(h => h.distance).ToArray();
        for (int i = 0; i < upwardHits.Length; i++)
        {
            var hit = upwardHits[i];
            Vector3 candidate = hit.point - player.transform.up * (cc.center.y * Mathf.Abs(scale.y) - height * 0.5f) + player.transform.up * cc.skinWidth;
            var candidateCenter = candidate + player.transform.rotation * Vector3.Scale(cc.center, scale);
            float candidateGroundY = candidate.y - fpc.GroundedOffset;
            var candidateGroundCenter = new Vector3(candidate.x, candidateGroundY, candidate.z);
            sb.Append("|candidate=").Append(i)
                .Append(",hitId:").Append(hit.collider.GetEntityId().ToString())
                .Append(",hit:").Append(hit.collider.name)
                .Append(",hitPoint:").Append(V(hit.point))
                .Append(",origin:").Append(V(candidate))
                .Append(",groundCheck:").Append(Physics.CheckSphere(candidateGroundCenter, fpc.GroundedRadius, groundMask, QueryTriggerInteraction.Ignore));
            AppendCapsule(sb, "candidateCapsuleAll" + i,
                candidateCenter + player.transform.up * halfSegment,
                candidateCenter - player.transform.up * halfSegment,
                radius, Physics.AllLayers, cc, player.transform, candidate, player.transform.rotation);
        }

        return sb.ToString();
    }

    static void AppendOverlapSphere(StringBuilder sb, string label, Vector3 center, float radius, LayerMask mask, Transform player)
    {
        var hits = Physics.OverlapSphere(center, radius, mask, QueryTriggerInteraction.Ignore)
            .Where(c => c != null && !IsPlayerCollider(c, player))
            .OrderBy(c => c.GetEntityId().ToString(), StringComparer.Ordinal).ToArray();
        sb.Append('|').Append(label).Append("Count=").Append(hits.Length);
        foreach (var c in hits)
            sb.Append('|').Append(label).Append("Hit=").Append(ColliderRecord(c, center));
    }

    static void AppendCapsule(StringBuilder sb, string label, Vector3 a, Vector3 b, float radius, LayerMask mask,
        CharacterController playerCollider, Transform player, Vector3 playerPosition, Quaternion playerRotation)
    {
        var hits = Physics.OverlapCapsule(a, b, radius, mask, QueryTriggerInteraction.Ignore)
            .Where(c => c != null && !IsPlayerCollider(c, player))
            .OrderBy(c => c.GetEntityId().ToString(), StringComparer.Ordinal).ToArray();
        sb.Append('|').Append(label).Append("Count=").Append(hits.Length);
        foreach (var other in hits)
        {
            bool penetrates = false;
            Vector3 direction = Vector3.zero;
            float distance = 0f;
            try
            {
                penetrates = Physics.ComputePenetration(playerCollider, playerPosition, playerRotation,
                    other, other.transform.position, other.transform.rotation, out direction, out distance);
            }
            catch (Exception ex)
            {
                sb.Append('|').Append(label).Append("PenetrationError=").Append(ex.GetType().Name);
            }
            sb.Append('|').Append(label).Append("Hit=").Append(ColliderRecord(other, playerPosition))
                .Append(",penetrates:").Append(penetrates)
                .Append(",direction:").Append(V(direction))
                .Append(",distance:").Append(F(distance));
        }
    }

    static void AppendRaySet(StringBuilder sb, string label, Vector3 origin, Vector3 direction, float distance,
        LayerMask mask, SceneMetaData oasis)
    {
        var hits = Physics.RaycastAll(origin, direction, distance, mask, QueryTriggerInteraction.Ignore)
            .OrderBy(h => h.distance).ToArray();
        sb.Append('|').Append(label).Append("Origin=").Append(V(origin))
            .Append('|').Append(label).Append("Count=").Append(hits.Length);
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            sb.Append('|').Append(label).Append("Hit=").Append(h.collider.GetEntityId().ToString())
                .Append(',').Append(h.collider.name)
                .Append(",scene:").Append(SceneName(h.collider.gameObject.scene))
                .Append(",layer:").Append(h.collider.gameObject.layer)
                .Append(",point:").Append(V(h.point))
                .Append(",normal:").Append(V(h.normal))
                .Append(",distance:").Append(F(h.distance))
                .Append(",closestSpawn:").Append(ClosestPointRecord(h.collider, oasis != null && oasis.SpawnTransform != null ? oasis.SpawnTransform.position : origin))
                .Append(",bounds:").Append(BoundsRecord(h.collider.bounds));
        }
    }

    static string ColliderRecord(Collider c, Vector3 point)
    {
        return c.GetEntityId().ToString() + "," + c.name + ",scene:" + SceneName(c.gameObject.scene) +
            ",layer:" + c.gameObject.layer + ",type:" + c.GetType().Name +
            ",ignorePlayerLayerCollision:" + Physics.GetIgnoreLayerCollision(GameObject.Find("PlayerCapsule").layer, c.gameObject.layer) +
            ",enabled:" + c.enabled + ",active:" + c.gameObject.activeInHierarchy +
            ",trigger:" + c.isTrigger + ",closest:" + ClosestPointRecord(c, point) +
            ",bounds:" + BoundsRecord(c.bounds);
    }

    static string ClosestPointRecord(Collider collider, Vector3 point)
    {
        var mesh = collider as MeshCollider;
        if (mesh != null && !mesh.convex) return "unsupported_nonconvex_mesh";
        if (!(collider is BoxCollider) && !(collider is SphereCollider) &&
            !(collider is CapsuleCollider) && mesh == null)
            return "unsupported_" + collider.GetType().Name;
        return V(collider.ClosestPoint(point));
    }

    static bool IsPlayerCollider(Collider c, Transform player)
    {
        return c.transform == player || c.transform.IsChildOf(player);
    }

    static void PushTail(string line)
    {
        Tail.Add(line);
        while (Tail.Count > TailFrames)
            Tail.RemoveAt(0);
    }

    static void WriteLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            WriteLine(line);
    }

    static void WriteLine(string line)
    {
        File.AppendAllText(outputPath, line + "\n", Encoding.ASCII);
    }

    static object GetField(object target, string name)
    {
        return target != null ? target.GetType().GetField(name, InstanceFlags)?.GetValue(target) : null;
    }

    static bool GetBool(object target, string name)
    {
        var value = GetField(target, name);
        return value is bool b && b;
    }

    static float GetPrivateFloat(object target, string name)
    {
        var value = GetField(target, name);
        return value is float f ? f : float.NaN;
    }

    static string SceneName(SceneMetaData data) => data != null ? SceneName(data.Scene.IsValid() ? data.Scene : data.gameObject.scene) : "null";
    static string SceneName(Scene scene) => scene.IsValid() ? scene.name : "invalid";
    static string currentNameFor(SceneMetaData scene) => SceneName(scene);
    static string screenNameFor(SceneMetaData scene) => SceneName(scene);
    static bool inTransitionFor(object target) => GetBool(target, "InTransition");
    static bool inTerminalFor(object target) => GetBool(target, "InTerminal");
    static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string V(Vector3 value) => F(value.x) + "," + F(value.y) + "," + F(value.z);
    static string Q(Quaternion value) => F(value.x) + "," + F(value.y) + "," + F(value.z) + "," + F(value.w);
    static string BoundsRecord(Bounds b) => "center:" + V(b.center) + ",extents:" + V(b.extents);
}
#endif
