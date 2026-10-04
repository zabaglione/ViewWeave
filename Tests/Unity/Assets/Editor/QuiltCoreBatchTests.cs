using System;
using ViewWeave;
using UnityEditor;
using UnityEngine;

public static class QuiltCoreBatchTests
{
    public static void Run()
    {
        CheckLayout();
        CheckProjection();
        CheckSettingsValidation();
        CheckViewSampling();
        Debug.Log("Quilt core batch tests passed");
    }

    static void CheckLayout()
    {
        QuiltLayout l = new QuiltLayout(4096, 4096, 6, 8, 48, 0.75f);
        Require(l.TileWidth == 512 && l.TileHeight == 682, "floor tile size");
        Require(l.UnusedRight == 0 && l.UnusedTop == 4, "unused border");
        Require(Math.Abs(l.AspectRatio - 0.75f) < 0.00001f, "view aspect is preserved");
        Require(l.PixelAspectRatio > 0.7f && l.PixelAspectRatio < 0.8f, "pixel aspect is separate");
        Require(l.GetTile(0).y == 0 && l.GetTile(47).y == 3410, "row order");
    }

    static void CheckProjection()
    {
        float a = QuiltMath.ViewOffset(0, 5, 40f, 1f);
        float b = QuiltMath.ViewOffset(4, 5, 40f, 1f);
        Require(Math.Abs(a + b) < 0.00001f, "symmetric view offsets");
        Matrix4x4 p = QuiltMath.OffAxisProjection(50f, 1f, 0.01f, 100f, a, 1f);
        Require(p.m02 > 0f, "off-axis projection sign");
        GameObject go = new GameObject("focal-plane-test");
        Camera c = go.AddComponent<Camera>();
        c.nearClipPlane = 0.01f; c.farClipPlane = 100f; c.fieldOfView = 50f; c.aspect = 1f;
        Vector3[] focalPoints = { new Vector3(0f, 0f, 1f), new Vector3(0f, 0.1f, 1f), new Vector3(0f, -0.1f, 1f) };
        for (int i = 0; i < 2; ++i)
        {
            float offset = i == 0 ? a : b;
            c.transform.position = new Vector3(offset, 0f, 0f);
            c.projectionMatrix = QuiltMath.OffAxisProjection(50f, 1f, c.nearClipPlane, c.farClipPlane, offset, 1f);
            foreach (Vector3 point in focalPoints)
                Require(Math.Abs(c.WorldToViewportPoint(point).x - 0.5f) < 0.001f, "focal plane invariant");
        }
        UnityEngine.Object.DestroyImmediate(go);
    }

    static void CheckSettingsValidation()
    {
        QuiltSettings s = new QuiltSettings { rows = 2, columns = 2, views = 3 };
        s.Validate();
        bool failed = false;
        try { s.views = 5; s.Validate(); } catch (ArgumentOutOfRangeException) { failed = true; }
        Require(failed, "view count validation");
        bool nanFailed = false;
        try { s.views = 3; s.viewAspect = float.NaN; s.Validate(); } catch (ArgumentOutOfRangeException) { nanFailed = true; }
        Require(nanFailed, "nonfinite setting validation");
    }

    static void CheckViewSampling()
    {
        Require(QuiltViewSampling.GetRenderCount(48, QuiltViewRenderMode.Full) == 48, "full render count");
        Require(QuiltViewSampling.GetRenderCount(47, QuiltViewRenderMode.Half) == 24, "odd half render count rounds up");
        Require(QuiltViewSampling.GetRenderCount(48, QuiltViewRenderMode.Quarter) == 12, "quarter render count");
        Require(QuiltViewSampling.GetRenderCount(1, QuiltViewRenderMode.Quarter) == 1, "single view render count");
        Require(QuiltViewSampling.GetRenderCount(2, QuiltViewRenderMode.Quarter) == 2, "two views retain endpoints");
        Require(QuiltViewSampling.GetRenderCount(3, QuiltViewRenderMode.Quarter) == 2, "three views retain endpoints");

        int[] evenIndices = { 0, 2, 5, 7 };
        for (int slot = 0; slot < evenIndices.Length; ++slot)
            Require(QuiltViewSampling.GetRenderedViewIndex(8, QuiltViewRenderMode.Half, slot) == evenIndices[slot], "even uniform selection " + slot);
        int[] oddIndices = { 0, 2, 4, 6 };
        for (int slot = 0; slot < oddIndices.Length; ++slot)
            Require(QuiltViewSampling.GetRenderedViewIndex(7, QuiltViewRenderMode.Half, slot) == oddIndices[slot], "odd uniform selection " + slot);
        Require(QuiltViewSampling.GetRenderedViewIndex(8, QuiltViewRenderMode.Half, 0) == 0, "first endpoint selected");
        Require(QuiltViewSampling.GetRenderedViewIndex(8, QuiltViewRenderMode.Half, 3) == 7, "last endpoint selected");
        Require(QuiltViewSampling.GetSourceViewIndex(6, QuiltViewRenderMode.Half, 4) == 3, "nearest tie chooses lower index");
        Require(QuiltViewSampling.GetSourceViewIndex(8, QuiltViewRenderMode.Half, 4) == 5, "nearest view selected");

        foreach (QuiltViewRenderMode mode in new[] { QuiltViewRenderMode.Full, QuiltViewRenderMode.Half, QuiltViewRenderMode.Quarter })
        {
            int viewCount = 11;
            int renderCount = QuiltViewSampling.GetRenderCount(viewCount, mode);
            int[] assignments = new int[viewCount];
            for (int slot = 0; slot < renderCount; ++slot)
            {
                int renderedView = QuiltViewSampling.GetRenderedViewIndex(viewCount, mode, slot);
                for (int view = 0; view < viewCount; ++view)
                    if (QuiltViewSampling.GetSourceViewIndex(viewCount, mode, view) == renderedView) assignments[view]++;
            }
            for (int view = 0; view < viewCount; ++view)
                Require(assignments[view] == 1, "every output tile assigned once " + mode + " " + view);
        }

        RequireThrows(() => QuiltViewSampling.GetRenderCount(0, QuiltViewRenderMode.Full), "invalid view count rejected");
        RequireThrows(() => QuiltViewSampling.GetRenderCount(4, (QuiltViewRenderMode)3), "invalid render mode rejected");
        RequireThrows(() => QuiltViewSampling.GetRenderedViewIndex(4, QuiltViewRenderMode.Half, 2), "invalid render slot rejected");
        RequireThrows(() => QuiltViewSampling.GetSourceViewIndex(4, QuiltViewRenderMode.Half, 4), "invalid output view rejected");
    }

    static void RequireThrows(Action action, string name)
    {
        bool failed = false;
        try { action(); } catch (ArgumentOutOfRangeException) { failed = true; }
        Require(failed, name);
    }

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
    }
}
