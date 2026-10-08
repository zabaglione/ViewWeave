using UnityEngine;

namespace ViewWeave
{
    public static class QuiltMath
    {
        public static float ViewOffset(int index, int count, float coneDegrees, float distance)
        {
            if (count <= 1) return 0f;
            float t = index / (float)(count - 1) * 2f - 1f;
            return Mathf.Tan(coneDegrees * Mathf.Deg2Rad * 0.5f) * distance * t;
        }

        public static Matrix4x4 OffAxisProjection(float fovDegrees, float aspect, float nearClip, float farClip, float eyeOffset, float distance)
        {
            float top = nearClip * Mathf.Tan(fovDegrees * Mathf.Deg2Rad * 0.5f);
            float right = top * aspect;
            float shift = -eyeOffset / distance * nearClip;
            return Matrix4x4.Frustum(-right + shift, right + shift, -top, top, nearClip, farClip);
        }

        /// <summary>
        /// Applies a horizontal eye offset to a centered perspective projection.
        /// centerProjection must be the projection for eyeOffset == 0, with the same
        /// field of view, aspect ratio, and clip planes used for the offset view.
        /// </summary>
        public static Matrix4x4 ProjectionWithViewOffset(Matrix4x4 centerProjection, float eyeOffset, float distance)
        {
            Matrix4x4 projection = centerProjection;
            projection.m02 = centerProjection.m02 + (-eyeOffset / distance) * centerProjection.m00;
            return projection;
        }

        public static Quaternion ParallelViewRotation(Quaternion baseRotation) => baseRotation;
    }
}
