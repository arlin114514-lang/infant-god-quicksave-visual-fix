using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FairyGUI;
using HarmonyLib;
using InfantGod.Animation;
using InfantGod.Core;
using InfantGod.Core.RewindSystem;
using InfantGod.UI.WindowSystem;
using Spine.Unity;
using UnityEngine;

namespace InfantGodQuickSaveFix
{
    [Serializable]
    public sealed class VisualSnapshot
    {
        public int format = 1;
        public string gameAssembly;
        public List<ActorState> actors = new List<ActorState>();
    }

    [Serializable]
    public sealed class ActorState
    {
        public string character;
        public int order;
        public CharacterAnimationSaveEntry animation;
        public string stateBackground;
        public string backgroundOverride;
        public bool hasBackgroundState;
        public string lips;
        public Color pupilColor;
        public TransformState renderRoot;
        public TransformState characterRoot;
        public TransformState instance;
        public TransformState skeleton;
        public float cameraSize;
        public List<PixelPhase> pixels = new List<PixelPhase>();
        public List<SpineTrack> tracks = new List<SpineTrack>();
        public List<SlotValue> slotOverrides = new List<SlotValue>();
        public List<SlotValue> persistentSlots = new List<SlotValue>();
        public List<PanelController> controllers = new List<PanelController>();
    }

