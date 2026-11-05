using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRKanojo.Unlock
{
    /// <summary>
    /// 解锁全部被锁内容（剧情 / 互动 / 体位 / 结局标记）。
    /// 直接调用游戏自带的开发用函数：
    ///   Manager.Game.InitData()      -> 重载 StoryList / InteractiveList / LessonList
    ///   Manager.Game.ALLClearData()  -> isStartMainFlag/isEnding=true、全部 isKiss/isAibu/...=true
    ///                                  storyPlayDict_/interactivePlayDict_ 全填 1、allHInfo 全填 1
    ///   Manager.Game.Save()          -> 写回 UserData/save/save.dat
    /// 全程反射，不引用 Assembly-CSharp：不赌类型名/字段名以外的任何东西。
    /// </summary>
    [BepInPlugin(Guid, "VRK Unlock All", "1.0.0")]
    public class UnlockPlugin : BaseUnityPlugin
    {
        public const string Guid = "vrk.unlock";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> UnlockAll;
        internal static ConfigEntry<bool> UnlockStory;
        internal static ConfigEntry<bool> UnlockInteractive;
        internal static ConfigEntry<bool> UnlockLessons;
        internal static ConfigEntry<bool> FlushToDisk;
        internal static ConfigEntry<float> RetrySeconds;
        internal static ConfigEntry<int> MaxAttempts;
        internal static ConfigEntry<bool> Verbose;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("Unlock", "Enabled", true,
                "总开关。true = 启动后自动解锁全部内容（只做一次，结果写进存档）。");
            UnlockAll = Config.Bind("Unlock", "UnlockAll", true,
                "调用游戏自带的 ALLClearData()：剧情 + 互动 + 体位表 + 结局标记，一次全给。");
            UnlockStory = Config.Bind("Unlock", "UnlockStory", true,
                "UnlockAll=false 时，单独解锁剧情（ALLScenarioData）。");
            UnlockInteractive = Config.Bind("Unlock", "UnlockInteractive", true,
                "UnlockAll=false 时，单独解锁互动（ALLInteraciveData，游戏里就是这么拼的）。");
            UnlockLessons = Config.Bind("Unlock", "UnlockLessons", true,
                "UnlockAll=false 时，单独补 allHInfo（体位/爱抚计数表）。");
            FlushToDisk = Config.Bind("Unlock", "FlushToDisk", true,
                "解锁后立刻调用 Game.Save() 写回 UserData/save/save.dat。");
            RetrySeconds = Config.Bind("Unlock", "RetrySeconds", 2f,
                "没找到 Game 单例时多久重试一次（秒）。");
            MaxAttempts = Config.Bind("Unlock", "MaxAttempts", 30,
                "最多重试多少次，防止在菜单外空转。");
            Verbose = Config.Bind("Debug", "Verbose", false,
                "打印每次重试的详细状态。");

            var go = new GameObject("VRK_Unlock_Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<UnlockRuntime>();

            Log.LogInfo("VRK Unlock All 1.0.0 ready. enabled=" + Enabled.Value + " unlockAll=" + UnlockAll.Value);
        }
    }

    internal class UnlockRuntime : MonoBehaviour
    {
        /// <summary>候选类型：游戏里存在两份同名 Game（Manager.Game 与拼错的 GmaeManager.Game），两份都试。</summary>
        private static readonly string[] CandidateTypeNames = { "Manager.Game", "GmaeManager.Game" };

        private static readonly string[] LoaderScenes =
        {
            "Init", "Init2", "CheckHMD", "SetHMD", "Logo2", "Logo", "Attention", "Title"
        };

        private float _nextTry;
        private int _tries;
        private bool _done;

        private static string ActiveScene()
        {
            try
            {
                Scene s = SceneManager.GetActiveScene();
                return s.name;
            }
            catch { return string.Empty; }
        }

        private static bool IsMenuScene(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return true;
            for (int i = 0; i < LoaderScenes.Length; i++)
            {
                if (string.Equals(LoaderScenes[i], scene, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void Update()
        {
            if (_done) return;
            if (!UnlockPlugin.Enabled.Value)
            {
                _done = true;
                return;
            }
            if (Time.unscaledTime < _nextTry) return;

            _nextTry = Time.unscaledTime + Mathf.Max(0.5f, UnlockPlugin.RetrySeconds.Value);
            _tries++;
            if (_tries > Mathf.Max(1, UnlockPlugin.MaxAttempts.Value))
            {
                _done = true;
                UnlockPlugin.Log.LogWarning("unlock: gave up after " + _tries + " attempts (Game singleton never became usable).");
                return;
            }

            string scene = ActiveScene();

            // 只在菜单/加载场景动手：InitData() 会把计数器清零，正片里跑会打断当前进度。
            if (!IsMenuScene(scene))
            {
                if (UnlockPlugin.Verbose.Value)
                {
                    UnlockPlugin.Log.LogInfo("unlock: waiting for a menu scene (now in '" + scene + "').");
                }
                return;
            }

            bool anyDone = false;
            foreach (string typeName in CandidateTypeNames)
            {
                if (TryUnlockType(typeName, scene))
                {
                    anyDone = true;
                }
            }

            if (anyDone)
            {
                _done = true;
            }
            else if (UnlockPlugin.Verbose.Value)
            {
                UnlockPlugin.Log.LogInfo("unlock: attempt " + _tries + " in '" + scene + "' -> Game not ready yet.");
            }
        }

        private static Type ResolveType(string fullName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type t = null;
                try { t = asms[i].GetType(fullName, false); }
                catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static object GetSingleton(Type t)
        {
            try
            {
                PropertyInfo p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (p != null) return p.GetValue(null, null);
                FieldInfo f = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }

        private static int CountOf(object target, string fieldName)
        {
            try
            {
                FieldInfo f = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return -1;
                object v = f.GetValue(target);
                if (v == null) return 0;
                System.Collections.ICollection c = v as System.Collections.ICollection;
                if (c != null) return c.Count;
                return -1;
            }
            catch { return -1; }
        }

        private static bool HasMethod(object target, string name)
        {
            if (target == null) return false;
            try
            {
                return target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null;
            }
            catch { return false; }
        }

        private static bool Invoke(object target, string name)
        {
            try
            {
                MethodInfo m = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null || m.GetParameters().Length != 0) return false;
                m.Invoke(target, null);
                return true;
            }
            catch (Exception e)
            {
                UnlockPlugin.Log.LogWarning("unlock: " + name + " threw " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>返回 true 表示这个 Game 单例已处理完（不论解锁与保存是否成功）。</summary>
        private static bool TryUnlockType(string typeName, string scene)
        {
            Type t = ResolveType(typeName);
            if (t == null) return false;

            object inst = GetSingleton(t);
            if (inst == null)
            {
                if (UnlockPlugin.Verbose.Value)
                {
                    UnlockPlugin.Log.LogInfo("unlock: " + typeName + " exists but Instance is null in '" + scene + "'.");
                }
                return false;
            }

            // ALLClearData()/ALLScenarioData() 依赖已加载的列表；列表为空就先跑游戏自己的 InitData()。
            if (CountOf(inst, "storyListDict_") == 0)
            {
                if (!HasMethod(inst, "InitData"))
                {
                    if (UnlockPlugin.Verbose.Value)
                    {
                        UnlockPlugin.Log.LogInfo("unlock: " + typeName + " has empty story list and no InitData().");
                    }
                    return false;
                }
                UnlockPlugin.Log.LogInfo("unlock: " + typeName + " lists are empty -> InitData()");
                if (!Invoke(inst, "InitData")) return false;
                if (CountOf(inst, "storyListDict_") == 0)
                {
                    UnlockPlugin.Log.LogWarning("unlock: " + typeName + " InitData() ran but story list is still empty; retrying later.");
                    return false;
                }
            }

            bool touched = false;
            if (UnlockPlugin.UnlockAll.Value)
            {
                touched |= Invoke(inst, "ALLClearData");
            }
            else
            {
                if (UnlockPlugin.UnlockStory.Value) touched |= Invoke(inst, "ALLScenarioData");
                if (UnlockPlugin.UnlockInteractive.Value) touched |= Invoke(inst, "ALLInteraciveData");
            }
            if (!touched) return false;

            int story = CountOf(inst, "storyPlayDict_");
            int inter = CountOf(inst, "interactivePlayDict_");
            int hinfo = CountOf(inst, "allHInfo");

            bool saved = false;
            if (UnlockPlugin.FlushToDisk.Value)
            {
                saved = Invoke(inst, "Save");
            }

            UnlockPlugin.Log.LogInfo(
                "unlock done on '" + typeName + "' in '" + scene + "': story=" + story +
                " interactive=" + inter + " hinfo=" + hinfo +
                " saved=" + saved);
            return true;
        }
    }
}
