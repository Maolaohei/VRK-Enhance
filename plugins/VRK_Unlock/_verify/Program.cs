using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int _fail;
    private static int _pass;

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("[PASS] " + what); }
        else { _fail++; Console.WriteLine("[FAIL] " + what); }
    }

    private static IEnumerable<Instruction> Body(MethodDefinition m)
    {
        if (m == null || !m.HasBody) yield break;
        foreach (var i in m.Body.Instructions) yield return i;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition mod)
    {
        foreach (var t in mod.Types)
        {
            yield return t;
            foreach (var n in Walk(t)) yield return n;
        }
    }

    private static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var x in Walk(n)) yield return x;
        }
    }

    private static IEnumerable<string> Strs(MethodDefinition m)
    {
        foreach (var i in Body(m))
            if (i.OpCode == OpCodes.Ldstr && i.Operand is string s) yield return s;
    }

    private static IEnumerable<string> Calls(MethodDefinition m)
    {
        foreach (var i in Body(m))
            if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr)
                yield return mr.DeclaringType.Name + "::" + mr.Name;
    }

    private static MethodDefinition Find(TypeDefinition t, string name)
    {
        return t.Methods.FirstOrDefault(x => x.Name == name);
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: unlockverify <VRK_Unlock.dll>"); return 2; }
        var mod = ModuleDefinition.ReadModule(args[0]);
        Console.WriteLine("dll: " + mod.Assembly.Name.Name + " " + mod.Assembly.Name.Version);

        var refs = mod.AssemblyReferences.Select(r => r.Name).ToList();
        Check(refs.Contains("mscorlib"), "A1 引用 mscorlib");
        Check(refs.Contains("UnityEngine"), "A2 引用 UnityEngine");
        Check(refs.Contains("BepInEx"), "A3 引用 BepInEx");
        Check(!refs.Contains("Assembly-CSharp"), "A4 不引用 Assembly-CSharp（纯反射，无版本耦合）");
        Check(!refs.Contains("0Harmony"), "A5 不用 Harmony（只调游戏自带函数）");

        var plugin = AllTypes(mod).FirstOrDefault(t => t.Name == "UnlockPlugin");
        Check(plugin != null, "B1 UnlockPlugin 存在");
        var attr = plugin.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin");
        Check(attr != null, "B2 有 [BepInPlugin]");
        if (attr != null)
        {
            Check(attr.ConstructorArguments.Count == 3 && (string)attr.ConstructorArguments[0].Value == "vrk.unlock",
                "B3 guid = vrk.unlock");
            Check(attr.ConstructorArguments.Count == 3 && (string)attr.ConstructorArguments[2].Value == "1.0.0",
                "B4 version = 1.0.0");
        }

        var runtime = AllTypes(mod).FirstOrDefault(t => t.Name == "UnlockRuntime");
        Check(runtime != null, "C1 UnlockRuntime 存在");

        var tryUnlock = runtime == null ? null : Find(runtime, "TryUnlockType");
        var tryStrs = tryUnlock == null ? new List<string>() : Strs(tryUnlock).ToList();
        Check(tryStrs.Contains("ALLClearData"), "C2 调用 ALLClearData（游戏自带全解锁）");
        Check(tryStrs.Contains("ALLScenarioData"), "C3 有 ALLScenarioData 分支");
        Check(tryStrs.Contains("ALLInteraciveData"), "C4 有 ALLInteraciveData 分支（游戏原拼写）");
        Check(tryStrs.Contains("InitData"), "C5 列表为空时先跑 InitData");
        Check(tryStrs.Contains("Save"), "C6 解锁后调用 Save 写盘");
        Check(tryStrs.Contains("storyPlayDict_"), "C7 复核 storyPlayDict_");
        Check(tryStrs.Contains("interactivePlayDict_"), "C8 复核 interactivePlayDict_");
        Check(tryStrs.Contains("allHInfo"), "C9 复核 allHInfo");

        var resolve = runtime == null ? null : Find(runtime, "ResolveType");
        var resolveStrs = resolve == null ? new List<string>() : Strs(resolve).ToList();
        var decl = runtime == null ? null : runtime.Fields.FirstOrDefault(f => f.Name == "CandidateTypeNames");
        Check(decl != null, "D1 有候选类型表");
        var cands = new List<string>();
        if (runtime != null)
        {
            foreach (var m in runtime.Methods.Where(x => x.Name == ".cctor"))
                cands.AddRange(Strs(m));
        }
        Check(cands.Contains("Manager.Game"), "D2 候选含 Manager.Game");
        Check(cands.Contains("GmaeManager.Game"), "D3 候选含 GmaeManager.Game（游戏里确实有两份）");
        Check(resolve != null && Calls(resolve).Any(c => c.EndsWith("::GetType")), "D4 用 Assembly.GetType 反射");

        var update = runtime == null ? null : Find(runtime, "Update");
        var updStrs = update == null ? new List<string>() : Strs(update).ToList();
        Check(update != null, "E1 Update 存在");
        Check(updStrs.Any(s => s.Contains("gave up after")), "E2 有重试上限，不会无限空转");
        Check(update != null && Body(update).Any(i => i.Operand is MethodReference mr && mr.Name == "get_unscaledTime"),
            "E3 用 unscaledTime 计时");
        Check(Body(update).Any(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Ldfld) &&
            i.Operand is FieldReference fr && fr.Name == "_done"), "E4 有 _done 幂等标记");
        Check(runtime != null && Find(runtime, "IsMenuScene") != null, "E5 有 IsMenuScene 场景门禁");
        Check(Calls(update).Any(c => c.EndsWith("::IsMenuScene")), "E6 Update 只在菜单/加载场景动手（不打断正片）");

        var awake = plugin == null ? null : Find(plugin, "Awake");
        var awakeStrs = awake == null ? new List<string>() : Strs(awake).ToList();
        Check(awake != null, "F1 Awake 存在");
        Check(awakeStrs.Count(s => s.Contains("Unlock")) >= 4, "F2 Awake 里绑定了 Unlock 配置");
        Check(!awakeStrs.Any(s => s.Contains("SystemInfo") || s.Contains("VRDevice")), "F3 Awake 不碰图形/VR 设备 API");
        Check(awakeStrs.Contains("VRK_Unlock_Runtime"), "F4 创建运行时物体");
        Check(Calls(awake).Any(c => c.EndsWith("::DontDestroyOnLoad")), "F5 DontDestroyOnLoad 保活");

        Console.WriteLine();
        Console.WriteLine("RESULT: " + _pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }
}
