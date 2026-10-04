using System;
using UnityEngine;

namespace ViewWeave
{
    public interface IQuiltRenderGuard
    {
        IDisposable Begin(Camera camera);
    }
}
