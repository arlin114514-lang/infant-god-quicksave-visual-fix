using System;
using System.IO;
using System.Reflection;
using BepInEx;
using InfantGod.Core.ModSystem;
using UnityEngine;

namespace InfantGodQuickSaveBridge
{
    // Only this explicitly installed repair module is loaded. No general DLL scanning.
    [BepInPlugin("local.infantgod.quicksave.bridge", "幼神修复模组前置", "1.0.1")]
    public sealed class Bootstrap : BaseUnityPlugin
    {
        private Type entry;
        private bool active;
        private bool refresh = true;
        private bool master;
        private float nextPoll;

        private void Awake()
        {
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            ModLoader.ModPreferencesChanged += PreferencesChanged;
            ModLoader.ModsReloaded += PreferencesChanged;
            Logger.LogInfo("前置已启动；修复功能由游戏 Mod 列表中的「快速存读档画面修复」控制。");
        }

        private void PreferencesChanged() { refresh = true; }

        private void Update()
        {
            if (Time.realtimeSinceStartup < 2f || Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 1f;
            bool now = ModLoader.LoadModsOnGameStart;
            if (now != master) { master = now; refresh = true; }
            if (!refresh) return;
            try
            {
                if (entry != null && (bool)entry.GetProperty("IsBusy").GetValue(null, null)) return;
                refresh = false;
                string directory = null;
                foreach (ModLoader.ModListEntry mod in ModLoader.GetInstalledMods())
                    if (mod.Enabled && File.Exists(Path.Combine(mod.DirectoryPath, "quicksave-fix.module")))
                    {
                        directory = mod.DirectoryPath;
                        break;
                    }
                bool wanted = master && directory != null;
                if (wanted == active) return;
                if (wanted)
                {
                    if (entry == null)
                    {
                        string file = Path.Combine(directory, "Code", "InfantGodQuickSaveFix.dll");
                        entry = Assembly.LoadFrom(file).GetType("InfantGodQuickSaveFix.Entry", true);
                    }
                    entry.GetMethod("Enable").Invoke(null, new object[] { directory });
                    active = true;
                    Logger.LogInfo("快速存读档画面修复：已启用（来自游戏 Mod 列表）。");
                }
                else if (entry != null)
                {
                    entry.GetMethod("Disable").Invoke(null, null);
                    active = false;
                    Logger.LogInfo("快速存读档画面修复：已停用。");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("修复模组未能切换状态：" + ex);
            }
        }

        private void OnDestroy()
        {
            ModLoader.ModPreferencesChanged -= PreferencesChanged;
            ModLoader.ModsReloaded -= PreferencesChanged;
        }
    }
}
