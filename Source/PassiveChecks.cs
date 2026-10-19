using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FairyGUI;
using HarmonyLib;
using InfantGod.Core.SkillCheckSystem;
using InfantGod.UI.WindowSystem;
using InfantGod.UI.WindowSystem.Windows;
using LiveStreamWindow;
using UnityEngine;

namespace InfantGodQuickSaveFix
{
    public static partial class Entry
    {
        private static readonly ConditionalWeakTable<UI_Panel_PassiveCheck, PassivePanel> passivePanels = new ConditionalWeakTable<UI_Panel_PassiveCheck, PassivePanel>();
        private static readonly List<WeakReference> passivePanelRefs = new List<WeakReference>();
        private static readonly HashSet<PassiveRoutine> runningPassiveChecks = new HashSet<PassiveRoutine>();
        private static int passiveGeneration;
        private static bool passiveShowsSuppressed;

        private static void InstallPassiveHooks(Harmony installed)
        {
            Patch(installed, typeof(UI_Panel_PassiveCheck), "ConstructFromXML", null, "PassivePanelConstructed", null);
            Patch(installed, typeof(NPCPanelManager), "RestorePanelDefaultState", null, "SpeakerPanelReset", null);
            Patch(installed, typeof(LiveStreamSpeakerManager), "ShowPassiveCheckResult", "BeforePassiveShow", "AfterPassiveShow", null);
            Patch(installed, typeof(SkillCheckManager), "PerformPassiveSkillCheckRoutine", null, "PassiveRoutineCreated", null);
        }

        private static void PassivePanelConstructed(UI_Panel_PassiveCheck __instance) { RegisterPassivePanel(__instance); }
        private static PassivePanel RegisterPassivePanel(UI_Panel_PassiveCheck panel)
        {
            if (panel == null || panel.isDisposed) return null;
            PassivePanel state;
            if (!passivePanels.TryGetValue(panel, out state))
            {
                state = new PassivePanel(panel);
                passivePanels.Add(panel, state);
                passivePanelRefs.Add(new WeakReference(state));
            }
            return state;
        }
        private static UI_Panel_PassiveCheck PassiveFor(LiveStreamSpeakerManager manager, string character)
        {
            GComponent panel = manager == null ? null : manager.GetSpeakerPanel(character);
            if (panel == null || panel.isDisposed) return null;
            UI_Panel_PassiveCheck passive = panel.GetChild("Panel_PassiveCheck") as UI_Panel_PassiveCheck;
            if (passive != null) return passive;
            for (int i = 0; i < panel.numChildren; i++)
                if (panel.GetChildAt(i) is UI_Panel_PassiveCheck) return (UI_Panel_PassiveCheck)panel.GetChildAt(i);
            return null;
        }
        private static void SpeakerPanelReset(GComponent panel)
        {
            if (panel == null || panel.isDisposed) return;
            for (int i = 0; i < panel.numChildren; i++)
            {
                UI_Panel_PassiveCheck passive = panel.GetChildAt(i) as UI_Panel_PassiveCheck;
                if (passive != null) RegisterPassivePanel(passive).Hide();
            }
        }
        private static bool BeforePassiveShow(LiveStreamSpeakerManager __instance, string characterName)
        {
            if (passiveShowsSuppressed) return false;
            PassivePanel state = RegisterPassivePanel(PassiveFor(__instance, characterName));
            if (state != null) state.PrepareToShow();
            return true;
        }
        private static void AfterPassiveShow(LiveStreamSpeakerManager __instance, string characterName, bool success)
        {
            if (passiveShowsSuppressed) return;
            PassivePanel state = RegisterPassivePanel(PassiveFor(__instance, characterName));
            if (state != null) state.Started(success);
        }
        private static void CleanPassivePanels()
        {
            foreach (NPCPanelManager manager in UnityEngine.Object.FindObjectsOfType<NPCPanelManager>())
                foreach (string character in manager.GetAllActiveNPCs()) SpeakerPanelReset(manager.GetPanelForCharacter(character));
            for (int i = passivePanelRefs.Count - 1; i >= 0; i--)
            {
                PassivePanel state = passivePanelRefs[i].Target as PassivePanel;
                if (state == null || state.Panel.isDisposed) { passivePanelRefs.RemoveAt(i); continue; }
                state.Hide();
            }
        }
        private static void UpdatePassivePanels()
        {
            for (int i = passivePanelRefs.Count - 1; i >= 0; i--)
            {
                PassivePanel state = passivePanelRefs[i].Target as PassivePanel;
                if (state == null || state.Panel.isDisposed) { passivePanelRefs.RemoveAt(i); continue; }
                state.Update();
            }
        }
        private static void BeginPassiveRestore()
        {
            passiveShowsSuppressed = true;
            passiveGeneration++;
            // Finish old checks before restoring combat data: a delayed finally must not
            // unlock a skill check belonging to the new timeline.
            foreach (PassiveRoutine routine in new List<PassiveRoutine>(runningPassiveChecks))
                try { routine.Dispose(); } catch (Exception ex) { Log("取消旧检定时遇到错误：" + ex.Message); }
            CleanPassivePanels();
        }
        private static void ClearPassiveTracking()
        {
            passiveShowsSuppressed = false;
            foreach (WeakReference reference in passivePanelRefs)
            {
                PassivePanel state = reference.Target as PassivePanel;
                if (state != null && !state.Panel.isDisposed) state.Release();
            }
        }
        [HarmonyAfter("dsh.infantgod.diceverdict.PerformPassiveSkillCheckRoutine")]
        private static void PassiveRoutineCreated(ref IEnumerator __result)
        { __result = new PassiveRoutine(__result, passiveGeneration, true); }

