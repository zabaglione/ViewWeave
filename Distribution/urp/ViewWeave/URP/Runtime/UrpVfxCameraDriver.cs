using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using LookingGlass.Community;

namespace LookingGlass.Community.URP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(QuiltRenderer))]
    public sealed class UrpVfxCameraDriver : MonoBehaviour
    {
        public bool enableDriver;
        public LayerMask vfxLayers;
        [Range(1f, 170f)] public float driverFieldOfView = 90f;
        public bool runWhenManualRendering;

        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }
        public Camera DriverCamera => driverCamera;

        QuiltRenderer quiltRenderer;
        Camera sourceCamera;
        Camera driverCamera;
        RenderTexture driverTexture;
        GameObject driverObject;
        bool errorLogged;

        void Awake()
        {
            quiltRenderer = GetComponent<QuiltRenderer>();
            sourceCamera = quiltRenderer != null ? quiltRenderer.sourceCamera : GetComponent<Camera>();
            LastError = string.Empty;
        }

        void OnEnable()
        {
            IsRunning = false;
            LastError = string.Empty;
            errorLogged = false;
        }

        void LateUpdate()
        {
            if (!enableDriver || (!runWhenManualRendering && (quiltRenderer == null || !quiltRenderer.autoRender)))
            {
                StopRunning();
                return;
            }
            if (!ValidateConfiguration())
            {
                StopRunning();
                return;
            }
            if (!EnsureDriver())
            {
                StopRunning();
                return;
            }
            SyncDriver();
            IsRunning = driverCamera != null && driverCamera.enabled && driverTexture != null;
        }

        bool ValidateConfiguration()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
                return Fail("URP render pipeline asset is required");
            if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
                return Fail("VFX Graph requires Linear color space; URP Gamma is unsupported");
            if (quiltRenderer == null)
                return Fail("QuiltRenderer is required");
            sourceCamera = quiltRenderer.sourceCamera != null ? quiltRenderer.sourceCamera : GetComponent<Camera>();
            if (sourceCamera == null)
                return Fail("source camera is required");
            if (sourceCamera.gameObject != gameObject)
                return Fail("source camera must be on the driver GameObject");
            if (sourceCamera.enabled)
                return Fail("source camera must be disabled");
            if (sourceCamera.orthographic)
                return Fail("source camera must be perspective");
            if (vfxLayers.value == 0 || (sourceCamera.cullingMask & vfxLayers.value) == 0)
                return Fail("vfxLayers must intersect the source camera culling mask");
            if (!float.IsFinite(driverFieldOfView) || driverFieldOfView < 1f || driverFieldOfView > 170f)
                return Fail("driverFieldOfView must be finite and between 1 and 170 degrees");
            return true;
        }

        bool EnsureDriver()
        {
            if (driverObject != null && driverCamera != null && driverTexture != null) return true;
            try
            {
                driverObject = new GameObject("ViewWeave.URP.VfxDriverCamera");
                driverObject.hideFlags = HideFlags.HideAndDontSave;
                driverCamera = driverObject.AddComponent<Camera>();
                UniversalAdditionalCameraData data = driverObject.AddComponent<UniversalAdditionalCameraData>();
                data.renderType = CameraRenderType.Base;
                data.renderPostProcessing = false;
                data.renderShadows = false;
                data.antialiasing = AntialiasingMode.None;
                data.volumeLayerMask = 0;
                data.requiresDepthOption = CameraOverrideOption.Off;
                data.requiresColorOption = CameraOverrideOption.Off;
                driverTexture = new RenderTexture(1, 1, 24, RenderTextureFormat.ARGB32) { name = "ViewWeave.URP.VfxDriver" };
                driverTexture.Create();
                if (!driverTexture.IsCreated()) throw new InvalidOperationException("driver render texture creation failed");
                driverCamera.targetTexture = driverTexture;
                driverCamera.enabled = false;
                driverCamera.allowHDR = false;
                driverCamera.allowMSAA = false;
                driverCamera.useOcclusionCulling = false;
                driverCamera.depth = -100f;
                driverCamera.clearFlags = CameraClearFlags.SolidColor;
                driverCamera.backgroundColor = Color.black;
                return true;
            }
            catch (Exception error)
            {
                Fail(Sanitize(error));
                ReleaseDriver();
                return false;
            }
        }

        void SyncDriver()
        {
            driverObject.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
            driverCamera.fieldOfView = driverFieldOfView;
            driverCamera.aspect = sourceCamera.aspect > 0f && float.IsFinite(sourceCamera.aspect) ? sourceCamera.aspect : 1f;
            driverCamera.nearClipPlane = sourceCamera.nearClipPlane;
            driverCamera.farClipPlane = sourceCamera.farClipPlane;
            driverCamera.cullingMask = sourceCamera.cullingMask & vfxLayers.value;
            driverCamera.clearFlags = CameraClearFlags.SolidColor;
            driverCamera.backgroundColor = Color.black;
            driverCamera.enabled = true;
            LastError = string.Empty;
            errorLogged = false;
        }

        void StopRunning()
        {
            IsRunning = false;
            if (driverCamera != null) driverCamera.enabled = false;
        }

        bool Fail(string message)
        {
            LastError = Sanitize(message);
            if (!errorLogged)
            {
                errorLogged = true;
                Debug.LogError("ViewWeave URP VFX driver stopped: " + LastError);
            }
            return false;
        }

        void OnDisable()
        {
            StopRunning();
            ReleaseDriver();
        }

        void OnDestroy()
        {
            StopRunning();
            ReleaseDriver();
        }

        void ReleaseDriver()
        {
            if (driverCamera != null) driverCamera.targetTexture = null;
            if (driverTexture != null)
            {
                if (RenderTexture.active == driverTexture) RenderTexture.active = null;
                driverTexture.Release();
                if (Application.isPlaying) Destroy(driverTexture); else DestroyImmediate(driverTexture);
                driverTexture = null;
            }
            if (driverObject != null)
            {
                if (Application.isPlaying) Destroy(driverObject); else DestroyImmediate(driverObject);
                driverObject = null;
                driverCamera = null;
            }
        }

        static string Sanitize(Exception error)
        {
            string value = error == null ? "unknown error" : error.GetType().Name + ": " + error.Message;
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }

        static string Sanitize(string value)
        {
            var chars = new System.Collections.Generic.List<char>(value == null ? 0 : value.Length);
            if (!string.IsNullOrEmpty(value)) foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return chars.Count == 0 ? "unknown error" : new string(chars.ToArray());
        }
    }
}
