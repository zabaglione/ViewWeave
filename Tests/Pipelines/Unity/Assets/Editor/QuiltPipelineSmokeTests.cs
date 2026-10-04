#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using LookingGlass.Community;

public static class QuiltPipelineSmokeEntryPoints
{
    static bool sUseHdrpLit;
    public static void RunHdrp() { sUseHdrpLit = true; RunHdrpCore(); }
    public static void RunHdrpUnlit() { sUseHdrpLit = false; RunHdrpCore(); }

    static void RunHdrpCore()
    {
        var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
        var oldPipeline = GraphicsSettings.defaultRenderPipeline;
        var oldQuality = QualitySettings.renderPipeline;
        GraphicsSettings.defaultRenderPipeline = asset;
        QualitySettings.renderPipeline = asset;
        var root = new GameObject("LookingGlass.HDRP.Smoke");
        var lightObject = new GameObject("SmokeDirectional");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 10f;
        light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
        var hdLight = lightObject.AddComponent<HDAdditionalLightData>();
        hdLight.intensity = 10f;
        var cameraObject = new GameObject("SmokeCamera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 0f, -4f);
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        var cameraData = cameraObject.AddComponent<HDAdditionalCameraData>();
        cameraData.volumeLayerMask = 1;
        cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
        cameraData.backgroundColorHDR = Color.black;
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.GetComponent<Renderer>().sharedMaterial = CreateHdrpMaterial(new Color(0.8f, 0.35f, 0.05f, 1f));
        cube.transform.SetParent(root.transform);
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.SetPositionAndRotation(new Vector3(0.8f, 0.2f, 0.6f), Quaternion.identity);
        sphere.GetComponent<Renderer>().sharedMaterial = CreateHdrpMaterial(new Color(0.05f, 0.65f, 0.8f, 1f));
        sphere.transform.SetParent(root.transform);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.transform.position = new Vector3(0f, -0.75f, 0f);
        floor.transform.localScale = Vector3.one * 2f;
        floor.GetComponent<Renderer>().sharedMaterial = CreateHdrpMaterial(new Color(0.08f, 0.08f, 0.08f, 1f));
        floor.transform.SetParent(root.transform);
        var volumeObject = new GameObject("SmokeVolume");
        volumeObject.layer = 0;
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10f;
        volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
        var bloom = volume.profile.Add<UnityEngine.Rendering.HighDefinition.Bloom>(true);
        bloom.intensity.value = 0f;
        var exposure = volume.profile.Add<Exposure>(true);
        exposure.mode.value = ExposureMode.Fixed;
        exposure.mode.overrideState = true;
        exposure.fixedExposure.value = sUseHdrpLit ? 4f : 0f;
        exposure.fixedExposure.overrideState = true;
        var reflection = volume.profile.Add<ScreenSpaceReflection>(true);
        reflection.enabled.value = false;
        reflection.enabled.overrideState = true;
        var motionBlur = volume.profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
        motionBlur.active = true;
        motionBlur.intensity.value = 0f;
        var guard = cameraObject.AddComponent<LookingGlass.Community.HDRP.HdrpQuiltCamera>();
        var quilt = cameraObject.AddComponent<QuiltRenderer>();
        quilt.sourceCamera = camera;
        quilt.GuardBehaviour = guard;
        quilt.settings.quiltWidth = 256;
        quilt.settings.quiltHeight = 192;
        quilt.settings.columns = 4;
        quilt.settings.rows = 2;
        quilt.settings.views = 8;
        quilt.settings.viewAspect = 4f / 3f;
        quilt.settings.cameraDistance = 4f;
        Debug.Log("HDRP setup lit=" + sUseHdrpLit + " light=" + light.intensity + " hdLight=" + lightObject.GetComponent<HDAdditionalLightData>().intensity + " fixedExposure=" + exposure.fixedExposure.value);
        try
        {
            quilt.RenderQuilt();
            if (quilt.QuiltTexture == null) throw new InvalidOperationException("quilt texture was not produced");
            quilt.RenderQuilt();
            var png = ReadAndSave(quilt.QuiltTexture, "TestResults/hdrp-quilt.png");
            if (png.Length == 0) throw new InvalidOperationException("quilt was empty");
            AssertTiles(quilt.QuiltTexture, quilt.settings.GetLayout());
            AssertTileColorRegions(quilt.QuiltTexture, quilt.settings.GetLayout());
            var litPixels = CapturePixels(quilt.QuiltTexture);
            light.intensity = 0f;
            hdLight.intensity = 0f;
            quilt.RenderQuilt();
            if (!PixelsDiffer(litPixels, CapturePixels(quilt.QuiltTexture))) throw new InvalidOperationException("HDRP directional light on/off produced no pixel change");
            light.intensity = 10f;
            hdLight.intensity = 10f;
            quilt.RenderQuilt();
            var bloomPixels = CapturePixels(quilt.QuiltTexture);
            bloom.intensity.value = 0.1f;
            quilt.RenderQuilt();
            if (!PixelsDiffer(bloomPixels, CapturePixels(quilt.QuiltTexture))) throw new InvalidOperationException("HDRP Bloom on/off produced no pixel change");
            if (camera.enabled || camera.transform.position != new Vector3(0f, 0f, -4f)) throw new InvalidOperationException("camera state was not restored");
            Debug.Log("LookingGlass HDRP pipeline smoke passed");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cube);
            UnityEngine.Object.DestroyImmediate(sphere);
            UnityEngine.Object.DestroyImmediate(floor);
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(volume.profile);
            UnityEngine.Object.DestroyImmediate(volumeObject);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(root);
            GraphicsSettings.defaultRenderPipeline = oldPipeline;
            QualitySettings.renderPipeline = oldQuality;
            UnityEngine.Object.DestroyImmediate(asset);
        }
    }

