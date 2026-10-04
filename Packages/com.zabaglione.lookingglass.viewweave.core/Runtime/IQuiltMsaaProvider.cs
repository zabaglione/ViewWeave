using UnityEngine;

namespace ViewWeave
{
    public interface IQuiltMsaaProvider
    {
        int GetMsaaSampleCount(Camera camera);
    }
}
