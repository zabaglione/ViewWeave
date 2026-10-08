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
        float[] fovs = { 20f, 50f, 95f, 135f };
        float[] aspects = { 0.5f, 1f, 1.7777778f, 2.4f };
        float[] nears = { 0.001f, 0.01f, 0.2f, 1f };
        float[] fars = { 2f, 100f, 1000f, 5000f };
        float[] distances = { 0.05f, 0.75f, 2f, 25f };
        float[] cones = { 0f, 1f, 40f, 89f };
        int[] counts = { 1, 2, 3, 7, 48 };
        for (int sample = 0; sample < fovs.Length; ++sample)
        {
            float fov = fovs[sample];
            float aspect = aspects[sample];
            float nearClip = nears[sample];
            float farClip = fars[sample];
            float distance = distances[sample];
            float cone = cones[sample];
            Matrix4x4 center = QuiltMath.OffAxisProjection(fov, aspect, nearClip, farClip, 0f, distance);
            foreach (int count in counts)
            {
                float maximumOffset = QuiltMath.ViewOffset(count - 1, count, cone, distance);
                for (int index = 0; index < count; ++index)
                {
                    float normalized = count <= 1 ? 0f : index / (float)(count - 1) * 2f - 1f;
                    float offset = maximumOffset * normalized;
                    Matrix4x4 optimized = QuiltMath.ProjectionWithViewOffset(center, offset, distance);
                    Matrix4x4 reference = LegacyFrustumProjection(fov, aspect, nearClip, farClip, offset, distance);
                    RequireMatricesClose(optimized, reference, 0.00002f, "center-plus-offset equals frustum " + count + ":" + index);
                }
                Require(Math.Abs(QuiltMath.ViewOffset(0, count, cone, distance) + maximumOffset) < 0.00002f, "symmetric view offsets " + count);
            }
        }
        CheckFocalPlaneInvariant(0.01f, 100f, 50f, 1f, 1f, Vector3.zero, Quaternion.identity);
        CheckFocalPlaneInvariant(0.03f, 250f, 73f, 1.6f, 2.3f, new Vector3(4.2f, -1.7f, 8.1f), Quaternion.Euler(17f, 62f, -11f));
        CheckFocalPlaneInvariant(0.2f, 900f, 112f, 0.72f, 0.65f, new Vector3(-15f, 2.4f, 0.3f), Quaternion.Euler(-31f, 178f, 24f));
    }

    static Matrix4x4 LegacyFrustumProjection(float fov, float aspect, float nearClip, float farClip, float offset, float distance)
    {
        float top = nearClip * Mathf.Tan(fov * Mathf.Deg2Rad * 0.5f);
        float right = top * aspect;
        float shift = -offset / distance * nearClip;
        return Matrix4x4.Frustum(-right + shift, right + shift, -top, top, nearClip, farClip);
    }

    static void RequireMatricesClose(Matrix4x4 actual, Matrix4x4 expected, float tolerance, string name)
    {
        for (int row = 0; row < 4; ++row)
            for (int column = 0; column < 4; ++column)
                Require(Math.Abs(actual[row, column] - expected[row, column]) <= tolerance, name + " matrix[" + row + "," + column + "]");
    }

    static void CheckFocalPlaneInvariant(float nearClip, float farClip, float fov, float aspect, float distance, Vector3 origin, Quaternion rotation)
    {
        GameObject go = new GameObject("focal-plane-test");
        Camera camera = go.AddComponent<Camera>();
        camera.nearClipPlane = nearClip;
        camera.farClipPlane = farClip;
        camera.transform.SetPositionAndRotation(origin, rotation);
        Vector3[] localPoints = { new Vector3(0f, 0f, distance), new Vector3(0.1f * distance, 0.07f * distance, distance), new Vector3(-0.13f * distance, -0.09f * distance, distance) };
        Vector3[] focalPoints = new Vector3[localPoints.Length];
        for (int point = 0; point < localPoints.Length; ++point) focalPoints[point] = camera.transform.TransformPoint(localPoints[point]);
        Matrix4x4 center = QuiltMath.OffAxisProjection(fov, aspect, nearClip, farClip, 0f, distance);
        camera.projectionMatrix = center;
        Vector3[] baselineViewport = new Vector3[focalPoints.Length];
        for (int point = 0; point < focalPoints.Length; ++point) baselineViewport[point] = camera.WorldToViewportPoint(focalPoints[point]);
        float[] offsets = { -0.3f * distance, 0f, 0.3f * distance };
        foreach (float offset in offsets)
        {
            camera.transform.position = origin + rotation * Vector3.right * offset;
            camera.projectionMatrix = QuiltMath.ProjectionWithViewOffset(center, offset, distance);
            for (int point = 0; point < focalPoints.Length; ++point)
            {
                Vector3 viewport = camera.WorldToViewportPoint(focalPoints[point]);
                Require(Math.Abs(viewport.x - baselineViewport[point].x) < 0.001f && Math.Abs(viewport.y - baselineViewport[point].y) < 0.001f, "translated and rotated focal plane invariant");
            }
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
