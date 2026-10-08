using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using ViewWeave.Editor;

namespace ViewWeave.Editor
{
    public static class ViewWeaveBridgeRigMenu
    {
        [MenuItem("GameObject/ViewWeave/Bridge Rig", false, 11)]
        static void CreateBridgeRig(MenuCommand command)
        {
            GameObject commandContext = command.context as GameObject;
            GameObject selectedObject = Selection.activeGameObject;
            Camera framingCamera = ResolveFramingCamera(commandContext, selectedObject);
            if (!ViewWeaveRigProviderRegistry.HasSupportedProvider())
            {
                Debug.LogError("ViewWeave Bridge Rig requires a supported URP or HDRP render pipeline.");
                return;
            }
            GameObject target = new GameObject("ViewWeave Bridge Rig");
            try
            {
                target.SetActive(false);
                if (commandContext != null) GameObjectUtility.SetParentAndAlign(target, commandContext);
                Undo.RegisterCreatedObjectUndo(target, "Create ViewWeave Bridge Rig");
                Camera sourceCamera = target.AddComponent<Camera>();
                QuiltRenderer quilt = target.AddComponent<QuiltRenderer>();
                CopyCameraFraming(sourceCamera, quilt, framingCamera, selectedObject);
                ViewWeaveRig rig = target.AddComponent<ViewWeaveRig>();
                BridgeWorkerQuiltOutput output = target.AddComponent<BridgeWorkerQuiltOutput>();
                output.initializeOnEnable = false;
                ViewWeaveBridgeSession session = target.AddComponent<ViewWeaveBridgeSession>();
                if (!ViewWeaveRigProviderRegistry.TryAddAdapter(target, framingCamera, out MonoBehaviour adapter))
                    throw new System.InvalidOperationException("ViewWeave Bridge Rig requires a supported URP or HDRP render pipeline");
                rig.SetGuard(adapter);
                rig.SetOutput(output);
                if (!rig.ConfigureComponents()) throw new System.InvalidOperationException("ViewWeave Bridge Rig configuration failed");
                session.startOnEnable = true;
                target.SetActive(true);
                InternalEditorUtility.SetIsInspectorExpanded(sourceCamera, false);
                InternalEditorUtility.SetIsInspectorExpanded(quilt, false);
                InternalEditorUtility.SetIsInspectorExpanded(output, false);
                InternalEditorUtility.SetIsInspectorExpanded(session, false);
                InternalEditorUtility.SetIsInspectorExpanded(adapter, false);
                InternalEditorUtility.SetIsInspectorExpanded(rig, true);
                Selection.activeGameObject = target;
            }
            catch (System.Exception exception)
            {
                Undo.DestroyObjectImmediate(target);
                Debug.LogError("ViewWeave Bridge Rig creation failed: " + Sanitize(exception.Message));
            }
        }

        static Camera ResolveFramingCamera(GameObject commandContext, GameObject selectedObject)
        {
            GameObject cameraContext = commandContext != null ? commandContext : selectedObject;
            Camera camera = cameraContext != null ? cameraContext.GetComponentInParent<Camera>(true) : null;
            return camera != null ? camera : Camera.main;
        }

        static void CopyCameraFraming(Camera sourceCamera, QuiltRenderer quilt, Camera framingCamera, GameObject selectedObject)
        {
            quilt.referenceCamera = framingCamera;
            quilt.followReferenceFieldOfView = true;
            if (framingCamera == null)
            {
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = sourceCamera.fieldOfView;
                return;
            }
            sourceCamera.transform.SetPositionAndRotation(framingCamera.transform.position, framingCamera.transform.rotation);
            sourceCamera.fieldOfView = framingCamera.fieldOfView;
            sourceCamera.nearClipPlane = framingCamera.nearClipPlane;
            sourceCamera.farClipPlane = framingCamera.farClipPlane;
            sourceCamera.cullingMask = framingCamera.cullingMask;
            quilt.settings.fieldOfView = framingCamera.fieldOfView;
            quilt.settings.cameraDistance = FocusDepthOrDefault(sourceCamera, selectedObject, 10f);
        }

        static float FocusDepthOrDefault(Camera sourceCamera, GameObject selected, float fallback)
        {
            Renderer renderer = selected == null ? null : selected.GetComponent<Renderer>();
            if (renderer == null || !renderer.enabled) return fallback;
            float depth = Vector3.Dot(renderer.bounds.center - sourceCamera.transform.position, sourceCamera.transform.forward);
            if (depth <= sourceCamera.nearClipPlane || depth >= sourceCamera.farClipPlane) return fallback;
            return depth;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }
    }
}
