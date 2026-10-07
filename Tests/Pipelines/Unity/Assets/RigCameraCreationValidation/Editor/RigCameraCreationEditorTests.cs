#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using ViewWeave;
using ViewWeave.Editor;
using ViewWeave.URP;

namespace ViewWeave.RigCameraCreationValidation
{
    public sealed class RigCameraCreationEditorTests
    {
        readonly List<UnityEngine.Object> ownedObjects = new List<UnityEngine.Object>();
        RenderPipelineAsset previousDefaultPipeline;
        RenderPipelineAsset previousQualityPipeline;
        GameObject previousSelection;
        Scene previewScene;
        UniversalRenderPipelineAsset pipeline;
        UniversalRendererData renderer0;
        UniversalRendererData renderer1;

        [SetUp]
        public void SetUp()
        {
            previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            previousQualityPipeline = QualitySettings.renderPipeline;
            previousSelection = Selection.activeGameObject;
            previewScene = EditorSceneManager.NewPreviewScene();
            renderer0 = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer0.name = "Rig Camera Test Renderer 0";
            renderer0.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            renderer1 = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer1.name = "Rig Camera Test Renderer 1";
            renderer1.postProcessData = renderer0.postProcessData;
            pipeline = UniversalRenderPipelineAsset.Create(renderer0);
            pipeline.name = "Rig Camera Test Pipeline";
            ownedObjects.Add(pipeline);
            ownedObjects.Add(renderer0);
            ownedObjects.Add(renderer1);

            SerializedObject pipelineData = new SerializedObject(pipeline);
            SerializedProperty rendererList = pipelineData.FindProperty("m_RendererDataList");
            Assert.That(rendererList, Is.Not.Null, "URP renderer data list must be available");
            rendererList.arraySize = 2;
            rendererList.GetArrayElementAtIndex(0).objectReferenceValue = renderer0;
            rendererList.GetArrayElementAtIndex(1).objectReferenceValue = renderer1;
            pipelineData.ApplyModifiedPropertiesWithoutUndo();

            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.SameAs(pipeline));
        }

        [TearDown]
        public void TearDown()
        {
            Selection.activeGameObject = previousSelection;
            QualitySettings.renderPipeline = previousQualityPipeline;
            GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline;
            if (previewScene.IsValid() && previewScene.isLoaded) EditorSceneManager.ClosePreviewScene(previewScene);
            for (int i = ownedObjects.Count - 1; i >= 0; --i)
                if (ownedObjects[i] != null) UnityEngine.Object.DestroyImmediate(ownedObjects[i]);
            ownedObjects.Clear();
        }

        [Test]
        public void FramingCameraResolutionPrefersCommandContextThenSelectionThenMain()
        {
            Camera mainCamera = CreateCamera("Main Camera", new Vector3(0f, 0f, -10f));
            mainCamera.tag = "MainCamera";
            Camera contextCamera = CreateCamera("Selected Non-Main Camera", new Vector3(3f, 2f, -5f));
            GameObject contextChild = CreateObject("Context Child");
            contextChild.transform.SetParent(contextCamera.transform, false);
            Camera selectionCamera = CreateCamera("Selection Camera", new Vector3(-2f, 1f, -4f));
            GameObject selectionChild = CreateObject("Selection Child");
            selectionChild.transform.SetParent(selectionCamera.transform, false);
            GameObject unrelatedContext = CreateObject("Unrelated Context");

            Assert.That(ResolveCamera(contextChild, selectionCamera.gameObject), Is.SameAs(contextCamera));
            Assert.That(ResolveCamera(null, selectionChild), Is.SameAs(selectionCamera));
            Assert.That(ResolveCamera(unrelatedContext, selectionChild), Is.SameAs(Camera.main));
        }

