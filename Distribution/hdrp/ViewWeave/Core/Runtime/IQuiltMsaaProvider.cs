using UnityEngine;

namespace ViewWeave
{
    public interface IQuiltMsaaProvider
    {
        int GetMsaaSampleCount(Camera camera);
    }

    /// <summary>Optional settings-aware sample selection before Core allocates its view target.</summary>
    public interface IQuiltMsaaSettingsProvider : IQuiltMsaaProvider
    {
        int GetMsaaSampleCount(Camera sourceCamera, Camera referenceCamera);
    }
}
