using System;
using LookingGlass.Community;
using UnityEngine;

public sealed class ForeignGuard : MonoBehaviour, IQuiltRenderGuard
{
    public IDisposable Begin(Camera camera) { return null; }
}