    [Serializable]
    public sealed class TransformState
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public static TransformState Capture(Transform t)
        {
            if (t == null) return null;
            return new TransformState { position = t.localPosition, rotation = t.localRotation, scale = t.localScale };
        }
        public void Apply(Transform t)
        {
            if (t == null) return;
            t.localPosition = position; t.localRotation = rotation; t.localScale = scale;
        }
    }

    [Serializable]
    public sealed class PixelPhase
    {
        public string name;
        public long order;
        public float elapsed;
        public bool waiting;
        public float waitRemaining;
        public float parameterScale;
    }

    [Serializable]
    public sealed class SpineTrack
    {
        public int index;
        public string animation;
        public bool loop;
        public float time;
        public float timeScale;
        public float alpha;
    }

    [Serializable]
    public sealed class SlotValue { public string slot; public string value; }

    [Serializable]
    public sealed class PanelController { public string name; public string page; }

    internal static class StateIO
    {
        internal static T Get<T>(object target, string field)
        {
            if (target == null) return default(T);
            FieldInfo f = AccessTools.Field(target.GetType(), field);
            if (f == null) throw new MissingFieldException(target.GetType().FullName, field);
            return (T)f.GetValue(target);
        }

        internal static void Set(object target, string field, object value)
        {
            AccessTools.Field(target.GetType(), field).SetValue(target, value);
        }

        internal static Dictionary<string, CharacterRenderImage> Images(NPCPanelManager npc)
        {
            return Get<Dictionary<string, CharacterRenderImage>>(npc, "characterRenderImages");
        }

        internal static int Order(NPCPanelManager npc, string name)
        {
            GComponent panel = npc.GetPanelForCharacter(name);
            return panel != null && panel.parent != null ? panel.parent.GetChildIndex(panel) : int.MaxValue;
        }

        internal static VisualSnapshot Capture()
        {
            VisualSnapshot snapshot = new VisualSnapshot { gameAssembly = Entry.GameHash };
            NPCPanelManager npc = UnityEngine.Object.FindObjectOfType<NPCPanelManager>();
            if (npc == null) return snapshot;
            foreach (KeyValuePair<string, CharacterRenderImage> item in Images(npc))
            {
                GameObject instance = item.Value.GetCharacterInstance();
                if (instance == null) continue;
                CharacterAnimationController c = instance.GetComponent<CharacterAnimationController>();
                if (c == null) continue;
                SkeletonAnimation sk = Get<SkeletonAnimation>(c, "targetSkeletonAnimation");
                ActorState actor = new ActorState
                {
                    character = item.Key,
                    order = Order(npc, item.Key),
                    animation = c.CreateSaveEntry(),
                    stateBackground = AssetName(Get<GameObject>(c, "stateBackgroundPrefab")),
                    backgroundOverride = AssetName(Get<GameObject>(c, "backgroundOverride")),
                    hasBackgroundState = true,
                    lips = Get<string>(c, "currentLips"),
                    pupilColor = Get<Color>(c, "pupilColor"),
                    renderRoot = TransformState.Capture(Get<Transform>(item.Value, "_renderRoot")),
                    characterRoot = TransformState.Capture(item.Value.characterRoot),
                    instance = TransformState.Capture(instance.transform),
                    skeleton = TransformState.Capture(sk == null ? null : sk.transform),
                    cameraSize = Get<Camera>(item.Value, "_camera").orthographicSize
                };
                CopySlots(Get<Dictionary<string, string>>(c, "slotAttachmentOverrides"), actor.slotOverrides);
                PixelAnimationPlayer player = Get<PixelAnimationPlayer>(c, "pixelAnimationPlayer");
                if (player != null)
                {
                    CopySlots(Get<Dictionary<string, string>>(player, "persistentSlotAttachments"), actor.persistentSlots);
                    foreach (object a in Get<IList>(player, "activeAnimations"))
                    {
                        PixelAnimation asset = Get<PixelAnimation>(a, "Asset");
                        if (asset == null) continue;
                        actor.pixels.Add(new PixelPhase
                        {
                            name = asset.TechName, order = Get<long>(a, "Order"),
                            elapsed = Get<float>(a, "ElapsedSeconds"), waiting = Get<bool>(a, "Waiting"),
                            waitRemaining = Get<float>(a, "WaitRemaining"), parameterScale = Get<float>(a, "ParameterScale")
                        });
                    }
                }
                if (sk != null && sk.AnimationState != null)
                {
                    var tracks = sk.AnimationState.Tracks;
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        var track = tracks.Items[i];
                        if (track == null || track.Animation == null) continue;
                        actor.tracks.Add(new SpineTrack { index = i, animation = track.Animation.Name, loop = track.Loop,
                            time = track.TrackTime, timeScale = track.TimeScale, alpha = track.Alpha });
                    }
                }
                GComponent panel = npc.GetPanelForCharacter(item.Key);
                if (panel != null)
                    foreach (Controller controller in panel.Controllers)
                        actor.controllers.Add(new PanelController { name = controller.name, page = controller.selectedPage });
                snapshot.actors.Add(actor);
            }
            snapshot.actors.Sort(delegate(ActorState a, ActorState b) { return a.order.CompareTo(b.order); });
            return snapshot;
        }

        internal static void Apply(VisualSnapshot snapshot, List<CharacterAnimationSaveEntry> fallback)
        {
            NPCPanelManager npc = UnityEngine.Object.FindObjectOfType<NPCPanelManager>();
            if (npc == null) return;
            if (snapshot == null)
            {
                ApplyLegacy(npc, fallback);
                return;
            }
            Coroutine layout = Get<Coroutine>(npc, "speakerLayoutCoroutine");
            if (layout != null) npc.StopCoroutine(layout);
            Set(npc, "speakerLayoutCoroutine", null);
            Get<IDictionary>(npc, "speakerLayoutStates").Clear();
            GList list = Get<GList>(npc, "speakerList");
            var images = Images(npc);
            int restored = 0;
            foreach (ActorState actor in snapshot.actors)
            {
                CharacterRenderImage image;
                if (!images.TryGetValue(actor.character, out image) || image.GetCharacterInstance() == null) continue;
                GameObject instance = image.GetCharacterInstance();
                CharacterAnimationController c = instance.GetComponent<CharacterAnimationController>();
                if (c == null) continue;
                ApplyAnimation(c, actor.animation);
                if (actor.hasBackgroundState)
                {
                    GameObject bg = ResolveBackground(actor.stateBackground);
                    GameObject over = ResolveBackground(actor.backgroundOverride);
                    if (String.IsNullOrEmpty(actor.stateBackground) || bg != null) Set(c, "stateBackgroundPrefab", bg);
                    if (String.IsNullOrEmpty(actor.backgroundOverride) || over != null) Set(c, "backgroundOverride", over);
                    AccessTools.Method(typeof(CharacterAnimationController), "ApplyBackgroundFromState").Invoke(c,
                        new object[] { Get<CharacterState>(c, "currentCharacterState") });
                }
                Set(c, "currentLips", actor.lips);
                Set(c, "pupilColor", actor.pupilColor);
                RestoreSlots(Get<Dictionary<string, string>>(c, "slotAttachmentOverrides"), actor.slotOverrides);
                PixelAnimationPlayer player = Get<PixelAnimationPlayer>(c, "pixelAnimationPlayer");
                if (player != null)
                {
                    RestoreSlots(Get<Dictionary<string, string>>(player, "persistentSlotAttachments"), actor.persistentSlots);
                    long maxOrder = 0;
                    foreach (object a in Get<IList>(player, "activeAnimations"))
                    {
                        PixelAnimation asset = Get<PixelAnimation>(a, "Asset");
                        foreach (PixelPhase phase in actor.pixels)
                            if (asset != null && asset.TechName == phase.name)
                            {
                                Set(a, "Order", phase.order); Set(a, "ElapsedSeconds", phase.elapsed);
                                Set(a, "Waiting", phase.waiting); Set(a, "WaitRemaining", phase.waitRemaining);
                                Set(a, "ParameterScale", phase.parameterScale);
                                maxOrder = Math.Max(maxOrder, phase.order);
                                break;
                            }
                    }
                    Set(player, "nextOrder", maxOrder + 1);
                }
                AccessTools.Method(typeof(CharacterAnimationController), "ApplyCombinedSkin", Type.EmptyTypes).Invoke(c, null);
                SkeletonAnimation sk = Get<SkeletonAnimation>(c, "targetSkeletonAnimation");
                if (sk != null && sk.AnimationState != null)
                {
                    sk.AnimationState.ClearTracks();
                    foreach (SpineTrack track in actor.tracks)
                    {
                        if (sk.Skeleton.Data.FindAnimation(track.animation) == null) continue;
                        var entry = sk.AnimationState.SetAnimation(track.index, track.animation, track.loop);
                        entry.TrackTime = track.time; entry.TimeScale = track.timeScale; entry.Alpha = track.alpha;
                        entry.MixDuration = 0f;
                    }
                    sk.AnimationState.Apply(sk.Skeleton);
                }
                if (player != null) player.ReapplyPersistentSlotAttachments();
                if (actor.renderRoot != null) actor.renderRoot.Apply(Get<Transform>(image, "_renderRoot"));
                if (actor.characterRoot != null) actor.characterRoot.Apply(image.characterRoot);
                if (actor.instance != null)
                {
                    image.SetCharacterTransform(actor.instance.position, actor.instance.rotation.eulerAngles, actor.instance.scale);
                    actor.instance.Apply(instance.transform);
                }
                if (actor.skeleton != null && sk != null) actor.skeleton.Apply(sk.transform);
                Camera camera = Get<Camera>(image, "_camera");
                if (camera != null && actor.cameraSize > 0f) camera.orthographicSize = actor.cameraSize;
                GComponent panel = npc.GetPanelForCharacter(actor.character);
                if (panel != null)
                {
                    if (list != null && panel.parent == list) list.SetChildIndex(panel, Math.Min(restored, list.numChildren - 1));
                    foreach (PanelController state in actor.controllers)
                    {
                        Controller ctrl = panel.GetController(state.name);
                        if (ctrl != null && ctrl.HasPage(state.page)) ctrl.selectedPage = state.page;
                    }
                }
                restored++;
            }
            // Restoring a settled layout avoids replaying the entrance width animation.
            AccessTools.Method(typeof(NPCPanelManager), "ApplyFinalSpeakerLayoutAfterRemoval").Invoke(npc, null);
            if (list != null) list.EnsureBoundsCorrect();
            Entry.Log("已在人物重建后恢复动画、背景、位置与顺序：" + restored + "/" + snapshot.actors.Count);
        }

        internal static void ApplyAnimation(CharacterAnimationController c, CharacterAnimationSaveEntry a)
        {
            if (a != null) c.RestoreRuntimeState(a.stateTechName, a.skins, a.defaultBodySkin, a.activePixelAnimations);
        }

        private static void ApplyLegacy(NPCPanelManager npc, List<CharacterAnimationSaveEntry> entries)
        {
            if (entries == null) return;
            foreach (var image in Images(npc))
            {
                if (image.Value.GetCharacterInstance() == null) continue;
                CharacterAnimationController c = image.Value.GetCharacterInstance().GetComponent<CharacterAnimationController>();
                if (c == null) continue;
                foreach (var entry in entries)
                    if (String.Equals(entry.characterTechName, c.GetNPCTechName(), StringComparison.OrdinalIgnoreCase))
                    { ApplyAnimation(c, entry); break; }
                if (Get<GameObject>(c, "stateBackgroundPrefab") == null)
                {
                    CharacterDefinition definition = Get<CharacterDefinition>(c, "characterDefinition");
                    if (definition != null)
                    {
                        GameObject bg = null;
                        foreach (CharacterState initial in definition.InitialCharacterStates)
                            if (initial != null && initial.HasBackgroundOverride) bg = initial.BackgroundPrefab;
                        if (bg != null)
                        {
                            Set(c, "stateBackgroundPrefab", bg);
                            AccessTools.Method(typeof(CharacterAnimationController), "ApplyBackgroundFromState").Invoke(c,
                                new object[] { Get<CharacterState>(c, "currentCharacterState") });
                        }
                    }
                }
            }
            if (Singleton<DecorationManager>.HasInstance) Singleton<DecorationManager>.Instance.ReapplyAistaltBackground();
            Entry.Log("旧快速存档：已补恢复角色状态；精确位置、继承背景与动画进度需启用后重新 F5 记录。");
        }

        private static string AssetName(GameObject asset) { return asset == null ? null : asset.name; }

        internal static GameObject ResolveBackground(string name)
        {
            if (String.IsNullOrEmpty(name)) return null;
            GameObject found = null;
            foreach (GameObject candidate in Resources.LoadAll<GameObject>("Prefabs/BG"))
                if (candidate.name == name) { if (found != null && found != candidate) return null; found = candidate; }
            if (found != null) return found;
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                if (!candidate.scene.IsValid() && candidate.name == name)
                { if (found != null && found != candidate) return null; found = candidate; }
            if (found == null) Entry.Log("无法定位背景资源：" + name + "，保留游戏恢复的背景。");
            return found;
        }

        private static void CopySlots(Dictionary<string, string> source, List<SlotValue> target)
        { foreach (var item in source) target.Add(new SlotValue { slot = item.Key, value = item.Value }); }

        private static void RestoreSlots(Dictionary<string, string> target, List<SlotValue> source)
        { target.Clear(); foreach (SlotValue item in source) target[item.slot] = item.value; }
    }
}
