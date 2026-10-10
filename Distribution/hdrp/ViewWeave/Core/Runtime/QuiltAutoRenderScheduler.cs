using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace ViewWeave
{
    internal static class QuiltAutoRenderScheduler
    {
        static readonly List<QuiltRenderer> registered = new List<QuiltRenderer>();
        static QuiltRenderer[] snapshot = Array.Empty<QuiltRenderer>();
        static bool snapshotDirty = true;
        static int lastRenderedFrame = -1;
        static bool missingFinishFrameDiagnosticLogged;

        internal static void Register(QuiltRenderer renderer)
        {
            if (!Application.isPlaying || ReferenceEquals(renderer, null) || registered.Contains(renderer)) return;
            registered.Add(renderer);
            snapshotDirty = true;
            EnsureNodeInstalled();
        }

        internal static void Unregister(QuiltRenderer renderer)
        {
            if (ReferenceEquals(renderer, null)) return;
            if (!registered.Remove(renderer)) return;
            snapshotDirty = true;
            if (registered.Count == 0)
                snapshot = Array.Empty<QuiltRenderer>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForSubsystem()
        {
            RemoveNode();
            registered.Clear();
            snapshot = Array.Empty<QuiltRenderer>();
            snapshotDirty = true;
            lastRenderedFrame = -1;
            missingFinishFrameDiagnosticLogged = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void RegisterExistingRenderers()
        {
            if (!Application.isPlaying) return;
            QuiltRenderer[] renderers = UnityEngine.Object.FindObjectsByType<QuiltRenderer>();
            for (int i = 0; i < renderers.Length; ++i)
                if (renderers[i].isActiveAndEnabled) Register(renderers[i]);
        }

        static void EnsureNodeInstalled()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (ContainsNode(loop)) return;
            PlayerLoopSystem copy = Clone(loop);
            if (!InsertNodeAfterFinishFrameRendering(ref copy))
            {
                if (!missingFinishFrameDiagnosticLogged)
                {
                    missingFinishFrameDiagnosticLogged = true;
                    Debug.LogError("Looking Glass quilt auto render scheduler could not find PostLateUpdate.FinishFrameRendering.");
                }
                return;
            }
            missingFinishFrameDiagnosticLogged = false;
            PlayerLoop.SetPlayerLoop(copy);
        }

        static void RemoveNode()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!ContainsNode(loop)) return;
            PlayerLoopSystem copy = Clone(loop);
            if (RemoveNodeFrom(ref copy)) PlayerLoop.SetPlayerLoop(copy);
        }

        static void RenderRegistered()
        {
            if (!Application.isPlaying) return;
            if (registered.Count == 0) return;
            if (lastRenderedFrame == Time.frameCount) return;
            lastRenderedFrame = Time.frameCount;
            if (snapshotDirty)
            {
                snapshot = registered.ToArray();
                snapshotDirty = false;
            }
            QuiltRenderer[] current = snapshot;
            for (int i = 0; i < current.Length; ++i)
            {
                QuiltRenderer renderer = current[i];
                if (renderer != null) renderer.RenderAutoQuiltAtBoundary();
            }
        }

        static PlayerLoopSystem CreateNode()
        {
            return new PlayerLoopSystem
            {
                type = typeof(QuiltAutoRenderScheduler),
                updateDelegate = RenderRegistered
            };
        }

        static bool ContainsNode(PlayerLoopSystem system)
        {
            if (system.type == typeof(QuiltAutoRenderScheduler)) return true;
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
                if (ContainsNode(children[i])) return true;
            return false;
        }

        static bool InsertNodeAfterFinishFrameRendering(ref PlayerLoopSystem system)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
            {
                if (children[i].type == typeof(PostLateUpdate.FinishFrameRendering))
                {
                    PlayerLoopSystem[] expanded = new PlayerLoopSystem[children.Length + 1];
                    Array.Copy(children, 0, expanded, 0, i + 1);
                    expanded[i + 1] = CreateNode();
                    Array.Copy(children, i + 1, expanded, i + 2, children.Length - i - 1);
                    system.subSystemList = expanded;
                    return true;
                }
                if (InsertNodeAfterFinishFrameRendering(ref children[i])) return true;
            }
            return false;
        }

        static bool RemoveNodeFrom(ref PlayerLoopSystem system)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            bool removed = false;
            int count = 0;
            for (int i = 0; i < children.Length; ++i)
                if (children[i].type == typeof(QuiltAutoRenderScheduler)) removed = true;
                else ++count;
            if (removed)
            {
                PlayerLoopSystem[] compact = new PlayerLoopSystem[count];
                int index = 0;
                for (int i = 0; i < children.Length; ++i)
                    if (children[i].type != typeof(QuiltAutoRenderScheduler)) compact[index++] = children[i];
                system.subSystemList = compact;
                return true;
            }
            for (int i = 0; i < children.Length; ++i)
                if (RemoveNodeFrom(ref children[i])) return true;
            return false;
        }

        static PlayerLoopSystem Clone(PlayerLoopSystem system)
        {
            PlayerLoopSystem copy = system;
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return copy;
            PlayerLoopSystem[] cloned = new PlayerLoopSystem[children.Length];
            for (int i = 0; i < children.Length; ++i) cloned[i] = Clone(children[i]);
            copy.subSystemList = cloned;
            return copy;
        }
    }
}