        private sealed class PassiveRoutine : IEnumerator, IDisposable
        {
            private readonly IEnumerator original;
            private readonly int generation;
            private readonly bool tracked;
            private bool started, disposed;
            private PassiveRoutine child;
            private object current;
            internal PassiveRoutine(IEnumerator original, int generation, bool tracked)
            { this.original = original; this.generation = generation; this.tracked = tracked; }
            public object Current { get { return current; } }
            public bool MoveNext()
            {
                if (disposed) return false;
                if (generation != passiveGeneration) { Dispose(); return false; }
                if (!started) { started = true; if (tracked) runningPassiveChecks.Add(this); }
                try
                {
                    if (!original.MoveNext()) { Dispose(); return false; }
                    IEnumerator nested = original.Current as IEnumerator;
                    child = nested == null ? null : new PassiveRoutine(nested, generation, false);
                    current = child == null ? original.Current : child;
                    return true;
                }
                catch { Dispose(); throw; }
            }
            public void Reset() { throw new NotSupportedException(); }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (tracked) runningPassiveChecks.Remove(this);
                if (child != null) child.Dispose();
                IDisposable disposable = original as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }
        private sealed class PassivePanel
        {
            internal readonly UI_Panel_PassiveCheck Panel;
            private readonly List<PassiveVisual> defaults = new List<PassiveVisual>();
            private readonly float rootScaleX, rootScaleY, rootRotation, rootAlpha;
            private bool allowed;
            private float expires;
            private bool unscaled;
            internal PassivePanel(UI_Panel_PassiveCheck panel)
            {
                Panel = panel;
                rootScaleX = panel.scaleX; rootScaleY = panel.scaleY; rootRotation = panel.rotation; rootAlpha = panel.alpha;
                CaptureChildren(panel);
            }
            private void CaptureChildren(GComponent parent)
            {
                for (int i = 0; i < parent.numChildren; i++)
                {
                    GObject child = parent.GetChildAt(i);
                    defaults.Add(new PassiveVisual(child));
                    if (child is GComponent) CaptureChildren((GComponent)child);
                }
            }
            internal void Hide()
            {
                allowed = false;
                StopTransitions(Panel);
                foreach (PassiveVisual state in defaults) state.Restore();
                Panel.SetScale(rootScaleX, rootScaleY); Panel.rotation = rootRotation;
                Panel.alpha = 0f;
            }
            internal void PrepareToShow()
            { Hide(); allowed = true; expires = float.PositiveInfinity; Panel.alpha = 1f; }
            internal void Release()
            { Hide(); Panel.alpha = rootAlpha; allowed = true; expires = float.PositiveInfinity; }
            internal void Started(bool success)
            {
                allowed = true;
                Transition transition = success ? Panel.m_OnEntry_Success : Panel.m_OnEntry_Fail;
                unscaled = transition == null || transition.ignoreEngineTimeScale;
                float speed = transition == null ? 1f : transition.timeScale;
                expires = speed <= 0f ? float.PositiveInfinity :
                    (unscaled ? Time.unscaledTime : Time.time) + (transition == null ? 0f : transition.totalDuration / speed) + .5f;
            }
            internal void Update()
            {
                if (!allowed)
                {
                    if (Panel.alpha != 0f || Panel.m_OnEntry_Success.playing || Panel.m_OnEntry_Fail.playing) Hide();
                }
                else if ((unscaled ? Time.unscaledTime : Time.time) >= expires) Hide();
            }
        }
        private sealed class PassiveVisual
        {
            private readonly GObject target;
            private readonly float x, y, alpha, scaleX, scaleY, rotation;
            private readonly bool visible;
            internal PassiveVisual(GObject target)
            {
                this.target = target; x = target.x; y = target.y; alpha = target.alpha;
                scaleX = target.scaleX; scaleY = target.scaleY; rotation = target.rotation; visible = target.visible;
            }
            internal void Restore()
            {
                if (target.isDisposed) return;
                target.SetXY(x, y); target.alpha = alpha; target.SetScale(scaleX, scaleY);
                target.rotation = rotation; target.visible = visible;
            }
        }
    }
}
