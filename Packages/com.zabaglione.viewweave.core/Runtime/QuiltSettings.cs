using System;
using UnityEngine;

namespace LookingGlass.Community
{
    [Serializable]
    public sealed class QuiltSettings
    {
        [Min(1)] public int quiltWidth = 4096;
        [Min(1)] public int quiltHeight = 4096;
        [Min(1)] public int columns = 8;
        [Min(1)] public int rows = 6;
        [Min(1)] public int views = 48;
        [Min(0.001f)] public float viewAspect = 1f;
        [Min(0.001f)] public float fieldOfView = 50f;
        [Min(0.001f)] public float cameraDistance = 1f;
        [Min(0f)] public float viewCone = 40f;

        public QuiltSettings Clone() => (QuiltSettings)MemberwiseClone();

        public void Validate()
        {
            if (quiltWidth < 1 || quiltHeight < 1) throw new ArgumentOutOfRangeException("quilt dimensions must be positive");
            if (columns < 1 || rows < 1) throw new ArgumentOutOfRangeException("rows and columns must be positive");
            if (views < 1 || views > rows * columns) throw new ArgumentOutOfRangeException("views must fit the quilt grid");
            if (float.IsNaN(viewAspect) || float.IsInfinity(viewAspect) || float.IsNaN(fieldOfView) || float.IsInfinity(fieldOfView) || viewAspect <= 0f || fieldOfView <= 0f || fieldOfView >= 179f) throw new ArgumentOutOfRangeException("invalid aspect or field of view");
            if (float.IsNaN(cameraDistance) || float.IsInfinity(cameraDistance) || float.IsNaN(viewCone) || float.IsInfinity(viewCone) || cameraDistance <= 0f || viewCone < 0f || viewCone >= 179f) throw new ArgumentOutOfRangeException("invalid camera geometry");
        }

        public QuiltLayout GetLayout()
        {
            Validate();
            return new QuiltLayout(quiltWidth, quiltHeight, rows, columns, views, viewAspect);
        }
    }
}
