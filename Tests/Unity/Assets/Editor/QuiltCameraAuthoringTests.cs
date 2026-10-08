using System;
using System.Reflection;
using ViewWeave;
using ViewWeave.Editor;
using UnityEditor;
using UnityEngine;

public static class QuiltCameraAuthoringTests
{
    [MenuItem("Tools/ViewWeave/Run Camera Authoring Validation")]
    public static void RunCameraAuthoringValidation()
    {
        Run();
    }

    public static void RunAllRegressions()
    {
        Run();
        QuiltCoreBatchTests.Run();
        QualityRigTests.Run();
    }

    public static void Run()
    {
        QuiltRendererAuthoringOperationsTests.Run();
        CheckProjectionBoundsAndFocusPlane();
        CheckParentTransformUsesCameraPose();
        CheckInvalidInputsAreRejected();
        CheckPhysicalFramingResolution();
        CheckAtomicFramingMutationApi();
        CheckInspectorPropertiesAndUndo();
        Debug.Log("Quilt camera authoring tests passed");
    }

    static void CheckAtomicFramingMutationApi()
    {
        GameObject go = new GameObject("atomic-framing-api-test");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            Require(renderer.TryGetFraming(out QuiltCameraFraming original), "framing snapshot is available");

            QuiltCameraFraming invalid = new QuiltCameraFraming(float.NaN, original.FocusDistance, false, 0f, 0f, 1f);
            Require(!renderer.TrySetFraming(invalid, out _) && renderer.TryGetFraming(out QuiltCameraFraming afterInvalid) &&
                afterInvalid == original, "invalid framing mutation is atomic");

            QuiltCameraFraming physical = new QuiltCameraFraming(60f, original.FocusDistance, true, 800f, 450f, 1f);
            Require(renderer.TrySetFraming(physical, out string physicalError), "valid physical framing is set: " + physicalError);
            Require(renderer.TryGetEffectiveSettings(out QuiltSettings firstEffective, out _), "physical framing resolves effective settings");
            float physicalPlaneHeight = 2f * firstEffective.cameraDistance * Mathf.Tan(firstEffective.fieldOfView * Mathf.Deg2Rad * 0.5f);
            Require(Mathf.Abs(physicalPlaneHeight - 0.45f) < 0.00001f, "physical API resolves the requested aperture height");
            float previousBaseDistance = renderer.settings.cameraDistance;
            Require(!renderer.TrySetFraming(new QuiltCameraFraming(50f, previousBaseDistance + 1f, true, 800f, 450f, 1f), out _) &&
                renderer.settings.cameraDistance == previousBaseDistance && renderer.settings.fieldOfView == 60f,
                "changed stored base focus is rejected without erasing the requested FOV");
            Require(renderer.TrySetFocusDistance(2f, out string focusError), "physical focus operation solves FOV: " + focusError);
            Require(renderer.TryGetEffectiveSettings(out QuiltSettings secondEffective, out _), "updated physical framing resolves");
            float secondPlaneHeight = 2f * secondEffective.cameraDistance * Mathf.Tan(secondEffective.fieldOfView * Mathf.Deg2Rad * 0.5f);
            Require(Mathf.Abs(secondEffective.cameraDistance - 2f) < 0.00001f && Mathf.Abs(secondPlaneHeight - physicalPlaneHeight) < 0.00001f,
                "physical focus edit changes effective distance while preserving plane height");

            object allowOwner = new object();
            object denyOwner = new object();
            IDisposable allow = RegisterMutationOwner(renderer, allowOwner, _ => string.Empty);
            IDisposable deny = RegisterMutationOwner(renderer, denyOwner, _ => "secondary owner blocks this edit");
            Require(allow != null && deny != null, "multiple mutation owners register");
            QuiltCameraFraming beforeOwners = renderer.TryGetFraming(out QuiltCameraFraming ownerSnapshot) ? ownerSnapshot : default;
            Require(!renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out string modeError) && modeError == "secondary owner blocks this edit" &&
                renderer.viewRenderMode == QuiltViewRenderMode.Full, "all active owner policies must allow a sampling-mode mutation");
            Require(!renderer.TrySetFraming(new QuiltCameraFraming(55f, beforeOwners.FocusDistance, true, 800f, 450f, 1f), out _) &&
                renderer.TryGetFraming(out QuiltCameraFraming afterOwnerBlock) && afterOwnerBlock == beforeOwners,
                "owner denial leaves framing unchanged");
            Vector3 guardedPosition = camera.transform.position;
            Quaternion guardedRotation = camera.transform.rotation;
            Require(!QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, 10f, 5f) &&
                camera.transform.position == guardedPosition && camera.transform.rotation == guardedRotation,
                "Orbit camera authoring obeys the active framing mutation policy");
            deny.Dispose();
            Require(renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out _), "remaining owner policy can allow sampling mode");
            allow.Dispose();
            Require(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback,
                out QuiltRendererOperationLease playbackLease, out _), "playback operation lease can be acquired after test owners are removed");
            Require(!renderer.TryCheckMutation(QuiltRendererMutationKind.AutoRender, true, out _) &&
                renderer.TryCheckMutation(QuiltRendererMutationKind.AutoRender, false, out _),
                "playback lease blocks re-enabling Auto Render but permits stopping it");
            guardedPosition = camera.transform.position;
            guardedRotation = camera.transform.rotation;
            Require(!QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, 10f, 5f) &&
                camera.transform.position == guardedPosition && camera.transform.rotation == guardedRotation,
                "Orbit camera authoring is blocked by a playback lease");
            playbackLease.Dispose();

            FieldInfo ownerRequirement = typeof(QuiltRenderer).GetField("mutationOwnerRequired", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(ownerRequirement != null, "owner requirement safety latch exists");
            Require(ownerRequirement.GetCustomAttribute<SerializeField>() == null,
                "runtime mutation owner requirement is not serialized into a saved Scene");
            ownerRequirement.SetValue(renderer, true);
            Require(!renderer.TrySetViewRenderMode(QuiltViewRenderMode.Quarter, out string missingOwnerError) &&
                missingOwnerError.Contains("no renderer mutation-policy registration"), "missing active-owner registration fails closed");
            ownerRequirement.SetValue(renderer, false);
            Require(renderer.TrySetViewRenderMode(QuiltViewRenderMode.Quarter, out _), "mutation resumes after owner requirement is cleared");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static IDisposable RegisterMutationOwner(QuiltRenderer renderer, object owner, Func<QuiltRendererMutationRequest, string> policy)
    {
        Type ownerPolicy = typeof(QuiltRenderer).Assembly.GetType("ViewWeave.QuiltRendererMutationPolicy");
        MethodInfo register = ownerPolicy == null ? null : ownerPolicy.GetMethod("RegisterOwner", BindingFlags.Static | BindingFlags.NonPublic);
        Require(register != null, "internal mutation owner registration exists");
        object[] arguments = { renderer, owner, policy, null };
        IDisposable result = register.Invoke(null, arguments) as IDisposable;
        Require(result != null, "mutation owner registration succeeds: " + arguments[3]);
        return result;
    }

    static void CheckPhysicalFramingResolution()
    {
        QuiltSettings stored = new QuiltSettings { views = 48, rows = 6, columns = 8, quiltWidth = 3360, quiltHeight = 3360, fieldOfView = 60f, viewAspect = 1f, cameraDistance = 4f, viewCone = 40f };
        QuiltPhysicalFraming portrait = new QuiltPhysicalFraming { enabled = true, visibleApertureWidthMillimeters = 62f, visibleApertureHeightMillimeters = 110f, unitsPerMeter = 1f };
        Require(QuiltFramingResolver.TryResolve(stored, portrait, 60f, out QuiltSettings portraitSettings, out _), "portrait physical framing resolves");
        Require(Mathf.Abs(portraitSettings.viewAspect - 62f / 110f) < 0.00001f, "portrait physical aspect resolves");
        Require(Mathf.Abs(portraitSettings.cameraDistance - (0.11f * 0.5f / Mathf.Tan(30f * Mathf.Deg2Rad))) < 0.00001f, "portrait focus distance follows physical aperture");
        Require(stored.viewAspect == 1f && stored.cameraDistance == 4f, "resolver does not mutate stored aspect or distance");

        QuiltPhysicalFraming landscape = new QuiltPhysicalFraming { enabled = true, visibleApertureWidthMillimeters = 160f, visibleApertureHeightMillimeters = 90f, unitsPerMeter = 1f };
        Require(QuiltFramingResolver.TryResolve(stored, landscape, 40f, out QuiltSettings landscapeSettings, out _), "landscape physical framing resolves");
        float planeHeight = 2f * landscapeSettings.cameraDistance * Mathf.Tan(landscapeSettings.fieldOfView * Mathf.Deg2Rad * 0.5f);
        Require(Mathf.Abs(landscapeSettings.viewAspect - 160f / 90f) < 0.00001f && Mathf.Abs(planeHeight - 0.09f) < 0.00001f, "landscape FOV preserves fixed physical plane height");
        Require(Mathf.Abs(portrait.DiagonalInches - Mathf.Sqrt(62f * 62f + 110f * 110f) / 25.4f) < 0.00001f, "physical diagonal uses millimeters without a DPI default");

        QuiltPhysicalFraming unset = new QuiltPhysicalFraming { enabled = true };
        Require(!QuiltFramingResolver.TryResolve(stored, unset, 60f, out _, out string unsetError) && unsetError.Contains("finite positive aperture dimensions"), "unset aperture is rejected without a fabricated default");
        landscape.unitsPerMeter = float.NaN;
        Require(!QuiltFramingResolver.TryResolve(stored, landscape, 60f, out _, out _), "non-finite unit scale is rejected");
        landscape.unitsPerMeter = 1f;
        Require(!QuiltFramingResolver.TryResolve(stored, landscape, 179f, out _, out _), "out-of-range FOV is rejected");

        GameObject rendererObject = new GameObject("physical-framing-authoring-test");
        GameObject cameraObject = new GameObject("physical-framing-camera-test");
        try
        {
            QuiltRenderer renderer = rendererObject.AddComponent<QuiltRenderer>();
            Camera sourceCamera = cameraObject.AddComponent<Camera>();
            renderer.sourceCamera = sourceCamera;
            renderer.physicalFraming = new QuiltPhysicalFraming
            {
                enabled = true,
                visibleApertureWidthMillimeters = 160f,
                visibleApertureHeightMillimeters = 90f,
                unitsPerMeter = 1f
            };
            float sourceFov = sourceCamera.fieldOfView;
            Require(QuiltRendererAuthoringOperations.TrySetEffectiveFocusDistance(renderer, 0.5f), "physical focus distance can be authored in the Main Stage");
            Require(QuiltFramingResolver.TryResolve(renderer.settings, renderer.physicalFraming, renderer.settings.fieldOfView, out QuiltSettings authored, out _), "authored physical focus resolves");
            Require(Mathf.Abs(authored.cameraDistance - 0.5f) < 0.00001f && sourceCamera.fieldOfView == sourceFov, "physical focus setter changes renderer FOV without mutating source camera");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(rendererObject);
        }
    }

    static void CheckProjectionBoundsAndFocusPlane()
    {
        GameObject root = new GameObject("authoring-test-root");
        try
        {
            root.transform.position = new Vector3(2.1f, -3.2f, 4.3f);
            root.transform.rotation = Quaternion.Euler(13f, 47f, -21f);
            root.transform.localScale = new Vector3(2f, 0.4f, 3.5f);
            Camera camera = root.AddComponent<Camera>();
            camera.nearClipPlane = 0.13f;
            camera.farClipPlane = 73f;
            QuiltSettings settings = new QuiltSettings { views = 48, rows = 6, columns = 8, fieldOfView = 68f, viewAspect = 1.3f, cameraDistance = 2.7f, viewCone = 37f };
            Require(QuiltFrustumGeometry.TryBuild(camera.transform, settings, camera.nearClipPlane, camera.farClipPlane, out QuiltFrustumGeometry.Geometry geometry), "valid frustum geometry builds");

            float focusHalfHeight = settings.cameraDistance * Mathf.Tan(settings.fieldOfView * Mathf.Deg2Rad * 0.5f);
            float focusHalfWidth = focusHalfHeight * settings.viewAspect;
            Require(Mathf.Abs(Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[1]) * 0.5f - focusHalfHeight) < 0.0001f, "focus plane height follows vertical fov");
            Require(Mathf.Abs(Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[3]) * 0.5f - focusHalfWidth) < 0.0001f, "focus plane width follows view aspect");

            float maximumOffset = QuiltMath.ViewOffset(settings.views - 1, settings.views, settings.viewCone, settings.cameraDistance);
            CheckProjectedQuad(camera, settings, -maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.nearClipPlane, geometry.leftNearPlane, "left near");
            CheckProjectedQuad(camera, settings, maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.nearClipPlane, geometry.rightNearPlane, "right near");
            CheckProjectedQuad(camera, settings, -maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.farClipPlane, geometry.leftFarPlane, "left far");
            CheckProjectedQuad(camera, settings, maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.farClipPlane, geometry.rightFarPlane, "right far");

            for (int corner = 0; corner < 4; ++corner)
            {
                Vector2 expected = new Vector2(corner == 0 || corner == 1 ? -1f : 1f, corner == 0 || corner == 3 ? -1f : 1f);
                CheckProjectedPoint(camera, settings, -maximumOffset, camera.nearClipPlane, camera.farClipPlane, geometry.focusPlane[corner], expected, "left focus");
                CheckProjectedPoint(camera, settings, maximumOffset, camera.nearClipPlane, camera.farClipPlane, geometry.focusPlane[corner], expected, "right focus");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    static void CheckProjectedQuad(Camera camera, QuiltSettings settings, float eyeOffset, float nearClip, float farClip, float depth, Vector3[] quad, string label)
    {
        for (int i = 0; i < quad.Length; ++i)
        {
            Vector2 expected = new Vector2(i == 0 || i == 1 ? -1f : 1f, i == 0 || i == 3 ? -1f : 1f);
            CheckProjectedPoint(camera, settings, eyeOffset, nearClip, farClip, quad[i], expected, label + " corner " + i);
        }
        Require(Mathf.Abs(Vector3.Dot(quad[0] - camera.transform.position, camera.transform.rotation * Vector3.forward) - depth) < 0.0002f, label + " plane depth");
    }

    static void CheckProjectedPoint(Camera camera, QuiltSettings settings, float eyeOffset, float nearClip, float farClip, Vector3 point, Vector2 expected, string label)
    {
        Vector3 originalPosition = camera.transform.position;
        Quaternion originalRotation = camera.transform.rotation;
        try
        {
            camera.transform.SetPositionAndRotation(originalPosition + originalRotation * Vector3.right * eyeOffset, originalRotation);
            Matrix4x4 view = camera.worldToCameraMatrix;
            Matrix4x4 projection = QuiltMath.OffAxisProjection(settings.fieldOfView, settings.viewAspect, nearClip, farClip, eyeOffset, settings.cameraDistance);
            Vector4 clip = projection * (view * new Vector4(point.x, point.y, point.z, 1f));
            Require(Mathf.Abs(clip.w) > 0.000001f, label + " finite clip w");
            float x = clip.x / clip.w;
            float y = clip.y / clip.w;
            Require(Mathf.Abs(x - expected.x) < 0.0005f && Mathf.Abs(y - expected.y) < 0.0005f, label + " matches off-axis projection");
        }
        finally { camera.transform.SetPositionAndRotation(originalPosition, originalRotation); }
    }

    static void CheckParentTransformUsesCameraPose()
    {
        GameObject parent = new GameObject("scaled-parent");
        GameObject child = new GameObject("source-camera");
        try
        {
            parent.transform.position = new Vector3(-4f, 1f, 8f);
            parent.transform.rotation = Quaternion.Euler(23f, 71f, 12f);
            parent.transform.localScale = new Vector3(3f, 0.25f, 5f);
            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = new Vector3(0.6f, 1.2f, -0.9f);
            child.transform.localRotation = Quaternion.Euler(-7f, 15f, 31f);
            Camera camera = child.AddComponent<Camera>();
            QuiltSettings settings = new QuiltSettings { views = 8, rows = 2, columns = 4, fieldOfView = 51f, viewAspect = 0.85f, cameraDistance = 3.1f, viewCone = 24f };
            Require(QuiltFrustumGeometry.TryBuild(camera.transform, settings, camera.nearClipPlane, camera.farClipPlane, out QuiltFrustumGeometry.Geometry geometry), "scaled parent geometry builds");
            Vector3 expectedFocusCenter = camera.transform.position + camera.transform.rotation * Vector3.forward * settings.cameraDistance;
            Require(Vector3.Distance(Average(geometry.focusPlane), expectedFocusCenter) < 0.0001f, "focus center follows camera position and rotation, not inherited scale");
            Require(Mathf.Abs(Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[1]) - 2f * settings.cameraDistance * Mathf.Tan(settings.fieldOfView * Mathf.Deg2Rad * 0.5f)) < 0.0001f, "parent scale does not distort frustum dimensions");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(child);
            UnityEngine.Object.DestroyImmediate(parent);
        }
    }

    static void CheckInvalidInputsAreRejected()
    {
        GameObject go = new GameObject("invalid-authoring-test");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            QuiltSettings settings = new QuiltSettings();
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 0f, 100f, out _), "zero near clip is rejected");
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 1f, 1f, out _), "non-increasing clip range is rejected");
            settings.fieldOfView = float.NaN;
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 0.1f, 100f, out _), "non-finite fov is rejected");
            settings.fieldOfView = 60f;
            settings.cameraDistance = float.PositiveInfinity;
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 0.1f, 100f, out _), "non-finite focus distance is rejected");
            Require(!QuiltFrustumGeometry.TryBuild(null, settings, 0.1f, 100f, out _), "missing transform is rejected");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static void CheckInspectorPropertiesAndUndo()
    {
        GameObject go = new GameObject("authoring-inspector-test");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;

            SerializedObject cameraObject = new SerializedObject(camera);
            SerializedProperty near = cameraObject.FindProperty("near clip plane") ?? cameraObject.FindProperty("m_NearClipPlane");
            SerializedProperty far = cameraObject.FindProperty("far clip plane") ?? cameraObject.FindProperty("m_FarClipPlane");
            Require(near != null && far != null, "inspector resolves source camera clip properties");
            Debug.Log("Camera clip serialized paths: " + near.propertyPath + ", " + far.propertyPath);

            bool sceneDirtyBeforeRepaint = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty;
            Vector3 sourcePositionBeforeRepaint = camera.transform.position;
            Quaternion sourceRotationBeforeRepaint = camera.transform.rotation;
            for (int repaint = 0; repaint < 5; ++repaint)
                Require(QuiltFrustumGeometry.TryBuild(camera.transform, renderer.settings, camera.nearClipPlane, camera.farClipPlane, out _), "repaint geometry remains valid");
            Require(sceneDirtyBeforeRepaint == UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty, "geometry refresh does not dirty the scene");
            Require(camera.transform.position == sourcePositionBeforeRepaint && camera.transform.rotation == sourceRotationBeforeRepaint, "geometry refresh preserves the source camera pose");

            float previousDistance = renderer.settings.cameraDistance;
            int group = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Change ViewWeave Focus Distance");
            Undo.RecordObject(renderer, "Change ViewWeave Focus Distance");
            renderer.settings.cameraDistance = 7.125f;
            EditorUtility.SetDirty(renderer);
            Undo.FlushUndoRecordObjects();
            Require(Mathf.Abs(renderer.settings.cameraDistance - 7.125f) < 0.000001f, "numeric focus edit is applied");
            Undo.PerformUndo();
            Require(Mathf.Abs(renderer.settings.cameraDistance - previousDistance) < 0.000001f, "focus edit undo restores the exact value");
            Undo.RevertAllDownToGroup(group);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static Vector3 Average(Vector3[] points)
    {
        Vector3 sum = Vector3.zero;
        foreach (Vector3 point in points) sum += point;
        return sum / points.Length;
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
