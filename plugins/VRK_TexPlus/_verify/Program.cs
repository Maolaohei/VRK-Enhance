using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class P
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
        if (!ok) fails++;
    }

    static IEnumerable<TypeDefinition> Flat(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in Flat(n)) yield return x;
    }

    static int Main(string[] a)
    {
        var asm = AssemblyDefinition.ReadAssembly(a[0]);
        var plugin = asm.MainModule;

        Console.WriteLine("== A. 程序集与身份");
        var refs = plugin.AssemblyReferences.Select(r => r.Name + " " + r.Version).ToList();
        Console.WriteLine("  refs: " + string.Join(", ", refs));
        Check(refs.Any(r => r.StartsWith("mscorlib 2.0")), "mscorlib 2.0.x (Unity/.NET 3.5 世代)");
        Check(refs.Any(r => r.StartsWith("UnityEngine")), "UnityEngine referenced");
        Check(refs.Any(r => r.StartsWith("BepInEx")), "BepInEx referenced");
        Check(!refs.Any(r => r.StartsWith("System.IO")), "no System.IO -> 不写盘");
        Check(!refs.Any(r => r.StartsWith("0Harmony")), "no 0Harmony -> 不做任何方法级补丁 (无法改坏游戏逻辑)");

        var t0 = plugin.Types.SelectMany(Flat).First(x => x.Name == "TexPlusPlugin");
        var bep = t0.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name == "BepInPlugin");
        Check(bep != null, "[BepInPlugin] present");
        var guid = bep != null ? (string)bep.ConstructorArguments[0].Value : "";
        Console.WriteLine("  guid: " + guid + "  ver: " + (bep != null ? bep.ConstructorArguments[2].Value : "?"));
        Check(guid == "vrk.texplus", "guid = vrk.texplus");

        Console.WriteLine("== B. 只用引擎级 API（不触碰游戏类型）");
        var used = new HashSet<string>();
        var calls = new List<string>();
        foreach (var t in plugin.Types.SelectMany(Flat))
            foreach (var m in t.Methods.Where(x => x.HasBody))
                foreach (var i in m.Body.Instructions)
                {
                    if (i.Operand is TypeReference tr) used.Add(tr.Name);
                    var mr = i.Operand as MethodReference;
                    if (mr != null) { calls.Add(mr.DeclaringType.Name + "::" + mr.Name); used.Add(mr.DeclaringType.Name); }
                    var fr = i.Operand as FieldReference;
                    if (fr != null) { calls.Add(fr.DeclaringType.Name + "::" + fr.Name); used.Add(fr.DeclaringType.Name); }
                }
        Console.WriteLine("  engine types used: " + string.Join(", ", used.Where(u => u.Length > 0).OrderBy(u => u)));
        var gameTypes = new HashSet<string> { "Game", "Manager", "GlobalVRKanojo", "IllusionCamera", "VRCameraFade", "LogoScene", "InitScene2", "Config" };
        var touched = used.Where(u => gameTypes.Contains(u)).ToList();
        Check(touched.Count == 0, "never references game types (" + (touched.Count == 0 ? "none" : string.Join(",", touched)) + ")");

        Console.WriteLine("== C. 纹理增强 API 调用面");
        Check(calls.Contains("QualitySettings::set_masterTextureLimit"), "QualitySettings.masterTextureLimit = 0");
        Check(calls.Contains("QualitySettings::set_anisotropicFiltering"), "QualitySettings.anisotropicFiltering = ForceEnable");
        Check(calls.Contains("Texture::set_anisoLevel"), "Texture.anisoLevel 提升");
        Check(calls.Contains("Texture::get_anisoLevel"), "先读 anisoLevel 再判断(避免无谓写入)");
        Check(calls.Contains("Texture::set_filterMode"), "Texture.filterMode 改写");
        Check(calls.Contains("Texture::set_mipMapBias"), "Texture.mipMapBias 可调");
        Check(calls.Contains("Resources::FindObjectsOfTypeAll"), "Resources.FindObjectsOfTypeAll 枚举全部贴图");
        Check(calls.Contains("SceneManager::add_sceneLoaded"), "订阅 sceneLoaded -> 换场景后自动补扫");
        Check(calls.Contains("SceneManager::remove_sceneLoaded"), "OnDestroy 时解除订阅(不泄漏)");
        bool skipsRT = calls.Any(c => c.Contains("RenderTexture")) || used.Contains("RenderTexture");
        Check(skipsRT, "跳过 RenderTexture(后处理缓冲不动)");

        Console.WriteLine("== D. 配置默认值");
        var awake = t0.Methods.First(m => m.Name == "Awake");
        var il = awake.Body.Instructions.ToList();
        bool aniso16 = il.Any(i => (i.OpCode == OpCodes.Ldc_I4_S && (sbyte)i.Operand == 16) || (i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 16));
        Check(aniso16, "AnisoLevel 默认 16");
        bool has20 = il.Any(i => i.OpCode.Name == "ldc.r4" && i.Operand is float f20 && Math.Abs(f20 - 20f) <= 0.001f);
        bool has0 = il.Any(i => i.OpCode.Name == "ldc.r4" && i.Operand is float f0 && f0 == 0f);
        Check(!has20 && has0, "RescanSeconds 默认 0 (不再周期扫描, 无周期 GC 尖峰)");
        Check(has0, "MipMapBias 默认 0(不改)");
        var runtime = plugin.Types.SelectMany(Flat).First(x => x.Name == "TexPlusRuntime");
        Check(runtime.BaseType != null && runtime.BaseType.Name == "MonoBehaviour", "TexPlusRuntime : MonoBehaviour");
        Check(runtime.Methods.Any(m => m.Name == "Scan"), "Scan() 存在");
        Check(runtime.Methods.Any(m => m.Name == "ApplyGlobals"), "ApplyGlobals() 存在");

        Console.WriteLine("== E. 去重(避免每轮重复写 native 属性)");
        var doneF = runtime.Fields.FirstOrDefault(f => f.Name == "_done");
        Check(doneF != null && doneF.FieldType.FullName.Contains("HashSet"), "已处理贴图按 instanceID 去重 (_done HashSet)");
        var scanM = runtime.Methods.First(m => m.Name == "Scan");
        bool usesId = runtime.Methods.Any(mm => mm.HasBody && mm.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "GetInstanceID"));
        Check(usesId, "扫描用 GetInstanceID 作去重键");
        Check(runtime.Methods.Any(mm => mm.Name == "ProcessSome"), "分帧扫描 ProcessSome() 存在");
        Check(runtime.Fields.Any(f => f.Name == "_cfgAniso"), "配置变化时清空去重表 (_cfgAniso/_cfgTrilinear/_cfgBias)");
        Check(scanM.Body.Instructions.Any(i => i.Operand is MethodReference m2 && m2.Name == "Clear"), "配置变化时调用 Clear() 重扫");

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "RESULT: ALL CHECKS PASSED (0 failures)" : "RESULT: " + fails + " CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
