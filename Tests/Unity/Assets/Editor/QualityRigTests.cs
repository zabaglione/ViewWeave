using System;
using ViewWeave;
using UnityEngine;
using UnityEngine.Rendering;

public static class QualityRigTests
{
    public static void Run()
    {
        CheckQualityProfiles();
        CheckRigOwnership();
        CheckRigRejections();
        Debug.Log("Quality rig batch tests passed");
    }

    static void CheckQualityProfiles()
    {
        QuiltQualityProfile preview = QuiltQualityProfile.Preview(1920, 1080, 8, 6, 1f, 40f);
        Require(preview.QuiltWidth == 480 && preview.QuiltHeight == 270, "preview scale");
        Require(preview.Views == 48 && preview.QuiltWidth % preview.Columns == 0 && preview.QuiltHeight % preview.Rows == 0, "grid divisible");
        CheckNativeNonDivisibleProfiles();
        RequireThrows<ArgumentOutOfRangeException>(() => new QuiltQualityProfile(-1, 100, 2, 2, 4, 1f, 20f), "negative dimensions rejected");
        RequireThrows<ArgumentOutOfRangeException>(() => QuiltQualityProfile.FromScaled(100, 100, 2, 2, 1f, 20f, float.NaN), "scale NaN rejected");
        RequireThrows<ArgumentOutOfRangeException>(() => QuiltQualityProfile.FromScaled(100, 100, 2, 2, 1f, 20f, float.PositiveInfinity), "scale infinity rejected");
        RequireThrows<ArgumentOutOfRangeException>(() => QuiltQualityProfile.FromScaled(int.MaxValue, int.MaxValue, 8, 6, 1f, 20f, 2f), "huge scale rejected");
        RequireThrows<ArgumentOutOfRangeException>(() => new QuiltQualityProfile(2, 8, 3, 2, 6, 1f, 20f), "grid larger than tile rejected");
        int max = SystemInfo.maxTextureSize;
        if (max > 0 && max < int.MaxValue - 1) RequireThrows<ArgumentOutOfRangeException>(() => new QuiltQualityProfile(max + 1, max + 1, 1, 1, 1, 1f, 20f), "device max texture rejected");
    }

    static void CheckNativeNonDivisibleProfiles()
    {
        QuiltQualityProfile fiveByNine = QuiltQualityProfile.Native(4096, 4096, 5, 9, 1f, 40f);
        QuiltQualityProfile fiveByNineFromScaled = QuiltQualityProfile.FromScaled(4096, 4096, 5, 9, 1f, 40f, 1f);
        Require(fiveByNine.QuiltWidth == 4096 && fiveByNine.QuiltHeight == 4096, "native 4096 dimensions preserved");
        Require(fiveByNineFromScaled.QuiltWidth == 4096 && fiveByNineFromScaled.QuiltHeight == 4096, "scale one preserves native dimensions");
        Require(fiveByNine.Columns == 5 && fiveByNine.Rows == 9 && fiveByNine.Views == 45, "native 5x9 grid preserved");
        QuiltLayout fiveLayout = new QuiltLayout(fiveByNine.QuiltWidth, fiveByNine.QuiltHeight, fiveByNine.Rows, fiveByNine.Columns, fiveByNine.Views, fiveByNine.Aspect);
        Require(fiveLayout.TileWidth == 819 && fiveLayout.TileHeight == 455, "native 5x9 floor tile");
        Require(fiveLayout.UsedRect.width == 4095 && fiveLayout.UsedRect.height == 4095, "native 5x9 used rect");
        Require(fiveLayout.UnusedRight == 1 && fiveLayout.UnusedTop == 1, "native 5x9 unused bounds");

        QuiltQualityProfile eightByNine = QuiltQualityProfile.Native(8192, 8192, 8, 9, 1f, 40f);
        QuiltLayout eightLayout = new QuiltLayout(eightByNine.QuiltWidth, eightByNine.QuiltHeight, eightByNine.Rows, eightByNine.Columns, eightByNine.Views, eightByNine.Aspect);
        Require(eightByNine.QuiltWidth == 8192 && eightByNine.QuiltHeight == 8192, "native 8192 dimensions preserved");
        Require(eightLayout.UsedRect.width == 8192 && eightLayout.UsedRect.height == 8190, "native 8x9 used rect");
        Require(eightLayout.UnusedRight == 0 && eightLayout.UnusedTop == 2, "native 8x9 unused bounds");
        RequireThrows<ArgumentOutOfRangeException>(() => QuiltQualityProfile.Native(4, 8, 5, 2, 1f, 40f), "native grid floor rejected");
    }

    static void CheckRigOwnership()
    {
        GameObject go = new GameObject("QualityRigTest");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = true;
            ViewWeaveRig rig = go.AddComponent<ViewWeaveRig>();
            rig.ApplyProfile(QuiltQualityProfile.Balanced(1920, 1080, 8, 6, 1f, 40f));
            QuiltRenderer renderer = go.GetComponent<QuiltRenderer>();
            Require(renderer != null && renderer.sourceCamera == camera, "same gameobject source camera");
            Require(!camera.enabled && renderer.settings.views == 48, "rig owns disabled camera and views");
            Require(Math.Abs(renderer.settings.fieldOfView - 50f) < 0.001f && Math.Abs(renderer.settings.cameraDistance - 1f) < 0.001f, "camera geometry preserved");
            rig.AutoRender = false;
            Require(!renderer.autoRender, "auto render opt in");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static void CheckRigRejections()
    {
        GameObject rigObject = new GameObject("QualityRigRejectionTest");
        GameObject foreignObject = new GameObject("ForeignCamera");
        try
        {
            rigObject.AddComponent<Camera>().enabled = false;
            ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
            MonoBehaviour foreignGuard = foreignObject.AddComponent<ForeignGuard>();
            Require(foreignGuard != null, "foreign guard component created");
            bool rejected = false;
            try { rig.SetGuard(foreignGuard); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "foreign guard rejected");
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                Require(!rig.ValidateRig(out _), "unsupported pipeline rejected");
                bool autoRejected = false;
                try { rig.AutoRender = true; } catch (InvalidOperationException) { autoRejected = true; }
                Require(autoRejected && !rig.AutoRender, "auto render rejects unsupported pipeline");
            }
            bool failed = false;
            try { rig.ApplyProfile(new QuiltQualityProfile(2, 8, 3, 2, 6, 1f, 20f)); } catch (ArgumentOutOfRangeException) { failed = true; }
            Require(failed, "grid larger than tile rejected");
        }
        finally { UnityEngine.Object.DestroyImmediate(rigObject); UnityEngine.Object.DestroyImmediate(foreignObject); }
    }

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
    }

    static void RequireThrows<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { return; } catch (Exception error) { throw new Exception("FAIL: " + name + " wrong exception " + error.GetType().Name); }
        throw new Exception("FAIL: " + name);
    }
}
