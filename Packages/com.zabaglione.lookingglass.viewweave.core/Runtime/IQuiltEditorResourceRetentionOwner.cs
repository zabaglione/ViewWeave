#if UNITY_EDITOR
using UnityEngine;

namespace ViewWeave
{
    public interface IQuiltEditorResourceRetentionOwner
    {
        bool TrackTexture(RenderTexture texture);
        void RequestStopAndTakeOwnership(QuiltRenderer renderer);
    }
}
#endif
