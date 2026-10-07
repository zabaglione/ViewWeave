using UnityEngine;

namespace ViewWeave
{
    /// <summary>World-space camera and zero-parallax planes used by Scene view authoring tools.</summary>
    public static class QuiltFrustumGeometry
    {
        public struct Geometry
        {
            public Vector3[] nearPlane;
            public Vector3[] farPlane;
            public Vector3[] focusPlane;
            public Vector3[] leftNearPlane;
            public Vector3[] rightNearPlane;
            public Vector3[] leftFarPlane;
            public Vector3[] rightFarPlane;
        }

        public static bool TryBuild(Transform cameraTransform, QuiltSettings settings, float nearClip, float farClip, out Geometry geometry)
        {
            geometry = default;
            if (cameraTransform == null || settings == null || !IsFinite(nearClip) || !IsFinite(farClip) || nearClip <= 0f || farClip <= nearClip)
                return false;

            float fov = settings.fieldOfView;
            float aspect = settings.viewAspect;
            float focusDistance = settings.cameraDistance;
            float cone = settings.viewCone;
            if (!IsFinite(fov) || fov <= 0f || fov >= 179f || !IsFinite(aspect) || aspect <= 0f ||
                !IsFinite(focusDistance) || focusDistance <= 0f || !IsFinite(cone) || cone < 0f || cone >= 179f ||
                settings.views < 1 || settings.rows < 1 || settings.columns < 1 || (long)settings.views > (long)settings.rows * settings.columns)
                return false;

            Vector3 origin = cameraTransform.position;
            Quaternion rotation = cameraTransform.rotation;
            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;
            Vector3 forward = rotation * Vector3.forward;
            float tangent = Mathf.Tan(fov * Mathf.Deg2Rad * 0.5f);
            if (!IsFinite(tangent) || tangent <= 0f)
                return false;

            geometry.nearPlane = CenterPlane(origin, right, up, forward, nearClip, tangent, aspect);
            geometry.farPlane = CenterPlane(origin, right, up, forward, farClip, tangent, aspect);
            geometry.focusPlane = CenterPlane(origin, right, up, forward, focusDistance, tangent, aspect);

            float extremeOffset = settings.views <= 1
                ? 0f
                : QuiltMath.ViewOffset(settings.views - 1, settings.views, cone, focusDistance);
            geometry.leftNearPlane = ViewPlane(origin, right, up, forward, nearClip, tangent, aspect, focusDistance, -extremeOffset);
            geometry.rightNearPlane = ViewPlane(origin, right, up, forward, nearClip, tangent, aspect, focusDistance, extremeOffset);
            geometry.leftFarPlane = ViewPlane(origin, right, up, forward, farClip, tangent, aspect, focusDistance, -extremeOffset);
            geometry.rightFarPlane = ViewPlane(origin, right, up, forward, farClip, tangent, aspect, focusDistance, extremeOffset);
            return AreFinite(geometry.nearPlane) && AreFinite(geometry.farPlane) && AreFinite(geometry.focusPlane) &&
                   AreFinite(geometry.leftNearPlane) && AreFinite(geometry.rightNearPlane) &&
                   AreFinite(geometry.leftFarPlane) && AreFinite(geometry.rightFarPlane);
        }

        static Vector3[] CenterPlane(Vector3 origin, Vector3 right, Vector3 up, Vector3 forward, float depth, float tangent, float aspect)
        {
            float halfHeight = depth * tangent;
            float halfWidth = halfHeight * aspect;
            Vector3 center = origin + forward * depth;
            return new[]
            {
                center - right * halfWidth - up * halfHeight,
                center - right * halfWidth + up * halfHeight,
                center + right * halfWidth + up * halfHeight,
                center + right * halfWidth - up * halfHeight
            };
        }

        static Vector3[] ViewPlane(Vector3 origin, Vector3 right, Vector3 up, Vector3 forward, float depth, float tangent, float aspect, float focusDistance, float eyeOffset)
        {
            float halfHeight = depth * tangent;
            float halfWidth = halfHeight * aspect;
            Vector3 eye = origin + right * eyeOffset;
            float projectionShiftAtDepth = -eyeOffset * depth / focusDistance;
            Vector3 center = eye + forward * depth + right * projectionShiftAtDepth;
            return new[]
            {
                center - right * halfWidth - up * halfHeight,
                center - right * halfWidth + up * halfHeight,
                center + right * halfWidth + up * halfHeight,
                center + right * halfWidth - up * halfHeight
            };
        }

        static bool AreFinite(Vector3[] points)
        {
            for (int i = 0; i < points.Length; ++i)
                if (!IsFinite(points[i].x) || !IsFinite(points[i].y) || !IsFinite(points[i].z)) return false;
            return true;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
