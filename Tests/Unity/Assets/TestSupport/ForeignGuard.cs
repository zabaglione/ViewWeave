using System;
using ViewWeave;
using UnityEngine;

public sealed class ForeignGuard : MonoBehaviour, IQuiltRenderGuard
{
    public IDisposable Begin(Camera camera) { return null; }
}