    public static void RunUrp()
    {
        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
        if (rendererData.postProcessData == null) throw new InvalidOperationException("URP PostProcessData asset is unavailable");
        var pipelineAsset = UniversalRenderPipelineAsset.Create(rendererData);
        pipelineAsset.supportsHDR = true;
        var oldPipeline = GraphicsSettings.defaultRenderPipeline;
        var oldQuality = QualitySettings.renderPipeline;
        GraphicsSettings.defaultRenderPipeline = pipelineAsset;
        QualitySettings.renderPipeline = pipelineAsset;
        var root = new GameObject("LookingGlass.URP.Smoke");
        var lightObject = new GameObject("SmokeDirectional");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 2f;
        light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
        var cameraObject = new GameObject("SmokeCamera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0f, 0f, -4f);
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderType = CameraRenderType.Base;
        cameraData.renderPostProcessing = true;
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.GetComponent<Renderer>().sharedMaterial = CreateMaterial(new Color(2f, 1f, 0.1f, 1f));
        cube.transform.SetParent(root.transform);
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.SetPositionAndRotation(new Vector3(0.8f, 0.2f, 0.6f), Quaternion.identity);
        sphere.GetComponent<Renderer>().sharedMaterial = CreateMaterial(new Color(0.1f, 1.5f, 2f, 1f));
        sphere.transform.SetParent(root.transform);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.transform.position = new Vector3(0f, -0.75f, 0f);
        floor.transform.localScale = Vector3.one * 2f;
        floor.GetComponent<Renderer>().sharedMaterial = CreateMaterial(new Color(0.08f, 0.08f, 0.08f, 1f));
        floor.transform.SetParent(root.transform);
        var volumeObject = new GameObject("SmokeVolume");
        var volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10f;
        volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
        var bloom = volume.profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
        bloom.threshold.value = 0.5f;
        bloom.intensity.value = 2f;
        var guard = cameraObject.AddComponent<LookingGlass.Community.URP.UrpQuiltCamera>();
        var quilt = cameraObject.AddComponent<QuiltRenderer>();
        quilt.sourceCamera = camera;
        quilt.GuardBehaviour = guard;
        quilt.settings.quiltWidth = 256;
        quilt.settings.quiltHeight = 192;
        quilt.settings.columns = 4;
        quilt.settings.rows = 2;
        quilt.settings.views = 8;
        quilt.settings.viewAspect = 4f / 3f;
        quilt.settings.cameraDistance = 4f;
        try
        {
            quilt.RenderQuilt();
            if (quilt.QuiltTexture == null) throw new InvalidOperationException("quilt texture was not produced");
            var png = ReadAndSave(quilt.QuiltTexture, "TestResults/urp-quilt.png");
            if (png.Length == 0) throw new InvalidOperationException("quilt was empty");
            AssertTiles(quilt.QuiltTexture, quilt.settings.GetLayout());
            var bloomPixels = CapturePixels(quilt.QuiltTexture);
            bloom.intensity.value = 0f;
            quilt.RenderQuilt();
            if (!PixelsDiffer(bloomPixels, CapturePixels(quilt.QuiltTexture))) throw new InvalidOperationException("Bloom on/off produced no pixel change");
            if (camera.enabled || camera.transform.position != new Vector3(0f, 0f, -4f)) throw new InvalidOperationException("camera state was not restored");
            Debug.Log("LookingGlass URP pipeline smoke passed");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cube);
            UnityEngine.Object.DestroyImmediate(sphere);
            UnityEngine.Object.DestroyImmediate(floor);
            UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(volume.profile);
            UnityEngine.Object.DestroyImmediate(volumeObject);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(root);
            GraphicsSettings.defaultRenderPipeline = oldPipeline;
            QualitySettings.renderPipeline = oldQuality;
            UnityEngine.Object.DestroyImmediate(rendererData);
            UnityEngine.Object.DestroyImmediate(pipelineAsset);
        }
    }

