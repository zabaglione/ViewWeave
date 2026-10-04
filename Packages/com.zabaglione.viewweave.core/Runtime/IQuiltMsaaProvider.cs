using UnityEngine;

namespace LookingGlass.Community
{
    public interface IQuiltMsaaProvider
    {
        int GetMsaaSampleCount(Camera camera);
    }
}
