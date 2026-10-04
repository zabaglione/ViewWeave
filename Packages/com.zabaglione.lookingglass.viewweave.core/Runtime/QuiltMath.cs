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

        public static Quaternion ParallelViewRotation(Quaternion baseRotation) => baseRotation;
    }
}