    static Material CreateMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("URP Lit shader is unavailable");
        var material = new Material(shader);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", 0.35f);
        return material;
    }

    static Material CreateHdrpMaterial(Color color)
    {
        var shader = Shader.Find(sUseHdrpLit ? "HDRP/Lit" : "HDRP/Unlit");
        if (shader == null) throw new InvalidOperationException("HDRP material shader is unavailable");
        var material = new Material(shader);
        material.SetColor("_BaseColor", color);
        material.SetColor("_UnlitColor", color);
        if (sUseHdrpLit)
        {
            material.SetColor("_EmissiveColor", Color.black);
            material.SetFloat("_EmissiveIntensity", 0f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.35f);
        }
        material.SetFloat("_Metallic", 0.05f);
        material.SetFloat("_Smoothness", 0.35f);
        return material;
    }

    static byte[] ReadAndSave(RenderTexture source, string relativePath)
    {
        var old = RenderTexture.active;
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = source;
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply(false, false);
            var data = image.EncodeToPNG();
            var path = Path.Combine(Application.dataPath, "..", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            var pixels = image.GetPixels32();
            for (var i = 0; i < pixels.Length; i++) if (pixels[i].a != 0 && (pixels[i].r != 0 || pixels[i].g != 0 || pixels[i].b != 0)) return data;
            throw new InvalidOperationException("quilt has no visible pixels");
        }
        finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
    }

    static void AssertTiles(RenderTexture source, QuiltLayout layout)
    {
        var old = RenderTexture.active;
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = source;
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply(false, false);
            var pixels = image.GetPixels32();
            var first = layout.GetTile(0);
            for (var i = 0; i < layout.ViewCount; i++)
            {
                var tile = layout.GetTile(i);
                var visible = false;
                for (var y = tile.y; y < tile.yMax && !visible; y++)
                    for (var x = tile.x; x < tile.xMax; x++)
                    {
                        var color = pixels[y * source.width + x];
                        if (color.a != 0 && (color.r != 0 || color.g != 0 || color.b != 0)) { visible = true; break; }
                    }
                if (!visible) throw new InvalidOperationException("quilt tile is empty: " + i);
            }
            var last = layout.GetTile(layout.ViewCount - 1);
            var differences = 0;
            for (var y = 0; y < Mathf.Min(first.height, last.height); y++)
                for (var x = 0; x < Mathf.Min(first.width, last.width); x++)
                {
                    var a = pixels[(first.y + y) * source.width + first.x + x];
                    var b = pixels[(last.y + y) * source.width + last.x + x];
                    if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 6) differences++;
                }
            if (differences < 8)
                throw new InvalidOperationException("first and last quilt views are identical");
        }
        finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
    }

    static void AssertTileColorRegions(RenderTexture source, QuiltLayout layout)
    {
        var old = RenderTexture.active;
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = source;
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply(false, false);
            var pixels = image.GetPixels32();
            for (var i = 0; i < layout.ViewCount; i++)
            {
                var tile = layout.GetTile(i);
                var orange = 0;
                var cyan = 0;
                var yStart = tile.y + tile.height / 2;
                for (var y = yStart; y < tile.yMax; y++)
                    for (var x = tile.x; x < tile.xMax; x++)
                    {
                        var p = pixels[y * source.width + x];
                        if (p.r > 140 && p.r > p.b * 1.05f) orange++;
                        if (p.b > 100 && p.b > p.r * 1.02f) cyan++;
                    }
                Debug.Log("HDRP tile " + i + " orange=" + orange + " cyan=" + cyan);
                if (orange < 32 || cyan < 32) throw new InvalidOperationException("tile " + i + " is missing a rendered geometry color region");
            }
        }
        finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
    }

    static Color32[] CapturePixels(RenderTexture source)
    {
        var old = RenderTexture.active;
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try { RenderTexture.active = source; image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); image.Apply(false, false); return image.GetPixels32(); }
        finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
    }

    static bool PixelsDiffer(Color32[] a, Color32[] b)
    {
        if (a.Length != b.Length) return true;
        for (var i = 0; i < a.Length; i++) if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b) return true;
        return false;
    }
}
#endif
