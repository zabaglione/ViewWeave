using System;
using UnityEngine;

namespace ViewWeave
{
    public interface IQuiltRenderPreparation
    {
        IDisposable PrepareQuilt(Camera camera);
    }
}