        [Test]
        public void CameraFramingCopyUsesOneResolvedCameraAndLeavesItUnchanged()
        {
            Camera framingCamera = CreateCamera("Framing Camera", new Vector3(4.5f, -1.25f, 7.75f));
            framingCamera.transform.rotation = Quaternion.Euler(11f, 37f, -6f);
            framingCamera.fieldOfView = 73f;
            framingCamera.nearClipPlane = 0.23f;
            framingCamera.farClipPlane = 143f;
            framingCamera.cullingMask = 1 << 12;
            Vector3 originalPosition = framingCamera.transform.position;
            Quaternion originalRotation = framingCamera.transform.rotation;
            float originalFov = framingCamera.fieldOfView;
            float originalNear = framingCamera.nearClipPlane;
            float originalFar = framingCamera.farClipPlane;
            int originalCullingMask = framingCamera.cullingMask;

            GameObject target = CreateObject("Inactive Rig");
            target.SetActive(false);
            Camera targetCamera = target.AddComponent<Camera>();
            QuiltRenderer renderer = target.AddComponent<QuiltRenderer>();
            InvokeMenuHelper("CopyCameraFraming", targetCamera, renderer, framingCamera, null);

            Assert.That(targetCamera.transform.position, Is.EqualTo(originalPosition));
            Assert.That(targetCamera.transform.rotation, Is.EqualTo(originalRotation));
            Assert.That(targetCamera.fieldOfView, Is.EqualTo(originalFov).Within(0.0001f));
            Assert.That(targetCamera.nearClipPlane, Is.EqualTo(originalNear).Within(0.0001f));
            Assert.That(targetCamera.farClipPlane, Is.EqualTo(originalFar).Within(0.0001f));
            Assert.That(targetCamera.cullingMask, Is.EqualTo(originalCullingMask));
            Assert.That(renderer.settings.fieldOfView, Is.EqualTo(originalFov).Within(0.0001f));
            Assert.That(framingCamera.transform.position, Is.EqualTo(originalPosition));
            Assert.That(framingCamera.transform.rotation, Is.EqualTo(originalRotation));
            Assert.That(framingCamera.fieldOfView, Is.EqualTo(originalFov).Within(0.0001f));
            Assert.That(framingCamera.nearClipPlane, Is.EqualTo(originalNear).Within(0.0001f));
            Assert.That(framingCamera.farClipPlane, Is.EqualTo(originalFar).Within(0.0001f));
            Assert.That(framingCamera.cullingMask, Is.EqualTo(originalCullingMask));
        }

