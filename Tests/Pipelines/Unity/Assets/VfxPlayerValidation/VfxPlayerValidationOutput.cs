using LookingGlass.Community;
using UnityEngine;

namespace LookingGlass.VfxPlayerValidation
{
    public sealed class VfxPlayerValidationOutput : MonoBehaviour, IQuiltOutput
    {
        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public int LastRenderedFrame { get; private set; } = -1;
        public int SubmissionCount { get; private set; }
        public Texture2D CapturedImage { get; private set; }
        public int CapturedFrame { get; private set; } = -1;

        public void RequestCapture()
        {
            RequestCapturePending = true;
        }

        public bool RequestCapturePending { get; private set; }

        public Texture2D TakeCapture(out int frame)
        {
            Texture2D image = CapturedImage;
            frame = CapturedFrame;
            CapturedImage = null;
            CapturedFrame = -1;
            return image;
        }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            QuiltTexture = quiltTexture;
            Layout = layout;
            LastRenderedFrame = Time.frameCount;
            SubmissionCount++;
            if (!RequestCapturePending) return;
            RequestCapturePending = false;
            RenderTexture previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                RenderTexture.active = quiltTexture;
                image = new Texture2D(quiltTexture.width, quiltTexture.height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, quiltTexture.width, quiltTexture.height), 0, 0);
                image.Apply(false, false);
                CapturedImage = image;
                CapturedFrame = LastRenderedFrame;
            }
            catch
            {
                if (image != null) Destroy(image);
                throw;
            }
            finally { RenderTexture.active = previous; }
        }
    }
}
