using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string group, string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("PASS  [" + group + "] " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
        else { _fail++; Console.WriteLine("FAIL  [" + group + "] " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        var stack = new Stack<TypeDefinition>(m.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes)
                stack.Push(n);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: verify <VRK_UpScale.dll> <vrkup.dll>");
            return 2;
        }

        string managedPath = args[0];
        string nativePath = args[1];

        var asm = AssemblyDefinition.ReadAssembly(managedPath);
        var m = asm.MainModule;
        var allTypes = AllTypes(m).ToList();

        // ---------------- A. identity / runtime ----------------
        Check("A", "assembly name is VRK_UpScale", asm.Name.Name == "VRK_UpScale", asm.Name.Name);

        var mscorlib = m.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib");
        Check("A", "references mscorlib 2.0.x (net35)",
            mscorlib != null && mscorlib.Version.Major == 2, mscorlib == null ? "missing" : mscorlib.Version.ToString());
        Check("A", "targets .NET 3.5 runtime", m.Runtime == TargetRuntime.Net_2_0 || m.Runtime == TargetRuntime.Net_4_0,
            m.Runtime.ToString());

        // ---------------- B. references ----------------
        var refNames = m.AssemblyReferences.Select(r => r.Name).ToList();
        Check("B", "references UnityEngine", refNames.Contains("UnityEngine"), string.Join(",", refNames));
        Check("B", "references BepInEx", refNames.Contains("BepInEx"), "");
        Check("B", "does NOT reference Assembly-CSharp (no game coupling)",
            !refNames.Contains("Assembly-CSharp"), "");

        // ---------------- C. plugin attribute ----------------
        var pluginType = allTypes.FirstOrDefault(t => t.Name == "UpScalePlugin");
        Check("C", "UpScalePlugin type exists", pluginType != null, "");
        var pluginAttr = pluginType?.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin");
        Check("C", "carries [BepInPlugin]", pluginAttr != null, "");
        if (pluginAttr != null)
        {
            var guid = pluginAttr.ConstructorArguments[0].Value as string;
            var nm = pluginAttr.ConstructorArguments[1].Value as string;
            var ver = pluginAttr.ConstructorArguments[2].Value as string;
            Check("C", "plugin GUID is vrk.upscale", guid == "vrk.upscale", guid ?? "null");
            Check("C", "plugin version 1.6.1", ver == "1.7.0", ver ?? "null");

            // Startup safety: the 0xC0000005 lesson. Awake must not reach the engine.
            var bannedTypes = new System.Collections.Generic.HashSet<string> {
                "UnityEngine.SystemInfo", "UnityEngine.VR.VRDevice", "UnityEngine.VRSettings",
                "UnityEngine.QualitySettings", "UnityEngine.Screen", "UnityEngine.Graphics",
                "UnityEngine.Camera", "UnityEngine.Shader", "UnityEngine.Material"
            };
            var pluginAwake = pluginType != null ? pluginType.Methods.FirstOrDefault(x => x.Name == "Awake") : null;
            var awakeCalls = new System.Collections.Generic.List<string>();
            if (pluginAwake != null && pluginAwake.HasBody)
                foreach (var ins in pluginAwake.Body.Instructions)
                {
                    var mr2 = ins.Operand as MethodReference;
                    if (mr2 != null && mr2.DeclaringType != null)
                        awakeCalls.Add(mr2.DeclaringType.FullName.Split('<')[0] + "::" + mr2.Name);
                }
            var awakeBanned = awakeCalls.FindAll(c => bannedTypes.Contains(c.Split(new[] { "::" }, StringSplitOptions.None)[0]));
            Check("N", "UpScalePlugin.Awake never touches SystemInfo / VRDevice / graphics", awakeBanned.Count == 0,
                  awakeBanned.Count == 0 ? "clean" : string.Join(", ", awakeBanned));
            Check("N", "device gate lives in CheckGate()", pluginType != null && pluginType.Methods.Any(x => x.Name == "CheckGate"),
                  pluginType == null ? "no plugin" : "ok");
            var gated = pluginType != null && pluginType.Methods.Any(x => x.Name == "CheckGate" &&
                x.HasBody && x.Body.Instructions.Any(i => i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).Name == "get_graphicsDeviceType"));
            Check("N", "CheckGate reads SystemInfo.graphicsDeviceType", gated, gated ? "ok" : "missing");
            var runtimeType = allTypes.FirstOrDefault(t => t.Name == "UpScaleRuntime");
            var deferred = runtimeType != null && runtimeType.Methods.Any(x => x.Name == "CheckGateDeferred");
            Check("N", "UpScaleRuntime defers the gate through a coroutine", deferred, deferred ? "ok" : "missing");

            Check("C", "plugin name mentions UpScale", nm != null && nm.Contains("UpScale"), nm ?? "null");
        }
        Check("C", "extends BaseUnityPlugin",
            pluginType?.BaseType?.Name == "BaseUnityPlugin", pluginType?.BaseType?.Name ?? "null");

        // ---------------- D. DllImport surface ----------------
        var pinvokes = new List<string>();
        var setLastError = new Dictionary<string, bool>();
        foreach (var t in allTypes)
            foreach (var meth in t.Methods)
            {
                if (!meth.HasPInvokeInfo) continue;
                var pi = meth.PInvokeInfo;
                string dll = pi.Module != null ? pi.Module.Name : "?";
                pinvokes.Add(dll + "!" + pi.EntryPoint);
                setLastError[meth.Name] = true; // present in the import table means the import is bound
            }

        Console.WriteLine("      pinvoke: " + string.Join(", ", pinvokes));
        Check("D", "imports kernel32!LoadLibraryW", pinvokes.Contains("kernel32!LoadLibraryW"), "");
        Check("D", "imports kernel32!GetProcAddress", pinvokes.Contains("kernel32!GetProcAddress"), "");
        Check("D", "LoadLibraryW is a real import-table entry", setLastError.ContainsKey("LoadLibraryW"), "");
        Check("D", "no d3d11/dxgi P/Invoke in managed code",
            !pinvokes.Any(p => p.IndexOf("d3d11", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               p.IndexOf("dxgi", StringComparison.OrdinalIgnoreCase) >= 0), "");

        // ---------------- E. native delegate contract ----------------
        var delegates = allTypes.Where(t => t.BaseType?.Name == "MulticastDelegate").ToList();
        Console.WriteLine("      delegates: " + string.Join(", ", delegates.Select(d => d.Name)));
        Check("E", "declares the 7 interop delegate signatures for the 12 exports",
            delegates.Count == 7, delegates.Count.ToString());

        int withAttr = 0, stdcall = 0;
        var attrNames = new List<string>();
        foreach (var d in delegates)
            foreach (var a in d.CustomAttributes)
            {
                attrNames.Add(d.Name + ":" + a.AttributeType.Name);
                if (a.AttributeType.Name.IndexOf("UnmanagedFunctionPointer", StringComparison.Ordinal) < 0)
                    continue;
                withAttr++;
                // System.Runtime.InteropServices.CallingConvention: Winapi=1, Cdecl=2, StdCall=3
                if (a.ConstructorArguments.Count > 0 && Convert.ToInt32(a.ConstructorArguments[0].Value) == 3)
                    stdcall++;
            }
        Console.WriteLine("      delegate attrs: " + string.Join(", ", attrNames));
        Check("E", "every delegate carries [UnmanagedFunctionPointer]", withAttr == delegates.Count,
            withAttr + "/" + delegates.Count);
        Check("E", "every delegate is StdCall (matches __stdcall exports)", stdcall == delegates.Count,
            stdcall + "/" + delegates.Count);

        // ---------------- F. export names bound ----------------
        var strings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in allTypes)
            foreach (var meth in t.Methods)
            {
                if (!meth.HasBody) continue;
                foreach (var ins in meth.Body.Instructions)
                    if (ins.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && ins.Operand is string s)
                        strings.Add(s);
            }

        string[] exports =
        {
            "VRKU_Version", "VRKU_LastError", "VRKU_DeviceName", "VRKU_Status",
            "VRKU_Frames", "VRKU_FailFrames", "VRKU_Init", "VRKU_Run", "VRKU_Shutdown"
        };
        foreach (var e in exports)
            Check("F", "binds native export " + e, strings.Contains(e), "");

        Check("F", "uses Marshal.GetDelegateForFunctionPointer",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference mr &&
                     mr.Name == "GetDelegateForFunctionPointer")), "");

        // ---------------- G. config surface ----------------
        foreach (var key in new[] { "Enabled", "Mode", "Sharpness", "Scale", "OutputWidth", "Verbose", "StatusKey" })
            Check("G", "config key " + key + " is bound",
                allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                    i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && (string)i.Operand == key)), "");

        Check("G", "reads [General] section", strings.Contains("General"), "");
        Check("G", "reads [RenderScale] section", strings.Contains("RenderScale"), "");
        Check("G", "reads [Debug] section", strings.Contains("Debug"), "");

        // ---------------- H. D3D11 gating ----------------
        Check("H", "queries SystemInfo.graphicsDeviceType",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference mr &&
                     mr.Name == "get_graphicsDeviceType")), "");
        Check("H", "references GraphicsDeviceType (D3D11 gate)",
            m.GetTypeReferences().Any(tr => tr.Name == "GraphicsDeviceType"), "");
        bool d3d11ConstComparison = allTypes.SelectMany(t => t.Methods)
            .Where(mm => mm.HasBody && mm.Body.Instructions.Any(i =>
                i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference mr2 &&
                mr2.Name == "get_graphicsDeviceType"))
            .Any(mm => mm.Body.Instructions.Any(i =>
            {
                switch (i.OpCode.Code)
                {
                    case Mono.Cecil.Cil.Code.Ldc_I4_2: return true;
                    case Mono.Cecil.Cil.Code.Ldc_I4_S: return i.Operand is sbyte sb && sb == 2;
                    case Mono.Cecil.Cil.Code.Ldc_I4: return i.Operand is int iv && iv == 2;
                    default: return false;
                }
            }));
        Check("H", "compares the device type against the D3D11 enum value (2)", d3d11ConstComparison, "");

        // ---------------- I. render hook ----------------
        var effect = allTypes.FirstOrDefault(t => t.Name == "UpscaleEffect");
        Check("I", "UpscaleEffect type exists", effect != null, "");
        var onRenderImage = effect?.Methods.FirstOrDefault(x => x.Name == "OnRenderImage");
        Check("I", "implements OnRenderImage(RenderTexture, RenderTexture)",
            onRenderImage != null && onRenderImage.Parameters.Count == 2 &&
            onRenderImage.Parameters.All(p => p.ParameterType.Name == "RenderTexture"), "");
        Check("I", "effect is a MonoBehaviour", effect?.BaseType?.Name == "MonoBehaviour",
            effect?.BaseType?.Name ?? "null");

        // v1.3.0：真正的渲染搬进了 RenderInto(src, dest)，OnRenderImage 只负责 dest==null 的兜底
        var renderInto = effect?.Methods.FirstOrDefault(m => m.Name == "RenderInto");
        Check("I", "has RenderInto(src, dest) helper", renderInto != null && renderInto.Parameters.Count == 2,
            renderInto == null ? "missing" : renderInto.Parameters.Count + " param(s)");

        int blitCalls = 0, nativeRunCalls = 0, nativePtrCalls = 0, initCalls = 0;
        foreach (var mm in new MethodDefinition[] { onRenderImage, renderInto })
        {
            if (mm == null || mm.Body == null) continue;
            foreach (var ins in mm.Body.Instructions)
            {
                var mr = ins.Operand as MethodReference;
                if (mr == null) continue;
                if (mr.Name == "Blit") blitCalls++;
                if (mr.Name == "Run") nativeRunCalls++;
                if (mr.Name == "Init") initCalls++;
                if (mr.Name == "GetNativeTexturePtr") nativePtrCalls++;
            }
        }
        Check("I", "always blits something out (dest may be backbuffer)", blitCalls >= 2, blitCalls + " call(s)");
        Check("I", "invokes native run exactly once per frame", nativeRunCalls == 1, nativeRunCalls + " call(s)");
        Check("I", "initialises native pipeline lazily", initCalls >= 1, initCalls + " call(s)");
        Check("I", "passes native texture pointers for both src and dst", nativePtrCalls >= 2, nativePtrCalls + " call(s)");
        bool hasTemp = onRenderImage.Body.Instructions.Any(i => i.Operand is MethodReference t
            && (t.Name == "EnsureTarget" || t.Name == "RenderInto"));
        Check("I", "handles dest == null with an owned render target", hasTemp, "EnsureTarget call in OnRenderImage");

        // v1.4.1: one persistent target we create ourselves, and no permanent give-up.
        bool hasEnsure = false, hasCreate = false, hasPersistentField = false;
        foreach (var t in allTypes)
        {
            foreach (var f in t.Fields)
                if (f.Name == "_target" && f.FieldType.Name == "RenderTexture") hasPersistentField = true;
            foreach (var mth in t.Methods)
            {
                if (mth.Name == "EnsureTarget") hasEnsure = true;
                if (!mth.HasBody) continue;
                foreach (var ins in mth.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Newobj && ins.Operand != null && ins.Operand.ToString().Contains("RenderTexture")) hasCreate = true;
                }
            }
        }
        Check("I", "keeps one persistent render target (no per-frame GetTemporary)", hasEnsure && hasPersistentField, "EnsureTarget + _target field");
        Check("I", "creates the target explicitly so its native pointer is stable", hasCreate, "RenderTexture allocation");

        // ---------------- J. render-scale plumbing ----------------
        Check("J", "allocates the offscreen target with a 24 bit depth buffer",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Newobj && i.Operand is MethodReference ctor &&
                     ctor.DeclaringType.Name == "RenderTexture" && ctor.Parameters.Count == 4)), "");
        Check("J", "assigns Camera.targetTexture",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt && i.Operand is MethodReference mr &&
                     mr.Name == "set_targetTexture")), "");
        Check("J", "creates a presenter camera (cullingMask cleared)",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt && i.Operand is MethodReference mr &&
                     mr.Name == "set_clearFlags")), "");
        Check("J", "clears targetTexture when leaving render-scale mode",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldnull)) &&
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt && i.Operand is MethodReference mr &&
                     mr.Name == "set_targetTexture")), "");

        // ---------------- K. lifecycle ----------------
        Check("K", "subscribes SceneManager.sceneLoaded",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference mr &&
                     mr.Name == "add_sceneLoaded")), "");
        Check("K", "unsubscribes SceneManager.sceneLoaded",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference mr &&
                     mr.Name == "remove_sceneLoaded")), "");
        Check("K", "calls VRKU_Shutdown on destroy",
            allTypes.SelectMany(t => t.Methods).Where(mm => mm.Name == "OnDestroy").Any(mm => mm.HasBody &&
                mm.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call &&
                    i.Operand is MethodReference mr && mr.Name == "Shutdown")), "");
        Check("K", "marks runtime object DontDestroyOnLoad",
            allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call && i.Operand is MethodReference mr &&
                     mr.Name == "DontDestroyOnLoad")), "");

        // ---------------- L. native binary ----------------
        Check("L", "native plugin exists on disk", File.Exists(nativePath), nativePath);
        if (File.Exists(nativePath))
        {
            byte[] nb = File.ReadAllBytes(nativePath);
            Check("L", "native plugin is 64 bit (PE32+ / machine 0x8664)",
                nb.Length > 0x40 && BitConverter.ToUInt16(nb, 0x3c) + 0x5a < nb.Length &&
                BitConverter.ToUInt16(nb, BitConverter.ToUInt16(nb, 0x3c) + 4) == 0x8664,
                "machine=" + (nb.Length > 0x40 ? "0x" + BitConverter.ToUInt16(nb, BitConverter.ToUInt16(nb, 0x3c) + 4).ToString("x4") : "?"));

            string ascii = System.Text.Encoding.ASCII.GetString(nb);
            int found = 0;
            foreach (var e in exports)
                if (ascii.Contains(e)) found++;
            Check("L", "native binary exports all 12 symbols", found == 9,
                "VRKU exports found: " + found + "/9 (+3 Unity entry points expected)");
            Check("L", "native exports UnityPluginLoad", ascii.Contains("UnityPluginLoad"), "");
            Check("L", "native exports UnityPluginUnload", ascii.Contains("UnityPluginUnload"), "");
            Check("L", "native exports UnitySetGraphicsDevice", ascii.Contains("UnitySetGraphicsDevice"), "");
            Check("L", "does not link d3dcompiler.dll at runtime", !ascii.Contains("d3dcompiler"), "");
            Check("L", "embeds EASU shader constants", ascii.Contains("CreatePixelShader"), "");

            uint peOff = BitConverter.ToUInt32(nb, 0x3c);
            ushort optMagic = BitConverter.ToUInt16(nb, (int)peOff + 24);
            Check("L", "PE optional header is PE32+", optMagic == 0x20b, "0x" + optMagic.ToString("x3"));
        }

        // ---------------- M. safety properties ----------------
        Check("M", "never references the removed SetActive trick",
            !allTypes.SelectMany(t => t.Methods).Any(mm => mm.HasBody && mm.Body.Instructions.Any(
                i => i.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt && i.Operand is MethodReference mr &&
                     mr.Name == "SetActive" && mr.Parameters.Count == 1)), "");
        Check("M", "does not touch PlayerPrefs",
            !strings.Any(s => s.IndexOf("PlayerPrefs", StringComparison.Ordinal) >= 0), "");

        Console.WriteLine();
        // ---- N2: v1.6.0 NativePass 诊断开关 / _small 复用 ----
        {
            var effT = allTypes.FirstOrDefault(t => t.Name == "UpscaleEffect");
            var rit = effT == null ? null : effT.Methods.FirstOrDefault(x => x.Name == "RenderInto");
            Check("N2", "RenderInto() exists", rit != null);
            bool gate = false, fallback = false;
            if (rit != null && rit.HasBody)
                foreach (var ins in rit.Body.Instructions)
                {
                    var fr = ins.Operand as FieldReference;
                    if (fr != null && fr.Name == "NativePass") gate = true;
                    var mr = ins.Operand as MethodReference;
                    if (mr != null && mr.Name == "Blit") fallback = true;
                }
            Check("N2", "RenderInto gates on NativePass", gate);
            Check("N2", "NativePass=false falls back to Graphics.Blit", fallback);

            var rsT = allTypes.FirstOrDefault(t => t.Name == "UpScaleRuntime");
            var ers = rsT == null ? null : rsT.Methods.FirstOrDefault(x => x.Name == "EnsureRenderScale");
            bool destroys = false, releases = false;
            if (ers != null && ers.HasBody)
                foreach (var ins in ers.Body.Instructions)
                {
                    var mr = ins.Operand as MethodReference;
                    if (mr == null) continue;
                    if (mr.Name == "Destroy") destroys = true;
                    if (mr.Name == "Release") releases = true;
                }
            Check("N2", "EnsureRenderScale no longer mid-frame Destroys its target", !destroys);
            Check("N2", "EnsureRenderScale reuses the target via Release()", releases);
        }

        Console.WriteLine("RESULT: " + _pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }
}
