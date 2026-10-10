using System;
using System.IO;
using ViewWeave;
using ViewWeave.BridgeWorkerValidation;
using ViewWeave.HDRP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.HighDefinition;

namespace ViewWeave.BridgeWorkerValidation.Editor
{
    public static class HdrpCameraBatchPerformanceBuilder
    {
        const string SourceScene = "Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.unity";
        const string PrototypeScene = "Assets/BridgeWorkerValidationGenerated/HdrpCameraBatchPerformance.unity";

        public static void BuildScene()
        {
            if (!File.Exists(SourceScene))
                throw new FileNotFoundException("Build the HDRP validation fixture first", SourceScene);

            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
                BridgeWorkerValidationRunner runner = UnityEngine.Object.FindAnyObjectByType<BridgeWorkerValidationRunner>(FindObjectsInactive.Include);
                if (runner == null) throw new InvalidOperationException("HDRP validation runner is missing from the generated fixture");

                SerializedObject runnerData = new SerializedObject(runner);
                Camera source = ReadReference<Camera>(runnerData, "sourceCamera");
                QuiltRenderer quilt = ReadReference<QuiltRenderer>(runnerData, "quilt");
                ViewWeaveRig rig = ReadReference<ViewWeaveRig>(runnerData, "rig");
                ViewWeaveBridgeSession session = ReadReference<ViewWeaveBridgeSession>(runnerData, "session");
                BridgeWorkerQuiltOutput output = ReadReference<BridgeWorkerQuiltOutput>(runnerData, "output");
                HdrpQuiltCamera guard = source == null ? null : source.GetComponent<HdrpQuiltCamera>();
                if (source == null || quilt == null || rig == null || session == null || output == null || guard == null)
                    throw new InvalidOperationException("HDRP fixture references are incomplete");

                runner.enabled = false;
                GameObject prototypeObject = source.gameObject;
                HdrpCameraBatchPerformancePrototype prototype = prototypeObject.GetComponent<HdrpCameraBatchPerformancePrototype>();
                if (prototype == null) prototype = prototypeObject.AddComponent<HdrpCameraBatchPerformancePrototype>();
                SerializedObject prototypeData = new SerializedObject(prototype);
                SetReference(prototypeData, "sourceCamera", source);
                SetReference(prototypeData, "quilt", quilt);
                SetReference(prototypeData, "sourceGuard", guard);
                SetReference(prototypeData, "rig", rig);
                SetReference(prototypeData, "session", session);
                SetReference(prototypeData, "output", output);
                SetReference(prototypeData, "originalRunner", runner);

                SerializedObject sessionData = new SerializedObject(session);
                SerializedProperty startOnEnable = sessionData.FindProperty("startOnEnable");
                if (startOnEnable == null) throw new InvalidOperationException("Bridge session startOnEnable field is unavailable");
                startOnEnable.boolValue = true;
                SerializedProperty quality = sessionData.FindProperty("quality");
                if (quality == null) throw new InvalidOperationException("Bridge session quality field is unavailable");
                quality.enumValueIndex = (int)QuiltQualityPreset.Balanced;
                sessionData.ApplyModifiedPropertiesWithoutUndo();

                Camera[] cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
                Camera mainCamera = null;
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera candidate = cameras[i];
                    if (candidate == source) continue;
                    if (mainCamera != null)
                        throw new InvalidOperationException("HDRP fixture contains multiple cameras in addition to the source camera");
                    mainCamera = candidate;
                }
                if (mainCamera == null)
                    mainCamera = CreateMainCamera(source);
                else if (mainCamera.tag != "MainCamera")
                    throw new InvalidOperationException("HDRP fixture contains an additional camera that is not tagged MainCamera");
                SetReference(prototypeData, "mainCamera", mainCamera);
                prototypeData.ApplyModifiedPropertiesWithoutUndo();

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, PrototypeScene, false))
                    throw new InvalidOperationException("Could not save the HDRP camera-batch prototype scene");
                AssetDatabase.Refresh();
                Debug.Log("HDRP camera-batch prototype scene created: " + PrototypeScene);
            }
            finally
            {
                if (previousSetup != null && previousSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
        }

        static Camera CreateMainCamera(Camera source)
        {
            GameObject cameraObject = new GameObject("BridgeWorkerValidation.HDRP.MainCamera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.CopyFrom(source);
            camera.enabled = true;
            camera.targetTexture = null;
            HDAdditionalCameraData sourceData = source.GetComponent<HDAdditionalCameraData>();
            if (sourceData != null)
            {
                HDAdditionalCameraData data = cameraObject.AddComponent<HDAdditionalCameraData>();
                sourceData.CopyTo(data);
            }
            return camera;
        }

        static T ReadReference<T>(SerializedObject serializedObject, string propertyName) where T : UnityEngine.Object
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            return property == null ? null : property.objectReferenceValue as T;
        }

        static void SetReference(SerializedObject serializedObject, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            if (property == null) throw new InvalidOperationException("Prototype field is unavailable: " + propertyName);
            property.objectReferenceValue = value;
        }
    }
}