        [Test]
        public void RigMenuUsesTheResolvedCameraForBothFramingAndUrpDataCopy()
        {
            Camera mainCamera = CreateCamera("Main Camera", new Vector3(0f, 0f, -20f));
            mainCamera.tag = "MainCamera";
            Camera framingCamera = CreateCamera("Context Framing Camera", new Vector3(4f, 2f, -8f));
            framingCamera.transform.rotation = Quaternion.Euler(5f, 29f, 1f);
            framingCamera.fieldOfView = 69f;
            framingCamera.nearClipPlane = 0.21f;
            framingCamera.farClipPlane = 119f;
            framingCamera.cullingMask = 1 << 10;
            UniversalAdditionalCameraData sourceData = framingCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            sourceData.SetRenderer(1);
            sourceData.volumeLayerMask = 1 << 6;
            sourceData.renderPostProcessing = true;
            Vector3 sourcePosition = framingCamera.transform.position;
            Quaternion sourceRotation = framingCamera.transform.rotation;

            GameObject contextChild = CreateObject("Right Click Context");
            contextChild.transform.SetParent(framingCamera.transform, false);
            Selection.activeGameObject = mainCamera.gameObject;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Test ViewWeave Rig Camera Copy");
            try
            {
                InvokeMenuHelper("CreateBridgeRig", new MenuCommand(contextChild, 0));
                GameObject rigObject = Selection.activeGameObject;
                Assert.That(rigObject, Is.Not.Null);
                Assert.That(rigObject.name, Is.EqualTo("ViewWeave Bridge Rig"));
                Camera rigCamera = rigObject.GetComponent<Camera>();
                Assert.That(rigCamera.transform.position, Is.EqualTo(sourcePosition));
                Assert.That(rigCamera.transform.rotation, Is.EqualTo(sourceRotation));
                Assert.That(rigCamera.fieldOfView, Is.EqualTo(framingCamera.fieldOfView).Within(0.0001f));
                Assert.That(rigCamera.nearClipPlane, Is.EqualTo(framingCamera.nearClipPlane).Within(0.0001f));
                Assert.That(rigCamera.farClipPlane, Is.EqualTo(framingCamera.farClipPlane).Within(0.0001f));
                Assert.That(rigCamera.cullingMask, Is.EqualTo(framingCamera.cullingMask));
                UniversalAdditionalCameraData rigData = rigObject.GetComponent<UniversalAdditionalCameraData>();
                Assert.That(GetRendererIndex(rigData), Is.EqualTo(1));
                Assert.That(rigData.volumeLayerMask, Is.EqualTo(sourceData.volumeLayerMask));
                Assert.That(rigData.renderPostProcessing, Is.True);
                Assert.That(framingCamera.transform.position, Is.EqualTo(sourcePosition));
                Assert.That(framingCamera.transform.rotation, Is.EqualTo(sourceRotation));
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void UrpProviderCopiesRendererAndCameraOptionsAndPreservesUnsupportedStack()
        {
            Camera framingCamera = CreateCamera("URP Framing Camera", Vector3.zero);
            framingCamera.transform.position = new Vector3(2f, -1f, 4f);
            framingCamera.transform.rotation = Quaternion.Euler(8f, 25f, 3f);
            framingCamera.fieldOfView = 68f;
            framingCamera.nearClipPlane = 0.17f;
            framingCamera.farClipPlane = 91f;
            framingCamera.cullingMask = 1 << 8;
            UniversalAdditionalCameraData sourceData = framingCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            sourceData.SetRenderer(1);
            sourceData.volumeLayerMask = 1 << 9;
            GameObject volumeAnchor = CreateObject("Volume Anchor");
            sourceData.volumeTrigger = volumeAnchor.transform;
            sourceData.renderPostProcessing = true;
            sourceData.renderShadows = false;
            sourceData.requiresDepthTexture = true;
            sourceData.requiresColorTexture = true;
            sourceData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            sourceData.antialiasingQuality = AntialiasingQuality.Medium;
            sourceData.stopNaN = true;
            sourceData.dithering = true;
            Camera overlayCamera = CreateCamera("Stack Overlay", Vector3.zero);
            UniversalAdditionalCameraData overlayData = overlayCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            overlayData.renderType = CameraRenderType.Overlay;
            sourceData.cameraStack.Add(overlayCamera);

            Vector3 sourcePosition = framingCamera.transform.position;
            Quaternion sourceRotation = framingCamera.transform.rotation;
            GameObject target = CreateObject("URP Rig Target");
            target.SetActive(false);
            Camera targetCamera = target.AddComponent<Camera>();
            QuiltRenderer quilt = target.AddComponent<QuiltRenderer>();
            InvokeMenuHelper("CopyCameraFraming", targetCamera, quilt, framingCamera, null);
            MonoBehaviour adapter;
            Assert.That(ViewWeaveRigProviderRegistry.TryAddAdapter(target, framingCamera, out adapter), Is.True);
            Assert.That(adapter, Is.TypeOf<UrpQuiltCamera>());

            UniversalAdditionalCameraData targetData = target.GetComponent<UniversalAdditionalCameraData>();
            Assert.That(GetRendererIndex(targetData), Is.EqualTo(1));
            Assert.That(targetData.volumeLayerMask, Is.EqualTo(sourceData.volumeLayerMask));
            Assert.That(targetData.volumeTrigger, Is.SameAs(volumeAnchor.transform));
            Assert.That(targetData.renderPostProcessing, Is.True);
            Assert.That(targetData.renderShadows, Is.False);
            Assert.That(targetData.requiresDepthTexture, Is.True);
            Assert.That(targetData.requiresColorTexture, Is.True);
            Assert.That(targetData.antialiasing, Is.EqualTo(AntialiasingMode.SubpixelMorphologicalAntiAliasing));
            Assert.That(targetData.antialiasingQuality, Is.EqualTo(AntialiasingQuality.Medium));
            Assert.That(targetData.stopNaN, Is.True);
            Assert.That(targetData.dithering, Is.True);
            Assert.That(targetData.cameraStack, Has.Count.EqualTo(1));
            Assert.That(targetData.cameraStack[0], Is.SameAs(overlayCamera));
            Assert.That(targetCamera.transform.position, Is.EqualTo(sourcePosition));
            Assert.That(targetCamera.transform.rotation, Is.EqualTo(sourceRotation));
            Assert.That(targetCamera.fieldOfView, Is.EqualTo(framingCamera.fieldOfView).Within(0.0001f));
            Assert.That(targetCamera.nearClipPlane, Is.EqualTo(framingCamera.nearClipPlane).Within(0.0001f));
            Assert.That(targetCamera.farClipPlane, Is.EqualTo(framingCamera.farClipPlane).Within(0.0001f));
            Assert.That(targetCamera.cullingMask, Is.EqualTo(framingCamera.cullingMask));
            Assert.That(framingCamera.transform.position, Is.EqualTo(sourcePosition));
            Assert.That(framingCamera.transform.rotation, Is.EqualTo(sourceRotation));
            Assert.Throws<NotSupportedException>(() => ((UrpQuiltCamera)adapter).ValidateForQuilt());
        }

        [Test]
        public void UrpProviderPreservesOverlayTypeAndGuardRejectsIt()
        {
            Camera framingCamera = CreateCamera("Overlay Framing Camera", Vector3.zero);
            UniversalAdditionalCameraData sourceData = framingCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            sourceData.renderType = CameraRenderType.Overlay;
            GameObject target = CreateObject("Overlay Rig Target");
            target.SetActive(false);
            target.AddComponent<Camera>();
            Assert.That(ViewWeaveRigProviderRegistry.TryAddAdapter(target, framingCamera, out MonoBehaviour adapter), Is.True);

            UniversalAdditionalCameraData targetData = target.GetComponent<UniversalAdditionalCameraData>();
            Assert.That(targetData.renderType, Is.EqualTo(CameraRenderType.Overlay));
            Assert.Throws<NotSupportedException>(() => ((UrpQuiltCamera)adapter).ValidateForQuilt());
        }

        [Test]
        public void UrpProviderLeavesDefaultsWhenFramingCameraHasNoAdditionalData()
        {
            Camera framingCamera = CreateCamera("Plain Framing Camera", Vector3.zero);
            GameObject target = CreateObject("Default URP Rig Target");
            target.SetActive(false);
            target.AddComponent<Camera>();
            Assert.That(ViewWeaveRigProviderRegistry.TryAddAdapter(target, framingCamera, out _), Is.True);

            UniversalAdditionalCameraData targetData = target.GetComponent<UniversalAdditionalCameraData>();
            Assert.That(targetData.renderType, Is.EqualTo(CameraRenderType.Base));
            Assert.That(targetData.renderPostProcessing, Is.False);
            Assert.That(targetData.renderShadows, Is.True);
            Assert.That(targetData.antialiasing, Is.EqualTo(AntialiasingMode.None));
            Assert.That(targetData.volumeLayerMask.value, Is.EqualTo(1));
        }

        Camera CreateCamera(string name, Vector3 position)
        {
            GameObject gameObject = CreateObject(name);
            Camera camera = gameObject.AddComponent<Camera>();
            camera.transform.position = position;
            return camera;
        }

        GameObject CreateObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            SceneManager.MoveGameObjectToScene(gameObject, previewScene);
            return gameObject;
        }

        static Camera ResolveCamera(GameObject commandContext, GameObject selection)
        {
            return (Camera)InvokeMenuHelper("ResolveFramingCamera", commandContext, selection);
        }

        static int GetRendererIndex(UniversalAdditionalCameraData data)
        {
            SerializedObject serializedData = new SerializedObject(data);
            SerializedProperty rendererIndex = serializedData.FindProperty("m_RendererIndex");
            Assert.That(rendererIndex, Is.Not.Null);
            return rendererIndex.intValue;
        }

        static object InvokeMenuHelper(string methodName, params object[] args)
        {
            MethodInfo method = typeof(ViewWeaveBridgeRigMenu).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Rig menu helper was not found: " + methodName);
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { throw exception.InnerException; }
        }
    }
}
#endif
