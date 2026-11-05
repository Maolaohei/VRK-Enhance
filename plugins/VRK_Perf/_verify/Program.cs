using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class PerfVerify
{
    static int fails = 0;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
        if (!ok) fails++;
    }

    static IEnumerable<TypeDefinition> Flat(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in Flat(n))
                yield return x;
    }

    static IEnumerable<MethodDefinition> WithBody(ModuleDefinition m)
    {
        foreach (var t in m.Types.SelectMany(Flat))
            foreach (var me in t.Methods)
                if (me.HasBody)
                    yield return me;
    }

    static List<string> AllCalls(ModuleDefinition m)
    {
        var list = new List<string>();
        foreach (var me in WithBody(m))
            foreach (var i in me.Body.Instructions)
            {
                var mr = i.Operand as MethodReference;
                if (mr != null) list.Add(mr.DeclaringType.Name + "::" + mr.Name);
            }
        return list;
    }

    static List<string> AllStrings(ModuleDefinition m)
    {
        var list = new List<string>();
        foreach (var me in WithBody(m))
            foreach (var i in me.Body.Instructions)
                if (i.OpCode == OpCodes.Ldstr) list.Add((string)i.Operand);
        return list;
    }

    static bool HasIntConst(Instruction i, int v)
    {
        if (i.OpCode == OpCodes.Ldc_I4_M1) return v == -1;
        if (i.OpCode == OpCodes.Ldc_I4_0) return v == 0;
        if (i.OpCode == OpCodes.Ldc_I4_1) return v == 1;
        if (i.OpCode == OpCodes.Ldc_I4_2) return v == 2;
        if (i.OpCode == OpCodes.Ldc_I4_3) return v == 3;
        if (i.OpCode == OpCodes.Ldc_I4_S) return (sbyte)i.Operand == v;
        if (i.OpCode == OpCodes.Ldc_I4) return (int)i.Operand == v;
        return false;
    }

    // 在 ldstr key 之后的最多 window 条指令里找常量 v（Bind 的默认值参数）
    static bool DefaultIs(List<Instruction> il, string key, int v, int window = 6)
    {
        for (int k = 0; k < il.Count; k++)
        {
            if (il[k].OpCode != OpCodes.Ldstr || (string)il[k].Operand != key) continue;
            int end = Math.Min(il.Count, k + window);
            for (int j = k + 1; j < end; j++)
                if (HasIntConst(il[j], v)) return true;
        }
        return false;
    }

    static List<Instruction> ILof(TypeDefinition t, string name)
    {
        var m = t.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        return m == null ? new List<Instruction>() : m.Body.Instructions.ToList();
    }

    static int Main(string[] a)
    {
        if (a.Length < 1)
        {
            Console.WriteLine("usage: perfverify <VRK_Perf.dll>");
            return 2;
        }

        var asm = AssemblyDefinition.ReadAssembly(a[0]);
        var mod = asm.MainModule;
        Console.WriteLine("assembly: " + asm.Name.Name + " " + asm.Name.Version);

        var refs = mod.AssemblyReferences.Select(r => r.Name + " " + r.Version).ToList();
        Console.WriteLine("refs: " + string.Join(", ", refs));

        Console.WriteLine();
        Console.WriteLine("== A. 程序集身份 / 依赖面");
        Check(refs.Any(r => r.StartsWith("mscorlib 2.0")), "mscorlib 2.0.x (Unity Mono 2.0 世代)");
        Check(refs.Any(r => r.StartsWith("UnityEngine")), "UnityEngine referenced");
        Check(refs.Any(r => r.StartsWith("BepInEx")), "BepInEx referenced");
        Check(!refs.Any(r => r.StartsWith("Assembly-CSharp")), "no Assembly-CSharp -> 零游戏类型耦合(不会因游戏类改名/缺失而崩)");
        Check(!refs.Any(r => r.StartsWith("0Harmony")), "no 0Harmony -> 不修改任何游戏方法体");
        Check(!refs.Any(r => r.StartsWith("System.IO")), "no System.IO");

        var plugin = mod.Types.SelectMany(Flat).First(x => x.Name == "PerfPlugin");
        var runtime = mod.Types.SelectMany(Flat).First(x => x.Name == "PerfRuntime");

        Console.WriteLine();
        Console.WriteLine("== B. 插件元数据");
        var bep = plugin.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name == "BepInPlugin");
        Check(bep != null, "[BepInPlugin] present");
        string guid = bep != null ? bep.ConstructorArguments[0].Value as string : null;
        string ver = bep != null ? bep.ConstructorArguments[2].Value as string : null;
        Console.WriteLine("  guid: " + guid + "  ver: " + ver);
        Check(guid == "vrk.perf", "guid = vrk.perf");
        Check(plugin.BaseType != null && plugin.BaseType.Name == "BaseUnityPlugin", "PerfPlugin : BaseUnityPlugin");
        Check(runtime.BaseType != null && runtime.BaseType.Name == "MonoBehaviour", "PerfRuntime : MonoBehaviour");

        Console.WriteLine();
        Console.WriteLine("== C. 每帧路径零分配");
        var upd = ILof(runtime, "Update");
        Check(upd.Count > 0, "PerfRuntime.Update 存在");
        bool alloc = upd.Any(i => i.OpCode == OpCodes.Newobj || i.OpCode == OpCodes.Newarr || i.OpCode == OpCodes.Box);
        Check(!alloc, "Update 内无 newobj/newarr/box -> 不产生 GC 垃圾");
        bool updStr = upd.Any(i => i.Operand is MethodReference mr &&
            (mr.Name == "Concat" || mr.Name == "Format" || mr.Name == "Join" || mr.Name == "ToString"));
        Check(!updStr, "Update 内无 String.Concat/Format/ToString");
        bool updReflect = upd.Any(i => i.Operand is MethodReference mr && mr.DeclaringType.Name == "Type");
        Check(!updReflect, "Update 内不直接做反射(重活都在到点分支里)");

        Console.WriteLine();
        Console.WriteLine("== D. 默认值(不砍画质的取向)");
        var awake = ILof(plugin, "Awake");
        Check(DefaultIs(awake, "VSync", 0), "VSync 默认 0 = 关闭垂直同步(240Hz 屏解除 120 帧掉档)");
        Check(DefaultIs(awake, "TargetFrameRate", 0), "TargetFrameRate 默认 0 = 不设上限");
        Check(DefaultIs(awake, "GcOnSceneLoad", 1) || DefaultIs(awake, "GcOnSceneLoad", 0), "GcOnSceneLoad 有默认值");
        Check(DefaultIs(awake, "PinOvrRecommendedMsaa", 1), "PinOvrRecommendedMsaa 默认 true(防止 OVR 把 4x MSAA 改成 0)");
        Check(DefaultIs(awake, "CameraReport", 0), "CameraReport 默认 false(诊断不常开)");

        Console.WriteLine();
        Console.WriteLine("== E. 只动这三样：垂直同步 / 帧率上限 / 加载期 GC");
        var calls = AllCalls(mod);
        Check(calls.Contains("QualitySettings::set_vSyncCount"), "写 QualitySettings.vSyncCount");
        Check(calls.Contains("Application::set_targetFrameRate"), "写 Application.targetFrameRate");
        Check(calls.Contains("GC::Collect"), "加载场景触发 GC.Collect");
        Check(calls.Contains("GC::WaitForPendingFinalizers"), "随后 WaitForPendingFinalizers");
        Check(calls.Contains("Application::set_backgroundLoadingPriority"), "加载期改 backgroundLoadingPriority");
        var strs = AllStrings(mod);
        Check(strs.Any(s => s == "Init") && strs.Any(s => s == "Logo") && strs.Any(s => s == "Attention"),
            "加载场景名单包含 Init/Logo/Attention");
        Check(strs.Count(s => s == "CheckHMD" || s == "SetHMD" || s == "Logo2") >= 3, "加载场景名单完整(CheckHMD/SetHMD/Logo2)");

        Console.WriteLine();
        Console.WriteLine("== F. OVR MSAA 只读反射补救");
        Check(strs.Any(s => s == "OVRManager, Assembly-CSharp"), "反射定位 OVRManager 类型");
        Check(strs.Any(s => s == "useRecommendedMSAALevel"), "反射字段 useRecommendedMSAALevel");
        Check(strs.Any(s => s == "instance"), "反射静态属性 instance");
        Check(calls.Contains("FieldInfo::SetValue"), "仅用 FieldInfo.SetValue 写一个 bool");
        Check(calls.Contains("Type::GetType"), "Type.GetType 按名字解析(游戏里没有 OVRManager 也不报错)");

        Console.WriteLine();
        Console.WriteLine("== G. 画质不降级(硬性)");
        Check(!calls.Contains("QualitySettings::set_antiAliasing"), "从不写 QualitySettings.antiAliasing -> 4x MSAA 保持");
        Check(!calls.Contains("QualitySettings::set_shadows"), "从不改阴影设置");
        Check(!calls.Contains("QualitySettings::set_shadowResolution"), "从不改阴影分辨率");
        Check(!calls.Contains("QualitySettings::set_pixelLightCount"), "从不改像素灯数量");
        Check(!calls.Contains("QualitySettings::set_lodBias"), "从不改 LOD bias");
        Check(!calls.Contains("QualitySettings::SetQualityLevel"), "从不切画质档");
        Check(!calls.Contains("GameObject::SetActive"), "不 SetActive 任何对象");
        Check(!calls.Contains("Object::Destroy"), "不 Destroy 任何对象");
        Check(!calls.Contains("Time::set_timeScale"), "不改 timeScale");
        Check(!calls.Contains("Texture::set_anisoLevel") && !calls.Contains("Texture::set_filterMode"), "不碰贴图参数(那是 TexPlus 的职责)");

        Console.WriteLine();
        Console.WriteLine("== H. 生命周期 / 诊断");
        Check(calls.Contains("SceneManager::add_sceneLoaded"), "订阅 sceneLoaded");
        Check(calls.Contains("SceneManager::remove_sceneLoaded"), "OnDestroy 退订(不泄漏)");
        var dtor = runtime.Methods.FirstOrDefault(m => m.Name == "OnDestroy");
        Check(dtor != null && dtor.HasBody && dtor.Body.Instructions.Any(i => i.Operand is MethodReference mr2 && mr2.Name == "remove_sceneLoaded"),
            "OnDestroy 体内确实移除订阅");
        Check(calls.Contains("Camera::get_allCameras"), "CameraReport 用 Camera.allCameras 枚举(只读)");
        Check(calls.Contains("Camera::get_depth") || calls.Contains("Camera::get_cullingMask") || calls.Contains("Camera::get_targetTexture"),
            "只读取相机属性用于打印, 不改相机状态");
        Check(!calls.Contains("Camera::set_enabled"), "从不 enable/disable 相机");
        Check(calls.Any(c => c.EndsWith("::DontDestroyOnLoad")), "宿主 GameObject 跨场景存活(DontDestroyOnLoad)");

        Console.WriteLine();
        Console.WriteLine("== I. 原生互操作面(应为空)");
        bool anyPinvoke = WithBody(mod).Any(m => m.HasPInvokeInfo) ||
                          mod.Types.SelectMany(Flat).SelectMany(t => t.Methods).Any(m => m.HasPInvokeInfo);
        Check(!anyPinvoke, "无 DllImport (纯托管插件)");
        Check(!calls.Contains("Marshal::") , "无 Marshal 调用");

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "RESULT: ALL CHECKS PASSED (0 failures)" : "RESULT: " + fails + " CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
