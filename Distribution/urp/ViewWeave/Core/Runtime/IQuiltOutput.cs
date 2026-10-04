using UnityEngine;

namespace LookingGlass.Community
{
    public interface IQuiltOutput
    {
        RenderTexture QuiltTexture { get; }
        QuiltLayout Layout { get; }
        void Submit(RenderTexture quiltTexture, QuiltLayout layout);
    }

    public sealed class QuiltOutputTarget : MonoBehaviour, IQuiltOutput
    {
        [SerializeField] RenderTexture quiltTexture;
        public RenderTexture QuiltTexture => quiltTexture;
        public QuiltLayout Layout { get; private set; }
        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            this.quiltTexture = quiltTexture;
            Layout = layout;
        }
    }
}
