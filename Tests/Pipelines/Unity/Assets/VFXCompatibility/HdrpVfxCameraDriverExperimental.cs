#if UNITY_INCLUDE_TESTS
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.VFX;
using LookingGlass.Community;

namespace LookingGlass.VFXCompatibility
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(QuiltRenderer))]
    sealed class HdrpVfxCameraDriverExperimental : MonoBehaviour
    {
        public bool enableDriver;
        public LayerMask vfxLayers;
        public bool runWhenManualRendering;
        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        QuiltRenderer quilt;
        Camera source;
        Camera driver;
        RenderTexture texture;
        GameObject driverObject;
        bool errorLogged;

        void Awake()
        {
            quilt = GetComponent<QuiltRenderer>();
            source = quilt == null ? GetComponent<Camera>() : quilt.sourceCamera;
            LastError = string.Empty;
        }

        void LateUpdate()
        {
            if (!enableDriver || (!runWhenManualRendering && (quilt == null || !quilt.autoRender)))
            {
                Stop();
                return;
            }
            if (!Validate() || !Ensure())
            {
                Stop();
                return;
            }
            driverObject.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            driver.fieldOfView = source.fieldOfView;
            driver.aspect = source.aspect > 0f && float.IsFinite(source.aspect) ? source.aspect : 1f;
            driver.nearClipPlane = source.nearClipPlane;
            driver.farClipPlane = source.farClipPlane;
            driver.cullingMask = source.cullingMask & vfxLayers.value;
            driver.GetComponent<HDAdditionalCameraData>().volumeLayerMask = source.GetComponent<HDAdditionalCameraData>().volumeLayerMask;
            VFXManager.PrepareCamera(driver);
            driver.enabled = true;
            IsRunning = true;
            LastError = string.Empty;
            errorLogged = false;
        }

        bool Validate()
        {
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)) return Fail("HDRP render pipeline asset is required");
            if (quilt == null) return Fail("QuiltRenderer is required");
            source = quilt.sourceCamera != null ? quilt.sourceCamera : GetComponent<Camera>();
            if (source == null || source.gameObject != gameObject) return Fail("source camera must be on the driver GameObject");
            if (source.enabled) return Fail("source camera must be disabled");
            if (source.orthographic) return Fail("source camera must be perspective");
            if (source.GetComponent<HDAdditionalCameraData>() == null) return Fail("source camera requires HDAdditionalCameraData");
            if (vfxLayers.value == 0 || (source.cullingMask & vfxLayers.value) == 0) return Fail("vfxLayers must intersect source culling mask");
            return true;
        }

        bool Ensure()
        {
            if (driverObject != null && driver != null && texture != null) return true;
            try
            {
                driverObject = new GameObject("ViewWeave.Test.HDRP.VfxDriver");
                driverObject.hideFlags = HideFlags.HideAndDontSave;
                driver = driverObject.AddComponent<Camera>();
                HDAdditionalCameraData data = driverObject.AddComponent<HDAdditionalCameraData>();
                data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                data.backgroundColorHDR = Color.black;
                data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
                texture = new RenderTexture(1, 1, 24, RenderTextureFormat.ARGB32) { name = "ViewWeave.Test.HDRP.VfxDriver" };
                texture.Create();
                if (!texture.IsCreated()) throw new InvalidOperationException("driver render texture creation failed");
                driver.targetTexture = texture;
                driver.enabled = false;
                driver.allowHDR = false;
                driver.allowMSAA = false;
                driver.useOcclusionCulling = false;
                driver.clearFlags = CameraClearFlags.SolidColor;
                driver.backgroundColor = Color.black;
                return true;
            }
            catch (Exception error)
            {
                Fail(error.GetType().Name + ": " + error.Message);
                Release();
                return false;
            }
        }

        void Stop() { IsRunning = false; if (driver != null) driver.enabled = false; }
        bool Fail(string message)
        {
            LastError = Sanitize(message);
            if (!errorLogged) { errorLogged = true; Debug.LogError("ViewWeave HDRP VFX test driver stopped: " + LastError); }
            return false;
        }
        void OnDisable() { Stop(); Release(); }
        void OnDestroy() { Stop(); Release(); }
        void Release()
        {
            if (driver != null) driver.targetTexture = null;
            if (texture != null) { if (RenderTexture.active == texture) RenderTexture.active = null; texture.Release(); DestroyImmediate(texture); texture = null; }
            if (driverObject != null) { DestroyImmediate(driverObject); driverObject = null; driver = null; }
        }
        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c);
            return chars.Count == 0 ? "unknown error" : new string(chars.ToArray());
        }
    }
}
#endif
