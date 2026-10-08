using System;
using System.Collections.Generic;

namespace ViewWeave
{
    public enum QuiltRendererMutationKind
    {
        Framing,
        SamplingMode,
        Quality,
        Structural,
        Transport,
        Recording,
        Playback,
        AutoRender,
        OutputZoom
    }

    public readonly struct QuiltRendererMutationRequest
    {
        public readonly QuiltRendererMutationKind Kind;
        public readonly bool HasFraming;
        public readonly QuiltCameraFraming Framing;
        public readonly bool HasViewRenderMode;
        public readonly QuiltViewRenderMode ViewRenderMode;
        public readonly bool HasBooleanValue;
        public readonly bool BooleanValue;
        public readonly bool HasFloatValue;
        public readonly float FloatValue;

        public QuiltRendererMutationRequest(QuiltRendererMutationKind kind)
        {
            Kind = kind;
            HasFraming = false;
            Framing = default;
            HasViewRenderMode = false;
            ViewRenderMode = default;
            HasBooleanValue = false;
            BooleanValue = false;
            HasFloatValue = false;
            FloatValue = 0f;
        }

        public QuiltRendererMutationRequest(QuiltCameraFraming framing)
        {
            Kind = QuiltRendererMutationKind.Framing;
            HasFraming = true;
            Framing = framing;
            HasViewRenderMode = false;
            ViewRenderMode = default;
            HasBooleanValue = false;
            BooleanValue = false;
            HasFloatValue = false;
            FloatValue = 0f;
        }

        public QuiltRendererMutationRequest(QuiltViewRenderMode viewRenderMode)
        {
            Kind = QuiltRendererMutationKind.SamplingMode;
            HasFraming = false;
            Framing = default;
            HasViewRenderMode = true;
            ViewRenderMode = viewRenderMode;
            HasBooleanValue = false;
            BooleanValue = false;
            HasFloatValue = false;
            FloatValue = 0f;
        }

        public QuiltRendererMutationRequest(QuiltRendererMutationKind kind, bool value)
        {
            Kind = kind;
            HasFraming = false;
            Framing = default;
            HasViewRenderMode = false;
            ViewRenderMode = default;
            HasBooleanValue = true;
            BooleanValue = value;
            HasFloatValue = false;
            FloatValue = 0f;
        }

        public QuiltRendererMutationRequest(QuiltRendererMutationKind kind, float value)
        {
            Kind = kind;
            HasFraming = false;
            Framing = default;
            HasViewRenderMode = false;
            ViewRenderMode = default;
            HasBooleanValue = false;
            BooleanValue = false;
            HasFloatValue = true;
            FloatValue = value;
        }
    }

    /// <summary>Bridge-neutral registration point for active owners that constrain rig edits.</summary>
    internal static class QuiltRendererMutationPolicy
    {
        sealed class OwnerRegistration : IDisposable
        {
            internal QuiltRenderer Renderer;
            internal object Owner;
            internal Func<QuiltRendererMutationRequest, string> GetBlockReason;
            internal bool Disposed;

            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                registrations.Remove(this);
                Renderer = null;
                Owner = null;
                GetBlockReason = null;
            }
        }

        static readonly List<OwnerRegistration> registrations = new List<OwnerRegistration>();

        /// <summary>Registers an owner before it begins using or reconfiguring a renderer.</summary>
        internal static IDisposable RegisterOwner(QuiltRenderer renderer, object owner,
            Func<QuiltRendererMutationRequest, string> getBlockReason, out string error)
        {
            error = string.Empty;
            if (renderer == null || owner == null || getBlockReason == null)
            {
                error = "A renderer, owner, and mutation policy are required.";
                return null;
            }

            for (int i = registrations.Count - 1; i >= 0; --i)
            {
                OwnerRegistration existing = registrations[i];
                if (existing == null || existing.Disposed || existing.Renderer == null)
                {
                    registrations.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(existing.Renderer, renderer) && ReferenceEquals(existing.Owner, owner))
                {
                    error = "This owner is already registered for the selected renderer.";
                    return null;
                }
            }

            var registration = new OwnerRegistration
            {
                Renderer = renderer,
                Owner = owner,
                GetBlockReason = getBlockReason
            };
            registrations.Add(registration);
            return registration;
        }

        internal static bool TryCheck(QuiltRenderer renderer, QuiltRendererMutationKind kind, out string error)
        {
            return TryCheck(renderer, new QuiltRendererMutationRequest(kind), out error);
        }

        internal static bool TryCheck(QuiltRenderer renderer, QuiltRendererMutationRequest request, out string error)
        {
            error = string.Empty;
            QuiltRendererMutationKind kind = request.Kind;
            if (renderer == null)
            {
                error = "A Quilt Renderer is required.";
                return false;
            }
            if (!Enum.IsDefined(typeof(QuiltRendererMutationKind), kind))
            {
                error = "The Quilt Renderer mutation kind is invalid.";
                return false;
            }
            bool disablingAutoRender = kind == QuiltRendererMutationKind.AutoRender && request.HasBooleanValue && !request.BooleanValue;
            if (!disablingAutoRender && (renderer.IsRendering || renderer.IsNotifyingRenderComplete))
            {
                error = renderer.IsRendering
                    ? "The Quilt Renderer is currently rendering."
                    : "Quilt settings cannot change during render completion notification.";
                return false;
            }
            if (!disablingAutoRender && QuiltRendererOperationLease.IsLeased(renderer))
            {
                error = "The Quilt Renderer is locked by an active recording or playback lease.";
                return false;
            }

            bool foundOwner = false;
            for (int i = registrations.Count - 1; i >= 0; --i)
            {
                OwnerRegistration registration = registrations[i];
                if (registration == null || registration.Disposed || registration.Renderer == null)
                {
                    registrations.RemoveAt(i);
                    continue;
                }
                if (!ReferenceEquals(registration.Renderer, renderer)) continue;
                foundOwner = true;
                try
                {
                    string reason = registration.GetBlockReason(request);
                    if (!string.IsNullOrEmpty(reason))
                    {
                        error = reason;
                        return false;
                    }
                }
                catch
                {
                    error = "An active renderer owner could not confirm that this change is safe.";
                    return false;
                }
            }

            if (renderer.HasMutationOwnerRequirement && !foundOwner)
            {
                error = "An active Bridge owner has no renderer mutation-policy registration.";
                return false;
            }
#if UNITY_EDITOR
            if (renderer.HasEditorPreviewRetentionOwner && !foundOwner)
            {
                error = "An editor preview owner has no active mutation-policy registration.";
                return false;
            }
#endif
            return true;
        }

        internal static int GetOwnerCount(QuiltRenderer renderer)
        {
            if (renderer == null) return 0;
            int count = 0;
            for (int i = registrations.Count - 1; i >= 0; --i)
            {
                OwnerRegistration registration = registrations[i];
                if (registration == null || registration.Disposed || registration.Renderer == null)
                {
                    registrations.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(registration.Renderer, renderer)) count++;
            }
            return count;
        }

        internal static bool IsOwnerRegistered(QuiltRenderer renderer, object owner)
        {
            if (renderer == null || owner == null) return false;
            for (int i = registrations.Count - 1; i >= 0; --i)
            {
                OwnerRegistration registration = registrations[i];
                if (registration == null || registration.Disposed || registration.Renderer == null)
                {
                    registrations.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(registration.Renderer, renderer) && ReferenceEquals(registration.Owner, owner)) return true;
            }
            return false;
        }
    }
}
