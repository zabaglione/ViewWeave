using System;
using UnityEngine;

namespace LookingGlass.Community
{
    public interface IQuiltRenderGuard
    {
        IDisposable Begin(Camera camera);
    }
}
