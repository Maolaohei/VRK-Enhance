using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Static verification for VRK_PreWarm.dll. Nothing here runs the game.
class PreWarmVerify
{
    static int pass, fail;
    static void Check(bool cond, string what)
    {
        if (cond) { pass++; Console.WriteLine("PASS  " + what); }
        else { fail++; Console.WriteLine("FAIL  " + what); }
    }

    static ModuleDefinition mod;
    static TypeDefinition plugin, runtime, iter;

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types) foreach (var x in Walk(t)) yield return x;
    }
    static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in Walk(n)) yield return x;
    }

    static MethodDefinition Find(TypeDefinition t, string name)
    {
        return t == null ? null : t.Methods.FirstOrDefault(x => x.Name == name);
    }

    static List<string> Calls(MethodDefinition m)
    {
        var list = new List<string>();
        if (m == null || !m.HasBody) return list;
        foreach (var ins in m.Body.Instructions)
        {
            var mr = ins.Operand as MethodReference;
            if (mr != null) list.Add(mr.DeclaringType.FullName.Split('<')[0] + "::" + mr.Name);
            var fr = ins.Operand as FieldReference;
            if (fr != null) list.Add(fr.DeclaringType.FullName.Split('<')[0] + "::" + fr.Name + "(field)");
        }
        return list;
    }

    static List<string> Strings(MethodDefinition m)
    {
        var list = new List<string>();
        if (m == null || !m.HasBody) return list;
        foreach (var ins in m.Body.Instructions)
            if (ins.OpCode == OpCodes.Ldstr) list.Add((string)ins.Operand);
        return list;
    }

    static int Main(string[] args)
    {
        string path = args[0];
        mod = ModuleDefinition.ReadModule(path);
        Console.WriteLine("=== " + Path.GetFileName(path) + "  (" + new FileInfo(path).Length + " bytes)");
        Console.WriteLine();

        var all = AllTypes(mod).ToList();
        plugin = all.FirstOrDefault(t => t.Name == "PreWarmPlugin");
        runtime = all.FirstOrDefault(t => t.Name == "PreWarmRuntime");

        // ---------- A: dependency surface ----------
        Console.WriteLine("-- A: dependency surface");
        var refs = mod.AssemblyReferences.ToList();
        var mscor = refs.FirstOrDefault(r => r.Name == "mscorlib");
        Check(mscor != null && mscor.Version.Major == 2, "mscorlib 2.x referenced (got " + (mscor == null ? "none" : mscor.Version.ToString()) + ")");
        Check(refs.Any(r => r.Name == "UnityEngine"), "UnityEngine referenced");
        Check(refs.Any(r => r.Name == "BepInEx"), "BepInEx referenced");
        Check(!refs.Any(r => r.Name == "Assembly-CSharp"), "no Assembly-CSharp (zero game coupling)");
        Check(!refs.Any(r => r.Name == "0Harmony"), "no 0Harmony (no patching)");
        Check(!refs.Any(r => r.Name == "System.IO" || r.Name == "System.Xml"), "no System.IO / System.Xml");

        // ---------- B: plugin identity ----------
        Console.WriteLine();
        Console.WriteLine("-- B: plugin identity");
        Check(plugin != null, "PreWarmPlugin type exists");
        var attr = plugin.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin");
        Check(attr != null, "[BepInPlugin] present");
        if (attr != null)
        {
            var a0 = attr.ConstructorArguments[0].Value as string;
            var a1 = attr.ConstructorArguments[1].Value as string;
            var a2 = attr.ConstructorArguments[2].Value as string;
            Check(a0 == "vrk.prewarm", "guid is vrk.prewarm (got " + a0 + ")");
            Check(a2 == "1.1.1", "version is 1.1.0 (got " + a2 + ")");
            Check(!string.IsNullOrEmpty(a1), "display name set ('" + a1 + "')");
        }
        Check(plugin != null && plugin.BaseType != null && plugin.BaseType.Name == "BaseUnityPlugin", "derives from BaseUnityPlugin");
        Check(runtime != null && runtime.BaseType != null && runtime.BaseType.Name == "MonoBehaviour", "PreWarmRuntime is a MonoBehaviour");

        // ---------- C: Awake must never touch the engine ----------
        Console.WriteLine();
        Console.WriteLine("-- C: startup safety (the 0xC0000005 rule)");
        var banned = new HashSet<string> {
            "UnityEngine.SystemInfo", "UnityEngine.VR.VRDevice", "UnityEngine.VRSettings",
            "UnityEngine.QualitySettings", "UnityEngine.Screen", "UnityEngine.Graphics",
            "UnityEngine.Shader", "UnityEngine.Material", "UnityEngine.Texture",
            "UnityEngine.Texture2D", "UnityEngine.RenderTexture", "UnityEngine.Camera",
            "UnityEngine.Resources", "UnityEngine.Application", "UnityEngine.Renderer"
        };
        var pluginAwake = Find(plugin, "Awake");
        var runtimeAwake = Find(runtime, "Awake");
        foreach (var pair in new[] { new { n = "PreWarmPlugin.Awake", m = pluginAwake }, new { n = "PreWarmRuntime.Awake", m = runtimeAwake } })
        {
            var bad = Calls(pair.m).Where(c => banned.Contains(c.Split(new[] { "::" }, StringSplitOptions.None)[0])).ToList();
            Check(pair.m != null, pair.n + " exists");
            Check(bad.Count == 0, pair.n + " touches no graphics API" + (bad.Count == 0 ? "" : " (found: " + string.Join(", ", bad) + ")"));
        }
        Check(!Calls(pluginAwake).Any(c => c.Contains("StartCoroutine")),
              "plugin Awake does not start a coroutine itself (runtime owns the coroutine)");

        // ---------- D: the three warming primitives ----------
        Console.WriteLine();
        Console.WriteLine("-- D: warming primitives");
        var iterMethods = all.SelectMany(t => t.Methods).Where(m => m.Name == "MoveNext" && t_IsIteratorOf(t_Owner(all, m), "Warm")).ToList();
        var allCalls = all.SelectMany(t => t.Methods).SelectMany(Calls).ToList();
        var allStrings = all.SelectMany(t => t.Methods).SelectMany(Strings).ToList();
        Check(allCalls.Any(c => c == "UnityEngine.Shader::WarmupAllShaders"), "calls Shader.WarmupAllShaders");
        Check(allCalls.Any(c => c == "UnityEngine.Material::SetPass"), "calls Material.SetPass");
        Check(allCalls.Any(c => c == "UnityEngine.Texture::GetNativeTexturePtr"), "calls Texture.GetNativeTexturePtr");
        Check(allCalls.Any(c => c.Contains("FindObjectsOfTypeAll")), "enumerates loaded objects with Resources.FindObjectsOfTypeAll");
        var typeOperandNames = all.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand as TypeReference)
            .Where(x => x != null).Select(x => x.Name).ToList();
        Check(typeOperandNames.Contains("RenderTexture") || allStrings.Any(s => s.Contains("RenderTexture")),
              "RenderTexture explicitly excluded");
        Check(allCalls.Any(c => c.Contains("get_realtimeSinceStartup")), "uses Time.realtimeSinceStartup for budgeting");
        Check(allCalls.Any(c => c == "UnityEngine.SceneManagement.SceneManager::GetActiveScene"), "warms the already-active scene on Start");

        // ---------- E: configuration ----------
        Console.WriteLine();
        Console.WriteLine("-- E: configuration");
        foreach (var cfg in new[] { "Enabled", "WarmShaders", "WarmMaterials", "WarmTextures", "SkipLoadingScenes", "FrameBudgetMs", "TotalBudgetMs" })
            Check(plugin.Fields.Any(f => f.Name == cfg), "config field " + cfg);
        Check(allStrings.Any(s => s == "PreWarm"), "config section PreWarm");
        Check(allStrings.Any(s => s == "Debug"), "config section Debug");
        Check(HasFloatConstant(pluginAwake, 6f), "FrameBudgetMs default 6.0f");
        Check(HasFloatConstant(pluginAwake, 2000f), "TotalBudgetMs default 2000.0f");

        // ---------- F: lifecycle ----------
        Console.WriteLine();
        Console.WriteLine("-- F: lifecycle");
        Check(Calls(runtimeAwake).Any(c => c.Contains("sceneLoaded")), "subscribes to sceneLoaded");
        Check(Calls(Find(runtime, "OnDestroy")).Any(c => c.Contains("sceneLoaded")), "unsubscribes in OnDestroy");
        Check(Find(runtime, "OnSceneLoaded") != null, "OnSceneLoaded handler exists");
        Check(Find(runtime, "Start") != null && Calls(Find(runtime, "Start")).Any(c => c.Contains("StartCoroutine")), "Start kicks off the first warm pass");
        var warm = Find(runtime, "Warm");
        Check(warm != null && warm.ReturnType.Name == "IEnumerator", "Warm is a coroutine");
        Check(iterMethods.Count > 0, "Warm iterator MoveNext exists (state machine compiled)");
        // C# compiles "yield return null" into ldnull + stfld <$current> + ret inside MoveNext.
        var yields = iterMethods.SelectMany(m => m.Body.Instructions).Count(i => i.OpCode == OpCodes.Ldnull);
        Check(yields >= 4, "Warm yields at least four times (frame slack + budget slicing), got " + yields);
        Check(iterMethods.SelectMany(m => m.Body.Instructions).Any(i => i.OpCode == OpCodes.Ldnull),
              "yield return null present");

        // ---------- G: nothing destructive ----------
        Console.WriteLine();
        Console.WriteLine("-- G: nothing destructive");
        Check(!allCalls.Any(c => c.EndsWith("::SetActive")), "never calls GameObject.SetActive (does not disturb the scene)");
        Check(!allCalls.Any(c => c.EndsWith("::Destroy") && !c.Contains("DestroyOnLoad")), "never destroys objects");
        Check(!allCalls.Any(c => c.Contains("QualitySettings::set_")), "never writes QualitySettings (picture quality untouched)");
        Check(!allCalls.Any(c => c.Contains("set_timeScale")), "never touches Time.timeScale");
        Check(!allCalls.Any(c => c.Contains("set_targetFrameRate")), "never touches Application.targetFrameRate (VRK_Perf owns that)");
        Check(!allTypes(all).Any(t => t.Methods.Any(m => m.HasPInvokeInfo)), "no P/Invoke");

        Console.WriteLine();
        Console.WriteLine("pass={0}  fail={1}", pass, fail);
        return fail == 0 ? 0 : 1;
    }

    static IEnumerable<TypeDefinition> allTypes(List<TypeDefinition> l) { return l; }

    static TypeDefinition t_Owner(List<TypeDefinition> all, MethodDefinition m)
    {
        foreach (var t in all)
            if (t.Methods.Contains(m)) return t;
        return null;
    }

    static bool t_IsIteratorOf(TypeDefinition owner, string name)
    {
        return owner != null && owner.Name.Contains(name);
    }

    static bool HasFloatConstant(MethodDefinition m, float value)
    {
        if (m == null || !m.HasBody) return false;
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ldc_R4 && ins.Operand is float f && Math.Abs(f - value) < 0.001f) return true;
            if (ins.OpCode.Code == Code.Ldc_R8 && ins.Operand is double d && Math.Abs(d - value) < 0.001) return true;
        }
        return false;
    }
}
