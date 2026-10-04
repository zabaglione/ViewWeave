using LookingGlass.Community;
using UnityEngine;

namespace LookingGlass.PlayerValidation
{
    public sealed class PlayerValidationOutput : MonoBehaviour, IQuiltOutput
    {
        public RenderTexture LastTexture { get; private set; }
        public QuiltLayout LastLayout { get; private set; }
        public RenderTexture QuiltTexture => LastTexture;
        public QuiltLayout Layout => LastLayout;
        public int LastRenderedFrame { get; private set; } = -1;
        public int SubmissionCount { get; private set; }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            LastTexture = quiltTexture;
            LastLayout = layout;
            LastRenderedFrame = Time.frameCount;
            SubmissionCount++;
        }
    }
}
