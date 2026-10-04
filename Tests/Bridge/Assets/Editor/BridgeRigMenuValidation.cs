using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;
using ViewWeave;
using ViewWeave.Editor;

public static class BridgeRigMenuValidation
{
    public static void Run()
    {
        GameObject mainObject = new GameObject("Validation Main Camera");
        GameObject selectedObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        GameObject targetObject = new GameObject("Validation Rig");
        GameObject[] priorMainCameras = GameObject.FindGameObjectsWithTag("MainCamera");
        try
        {
            foreach (GameObject priorMainCamera in priorMainCameras)
            {
                if (priorMainCamera != null) priorMainCamera.tag = "Untagged";
            }
            mainObject.tag = "MainCamera";
            Camera main = mainObject.AddComponent<Camera>();
            main.transform.SetPositionAndRotation(new Vector3(1f, 2f, -4f), Quaternion.Euler(10f, 20f, 0f));
            main.fieldOfView = 61f;
            main.nearClipPlane = 0.2f;
            main.farClipPlane = 77f;
            main.cullingMask = 123;
            selectedObject.transform.position = new Vector3(1f, 2f, 6f);
            Selection.activeGameObject = selectedObject;
            Camera source = targetObject.AddComponent<Camera>();
            QuiltRenderer quilt = targetObject.AddComponent<QuiltRenderer>();
            MethodInfo copy = typeof(ViewWeaveBridgeRigMenu).GetMethod("CopyMainCameraFraming", BindingFlags.NonPublic | BindingFlags.Static);
            if (copy == null) throw new InvalidOperationException("menu framing helper is missing");
            copy.Invoke(null, new object[] { source, quilt });
            if (source.transform.position != main.transform.position || source.transform.rotation != main.transform.rotation) throw new InvalidOperationException("main camera transform was not copied");
            if (source.fieldOfView != main.fieldOfView || source.nearClipPlane != main.nearClipPlane || source.farClipPlane != main.farClipPlane || source.cullingMask != main.cullingMask) throw new InvalidOperationException("main camera framing was not copied");
            if (Mathf.Abs(quilt.settings.fieldOfView - main.fieldOfView) > 0.001f) throw new InvalidOperationException("quilt field of view was not synchronized");
            if (!(quilt.settings.cameraDistance > source.nearClipPlane && quilt.settings.cameraDistance < source.farClipPlane)) throw new InvalidOperationException("selected renderer focus depth is invalid");
            selectedObject.transform.position = main.transform.position - main.transform.forward * 5f;
            copy.Invoke(null, new object[] { source, quilt });
            if (Mathf.Abs(quilt.settings.cameraDistance - 10f) > 0.001f) throw new InvalidOperationException("invalid selected renderer depth did not use fallback");
            targetObject.SetActive(false);
            ViewWeaveRig rig = targetObject.AddComponent<ViewWeaveRig>();
            BridgeWorkerQuiltOutput output = targetObject.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            targetObject.SetActive(true);
            ViewWeaveBridgeSession session = targetObject.AddComponent<ViewWeaveBridgeSession>();
            MethodInfo resolveDependencies = typeof(ViewWeaveBridgeSession).GetMethod("ResolveDependencies", BindingFlags.NonPublic | BindingFlags.Instance);
            if (resolveDependencies == null) throw new InvalidOperationException("session dependency resolver is missing");
            resolveDependencies.Invoke(session, null);
            Assert.IsFalse(EditorApplication.isPlaying);
            Assert.IsTrue(targetObject.activeInHierarchy);
            Assert.IsTrue(rig.isActiveAndEnabled);
            Assert.IsTrue(output.isActiveAndEnabled);
            Assert.IsTrue(session.isActiveAndEnabled);
            Assert.IsFalse(session.StartSession());
            Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Fault, session.State);
            if (string.IsNullOrEmpty(session.LastError) || !session.LastError.Contains("Windows D3D11 Play mode")) throw new InvalidOperationException("edit mode session rejection message is missing: " + session.LastError);
            foreach (char character in session.LastError)
            {
                if (character > 0x7f) throw new InvalidOperationException("edit mode session rejection message is not ASCII");
            }
            UnityEngine.Object.DestroyImmediate(targetObject);
            UnityEngine.Object.DestroyImmediate(mainObject);
            Selection.activeGameObject = null;
            targetObject = new GameObject("Validation Rig Without Main Camera");
            source = targetObject.AddComponent<Camera>();
            quilt = targetObject.AddComponent<QuiltRenderer>();
            copy.Invoke(null, new object[] { source, quilt });
            if (Mathf.Abs(quilt.settings.cameraDistance - 3f) > 0.001f) throw new InvalidOperationException("no-main-camera default focus depth is invalid");
            foreach (GameObject priorMainCamera in priorMainCameras)
            {
                if (priorMainCamera != null) priorMainCamera.tag = "MainCamera";
            }
            Debug.Log("BridgeRigMenuValidation pass");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(targetObject);
            UnityEngine.Object.DestroyImmediate(selectedObject);
            UnityEngine.Object.DestroyImmediate(mainObject);
            foreach (GameObject priorMainCamera in priorMainCameras)
            {
                if (priorMainCamera != null) priorMainCamera.tag = "MainCamera";
            }
            Selection.activeObject = null;
        }
    }
}
