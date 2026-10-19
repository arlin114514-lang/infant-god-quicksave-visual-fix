using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FairyGUI;
using HarmonyLib;
using InfantGod.Core;
using InfantGod.Core.RewindSystem;
using InfantGod.Core.SaveSystem;
using InfantGod.Core.SkillCheckSystem;
using InfantGod.UI.WindowSystem;
using InfantGod.UI.WindowSystem.Windows;
using LiveStreamWindow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace InfantGodQuickSaveFix
{
    public static partial class Entry
    {
        public const string Id = "local.infantgod.quicksave.visualfix";
        private const string JsonKey = "quickSaveVisualFix";
        private static Harmony harmony;
        private static Worker worker;
        private static readonly ConditionalWeakTable<RewindSnapshot, VisualSnapshot> visuals = new ConditionalWeakTable<RewindSnapshot, VisualSnapshot>();
        private static readonly Dictionary<string, VisualSnapshot> captures = new Dictionary<string, VisualSnapshot>();
        private static int restoring;
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings { ContractResolver = new FieldsOnlyResolver() };
        internal static string GameHash;
        public static bool IsBusy { get { return restoring > 0 || (worker != null && worker.pendingSave); } }

        public static void Log(string text) { Debug.Log("[QuickSaveVisualFix] " + text); }

        public static void Enable(string modDirectory)
        {
            if (harmony != null) return;
            using (SHA256 sha = SHA256.Create())
                GameHash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(typeof(RewindService).Assembly.Location))).Replace("-", "");
            ValidateGame();
            Harmony installed = new Harmony(Id);
            try
            {
                installed.CreateReversePatcher(AccessTools.Method(typeof(RewindService), "LoadRoutine", new Type[] { typeof(string), typeof(Action<string>), typeof(Action<bool, string>), typeof(bool) }),
                    new HarmonyMethod(typeof(Entry), "NativeLoad")).Patch();
                Patch(installed, typeof(RewindService), "CaptureSnapshot", null, "Captured", new Type[] { typeof(string) });
                Patch(installed, typeof(RewindService), "SaveSnapshot", null, "Saved", new Type[] { typeof(string), typeof(string).MakeByRefType(), typeof(bool).MakeByRefType() });
                Patch(installed, typeof(RewindService), "ReadSnapshot", null, "Read", new Type[] { typeof(string), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() });
                Patch(installed, typeof(RewindService), "RebindSpeakersAfterRestore", null, "Rebind", null);
                Patch(installed, typeof(RewindService), "RestoreLiveStreamControllers", null, "ControllersRestored", null);
                Patch(installed, typeof(RewindService), "LoadRoutine", null, "Loading", new Type[] { typeof(string), typeof(Action<string>), typeof(Action<bool, string>), typeof(bool) });
                Patch(installed, typeof(LiveStreamSkillCheckUI), "ForceHideForExternalRestore", "BeforeHide", "AfterHide", null);
                Patch(installed, typeof(SaveSystem), "RequestQuickSave", "BeforeQuickSave", null, null);
                Patch(installed, typeof(SaveSystem), "RequestQuickLoad", "BeforeQuickLoad", null, null);
                InstallPassiveHooks(installed);
                harmony = installed;
                GameObject host = new GameObject("BepInEx_QuickSaveVisualFix");
                UnityEngine.Object.DontDestroyOnLoad(host);
                worker = host.AddComponent<Worker>();
                CleanPassivePanels();
                Log("v1.0.1 已启用；F5/F9 和原生快速存档菜单均受修复。游戏指纹 " + GameHash.Substring(0, 12));
            }
            catch
            {
                installed.UnpatchSelf();
                throw;
            }
        }

        public static void Disable()
        {
            if (IsBusy) return;
            if (harmony != null) harmony.UnpatchSelf();
            harmony = null;
            captures.Clear();
            ClearPassiveTracking();
            if (worker != null) UnityEngine.Object.Destroy(worker.gameObject);
            worker = null;
            Log("已停用；原有快速存档仍可由游戏读取。");
        }

        private static void ValidateGame()
        {
            foreach (string field in new string[] { "characterRenderImages", "speakerList", "speakerLayoutCoroutine", "speakerLayoutStates" })
                Require(AccessTools.Field(typeof(NPCPanelManager), field), field);
            foreach (string field in new string[] { "stateBackgroundPrefab", "backgroundOverride", "currentCharacterState", "currentLips", "pupilColor", "pixelAnimationPlayer", "targetSkeletonAnimation", "slotAttachmentOverrides" })
                Require(AccessTools.Field(typeof(InfantGod.Animation.CharacterAnimationController), field), field);
            Require(AccessTools.Field(typeof(InfantGod.Animation.PixelAnimationPlayer), "activeAnimations"), "activeAnimations");
        }

        private static void Require(object value, string name)
        { if (value == null) throw new NotSupportedException("游戏版本不兼容，未安装补丁：" + name); }

        private static void Patch(Harmony h, Type type, string name, string prefix, string postfix, Type[] args)
        {
            MethodInfo target = args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args);
            Require(target, type.FullName + "." + name);
            h.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(Entry), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(Entry), postfix));
        }

        private static void Captured(string name, RewindSnapshot __result)
        {
            if (__result == null) return;
            try
            {
                VisualSnapshot state = StateIO.Capture();
                visuals.Add(__result, state);
                captures[name ?? String.Empty] = state;
                // The native activeCharacters order can differ from the actual visible list after swaps.
                if (__result.liveStreamCast != null && __result.liveStreamCast.characters != null)
                {
                    var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (ActorState actor in state.actors) order[actor.character] = actor.order;
                    __result.liveStreamCast.characters.Sort(delegate(string a, string b)
                    { return (order.ContainsKey(a) ? order[a] : int.MaxValue).CompareTo(order.ContainsKey(b) ? order[b] : int.MaxValue); });
                }
            }
            catch (Exception ex) { Debug.LogError("[QuickSaveVisualFix] 抓取画面状态失败：" + ex); }
        }

        private static void Saved(string name, string path, bool __result)
        {
            VisualSnapshot state;
            if (!__result || String.IsNullOrEmpty(path) || !captures.TryGetValue(name ?? String.Empty, out state)) return;
            string temp = path + ".visualfix.tmp";
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                root[JsonKey] = JObject.Parse(JsonConvert.SerializeObject(state, JsonSettings));
                File.WriteAllText(temp, root.ToString());
                File.Replace(temp, path, null);
                Log("快速存档已附带完整画面状态：" + state.actors.Count + " 个角色。");
            }
            catch (Exception ex)
            {
                Debug.LogError("[QuickSaveVisualFix] 画面扩展写入失败，原生存档已保留：" + ex);
                if (File.Exists(temp)) File.Delete(temp);
            }
            finally { captures.Remove(name ?? String.Empty); }
        }

        private static void Read(string file, RewindSnapshot __result)
        {
            if (__result == null || String.IsNullOrEmpty(file)) return;
            try
            {
                JToken node = JObject.Parse(File.ReadAllText(file))[JsonKey];
                if (node == null) return;
                VisualSnapshot state = JsonConvert.DeserializeObject<VisualSnapshot>(node.ToString(), JsonSettings);
                if (state == null || state.format != 1 || state.actors == null) return;
                visuals.Add(__result, state);
            }
            catch (Exception ex) { Log("画面扩展不可用，按旧存档恢复：" + ex.Message); }
        }

        private static void Rebind(RewindSnapshot snapshot, ref IEnumerator __result)
        { __result = RebindRoutine(__result, snapshot); }

        private static IEnumerator RebindRoutine(IEnumerator original, RewindSnapshot snapshot)
        {
            yield return original;
            // Unity Start/OnEnable and the old objects' delayed destruction must finish first.
            yield return null;
            ApplySnapshot(snapshot);
        }

        private static void ApplySnapshot(RewindSnapshot snapshot)
        {
            VisualSnapshot state;
            visuals.TryGetValue(snapshot, out state);
            List<InfantGod.Animation.CharacterAnimationSaveEntry> fallback = null;
            if (state == null && currentGame != null) fallback = currentGame.characterAnimationData;
            try { StateIO.Apply(state, fallback); }
            catch (Exception ex) { Debug.LogError("[QuickSaveVisualFix] 画面恢复失败：" + ex); }
            CleanSkillCheck();
            passiveShowsSuppressed = false;
        }

        private static SaveData currentGame;
        private static void Loading(string name, Action<string> log, Action<bool, string> onDone, bool onlyRestoreLiveStreamWindow, ref IEnumerator __result)
        {
            // Constructing the original iterator has no side effects. Recreate it with a deferred callback.
            __result = LoadWrapped(name, log, onDone, onlyRestoreLiveStreamWindow);
        }

        private static IEnumerator LoadWrapped(string name, Action<string> log, Action<bool, string> onDone, bool onlyLive)
        {
            if (restoring > 0) { if (onDone != null) onDone(false, "正在读档，请稍候"); yield break; }
            string file, error;
            RewindSnapshot snapshot = RewindService.ReadSnapshot(name, out file, out error);
            if (snapshot == null) { if (onDone != null) onDone(false, error); yield break; }
            currentGame = RewindGameState.Load(RewindGameState.PathFor(file));
            restoring++;
            if (worker != null) worker.CancelPendingSave();
            bool done = false, success = false;
            string message = null;
            try
            {
                BeginPassiveRestore();
                CleanSkillCheck();
                // ReversePatch provides the unmodified factory and avoids recursive wrapping.
                yield return NativeLoad(name, log, delegate(bool ok, string text) { done = true; success = ok; message = text; }, onlyLive);
                if (done && success && !(snapshot.vm != null && !String.IsNullOrEmpty(snapshot.vm.nodeName) && snapshot.runnerWasRunning))
                {
                    // The native routine skips rebuilding cast when no dialogue VM was active.
                    var speakers = UnityEngine.Object.FindObjectOfType<LiveStreamSpeakerManager>();
                    if (speakers != null && snapshot.liveStreamCast != null) speakers.RestoreCastState(snapshot.liveStreamCast);
                    yield return null;
                    ApplySnapshot(snapshot);
                }
                if (onDone != null) onDone(done && success, message ?? "快速存档恢复未完成");
            }
            finally { currentGame = null; restoring--; passiveShowsSuppressed = false; }
        }

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(RewindService), "LoadRoutine", new Type[] { typeof(string), typeof(Action<string>), typeof(Action<bool, string>), typeof(bool) })]
        private static IEnumerator NativeLoad(string name, Action<string> log, Action<bool, string> onDone, bool onlyLive)
        { throw new NotImplementedException("Reverse patch not installed"); }

        private static void ControllersRestored() { CleanSkillCheck(); }

        internal static void CleanSkillCheck()
        {
            LiveStreamSkillCheckUI ui = LiveStreamSkillCheckUI.Instance;
            if (ui != null) ui.ForceHideForExternalRestore();
            CleanPassivePanels();
        }

        private static void BeforeHide(LiveStreamSkillCheckUI __instance)
        {
            UI_Panel_SkillCheck panel = StateIO.Get<UI_Panel_SkillCheck>(__instance, "panelSkillCheck");
            if (panel != null && !panel.isDisposed) StopTransitions(panel);
        }

        private static void AfterHide(LiveStreamSkillCheckUI __instance)
        {
            UI_Panel_SkillCheck panel = StateIO.Get<UI_Panel_SkillCheck>(__instance, "panelSkillCheck");
            if (panel == null || panel.isDisposed) return;
            // Roll transitions animate alpha outside the normal controller gears.
            foreach (GObject result in new GObject[] { panel.m_Success, panel.m_Success_BG, panel.m_Fail, panel.m_Fail_BG })
                if (result != null) result.alpha = 0f;
        }

        private static void StopTransitions(GComponent panel)
        {
            foreach (Transition transition in panel.Transitions) transition.Stop(false, false);
            GTween.Kill(panel, false);
            for (int i = 0; i < panel.numChildren; i++)
            {
                GObject child = panel.GetChildAt(i);
                GTween.Kill(child, false);
                if (child is GComponent) StopTransitions((GComponent)child);
            }
        }

        internal static bool CheckInProgress()
        {
            if (runningPassiveChecks.Count > 0) return true;
            SkillCheckManager manager = SkillCheckManager.Instance;
            if (manager != null && StateIO.Get<bool>(manager, "activeCheckRunning")) return true;
            var ui = LiveStreamSkillCheckUI.Instance;
            Controller checking = ui == null ? null : StateIO.Get<Controller>(ui, "isCheckingController");
            return checking != null && checking.selectedIndex != 0;
        }

        private static bool BeforeQuickSave(SaveSystem __instance)
        {
            if (restoring > 0) return false;
            if (!CheckInProgress()) return true;
            if (worker != null && !worker.pendingSave) worker.StartCoroutine(worker.SaveWhenReady(__instance));
            return false;
        }

        private static void BeforeQuickLoad() { if (worker != null) worker.CancelPendingSave(); }

        // Unity's native serializer does not build nested type trees for externally loaded DTOs.
        // Restrict Json.NET to fields so Vector3.normalized/Color.linear cannot recurse.
        private sealed class FieldsOnlyResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization mode)
            {
                var result = new List<JsonProperty>();
                foreach (JsonProperty property in base.CreateProperties(type, mode))
                    if (type.GetField(property.UnderlyingName, BindingFlags.Public | BindingFlags.Instance) != null) result.Add(property);
                return result;
            }
        }

        public sealed class Worker : MonoBehaviour
        {
            internal bool pendingSave;
            private int generation;
            private GTextField notice;
            private float noticeUntil;
            private void Update()
            {
                if (notice != null && Time.realtimeSinceStartup > noticeUntil)
                { notice.Dispose(); notice = null; }
            }
            private void OnDestroy() { if (notice != null) notice.Dispose(); }
            private void LateUpdate() { UpdatePassivePanels(); }
            private void Notice(string text)
            {
                if (notice == null)
                {
                    notice = new GTextField(); notice.touchable = false;
                    notice.SetSize(850f, 46f); notice.SetXY(32f, 24f);
                    TextFormat format = notice.textFormat; format.size = 22; format.color = Color.white;
                    notice.textFormat = format; notice.stroke = 2f; notice.strokeColor = Color.black;
                    GRoot.inst.AddChild(notice);
                }
                notice.text = text; noticeUntil = Time.realtimeSinceStartup + 6f;
            }
            public void CancelPendingSave() { pendingSave = false; generation++; }
            public IEnumerator SaveWhenReady(SaveSystem system)
            {
                pendingSave = true;
                int ticket = ++generation;
                Log("检定动画进行中：已预约快速存档，检定结束后执行。再次按 F9 会取消这次预约。");
                Notice("已预约快速存档，将在本次检定结束后保存。");
                float deadline = Time.realtimeSinceStartup + 300f;
                while (ticket == generation && CheckInProgress() && Time.realtimeSinceStartup < deadline) yield return null;
                if (ticket != generation) yield break;
                pendingSave = false;
                if (system != null && !CheckInProgress())
                {
                    yield return null;
                    if (ticket == generation && restoring == 0) system.RequestQuickSave();
                }
                else Log("检定尚未完成，预约存档已取消；请在检定完成后重新按 F5。");
            }
        }
    }
}
