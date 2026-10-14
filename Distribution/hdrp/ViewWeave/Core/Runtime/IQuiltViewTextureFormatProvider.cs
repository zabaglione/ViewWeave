using UnityEngine;

namespace ViewWeave
{
    /// <summary>Optional pipeline choice for the per-view render target format.</summary>
    public interface IQuiltViewTextureFormatProvider
    {
        RenderTextureFormat GetQuiltViewTextureFormat(Camera sourceCamera);
    }
}
