using UnityEditor;
using UnityEngine;
using ViewWeave.Editor;

namespace ViewWeave.Editor
{
    public static class ViewWeaveBridgeRigMenu
    {
        [MenuItem("GameObject/ViewWeave/Bridge Rig", false, 11)]
        static void CreateBridgeRig(MenuCommand command)
        {
            if (!ViewWeaveRigProviderRegistry.HasSupportedProvider())
            {
                Debug.LogError("ViewWeave Bridge Rig requires a supported URP or HDRP render pipeline.");
                return;
            }
            GameObject parent = command.context as GameObject;
            GameObject target = new GameObject("ViewWeave Bridge Rig");
            target.SetActive(false);
            if (parent != null) GameObjectUtility.SetParentAndAlign(target, parent);
            Undo.RegisterCreatedObjectUndo(target, "Create ViewWeave Bridge Rig");
            Camera sourceCamera = target.AddComponent<Camera>();
            QuiltRenderer quilt = target.AddComponent<QuiltRenderer>();
            CopyMainCameraFraming(sourceCamera, quilt);
            ViewWeaveRig rig = target.AddComponent<ViewWeaveRig>();
            BridgeWorkerQuiltOutput output = target.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            ViewWeaveBridgeSession session = target.AddComponent<ViewWeaveBridgeSession>();
            if (!ViewWeaveRigProviderRegistry.TryAddAdapter(target, out MonoBehaviour adapter))
            {
                Undo.DestroyObjectImmediate(target);
                Debug.LogError("ViewWeave Bridge Rig requires a supported URP or HDRP render pipeline.");
                return;
            }
            try
            {
                rig.SetGuard(adapter);
                rig.SetOutput(output);
                if (!rig.ConfigureComponents()) throw new System.InvalidOperationException("ViewWeave Bridge Rig configuration failed");
                session.startOnEnable = true;
                target.SetActive(true);
                Selection.activeGameObject = target;
            }
            catch (System.Exception exception)
            {
                Undo.DestroyObjectImmediate(target);
                Debug.LogError("ViewWeave Bridge Rig creation failed: " + Sanitize(exception.Message));
            }
        }

        static void CopyMainCameraFraming(Camera sourceCamera, QuiltRenderer quilt)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
            {
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = sourceCamera.fieldOfView;
                return;
            }
            sourceCamera.transform.SetPositionAndRotation(mainCamera.transform.position, mainCamera.transform.rotation);
            sourceCamera.fieldOfView = mainCamera.fieldOfView;
            sourceCamera.nearClipPlane = mainCamera.nearClipPlane;
            sourceCamera.farClipPlane = mainCamera.farClipPlane;
            sourceCamera.cullingMask = mainCamera.cullingMask;
            quilt.settings.fieldOfView = mainCamera.fieldOfView;
            quilt.settings.cameraDistance = FocusDepthOrDefault(sourceCamera, Selection.activeGameObject, 10f);
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
