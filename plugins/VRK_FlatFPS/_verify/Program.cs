using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class V
{
    static int fails = 0;
    static void Ok(string m) { Console.WriteLine("  [PASS] " + m); }
    static void Bad(string m) { fails++; Console.WriteLine("  [FAIL] " + m); }
    static void Check(bool cond, string m) { if (cond) Ok(m); else Bad(m); }

    static AssemblyDefinition game, plugin;
    static Dictionary<string, TypeDefinition> gtypes = new Dictionary<string, TypeDefinition>();

    static TypeDefinition T(string full)
    {
        TypeDefinition t;
        gtypes.TryGetValue(full, out t);
        return t;
    }
    static MethodDefinition M(string type, string name)
    {
        var t = T(type);
        return t == null ? null : t.Methods.FirstOrDefault(x => x.Name == name);
    }
    static string Elem(TypeReference t) { return t.IsByReference ? t.GetElementType().Name : t.Name; }
    static FieldDefinition F(string type, string name)
    {
        var t = T(type);
        return t == null ? null : t.Fields.FirstOrDefault(x => x.Name == name);
    }

    static int Main(string[] a)
    {
        game = AssemblyDefinition.ReadAssembly(a[0]);
        plugin = AssemblyDefinition.ReadAssembly(a[1]);
        foreach (var t in game.MainModule.Types.SelectMany(Flat)) gtypes[t.FullName] = t;

        Console.WriteLine("== A. plugin assembly identity");
        var refs = plugin.MainModule.AssemblyReferences.Select(r => r.Name + " " + r.Version).ToList();
        Console.WriteLine("  refs: " + string.Join(" | ", refs));
        Check(refs.Any(r => r.StartsWith("mscorlib 2.0")), "plugin targets mscorlib 2.0.x (.NET 3.5 runtime)");
        Check(refs.Any(r => r.StartsWith("UnityEngine ")), "references UnityEngine");
        Check(refs.Any(r => r.StartsWith("Assembly-CSharp ")), "references Assembly-CSharp");
        Check(refs.Any(r => r.StartsWith("0Harmony ")), "references 0Harmony (HarmonyX)");
        var plugType = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
        var bip = plugType == null ? null : plugType.CustomAttributes.FirstOrDefault(x => x.AttributeType.Name == "BepInPlugin");
        Check(bip != null && bip.ConstructorArguments.Count >= 3 && (string)bip.ConstructorArguments[0].Value == "vrk.flatfps",
            "BepInPlugin(Guid=\"vrk.flatfps\", ...) present");

        Console.WriteLine("== B. patch target methods");
        var checkHmd = M("InitScene2", "CheckHMD");
        Check(checkHmd != null && checkHmd.IsPrivate && !checkHmd.IsStatic && checkHmd.ReturnType.Name == "Boolean",
            "InitScene2.CheckHMD is private instance bool");
        var logoStart = M("LogoScene", "Start");
        Check(logoStart != null && logoStart.IsPrivate && logoStart.ReturnType.Name == "Void",
            "LogoScene.Start is private instance void");
        var kiv = M("IllusionCamera", "KeyInput_Value");
        Check(kiv != null && kiv.IsPrivate && !kiv.IsStatic, "IllusionCamera.KeyInput_Value is private instance");
        if (kiv != null)
        {
            var ps = string.Join(", ", kiv.Parameters.Select(p => (p.ParameterType.IsByReference ? "ref " : "") + Elem(p.ParameterType) + " " + p.Name));
            Check(kiv.Parameters.Count == 4
                  && kiv.Parameters[0].ParameterType.IsByReference && Elem(kiv.Parameters[0].ParameterType) == "Single"
                  && kiv.Parameters[1].ParameterType.Name == "Single"
                  && kiv.Parameters[2].ParameterType.Name == "KeyCode"
                  && kiv.Parameters[3].ParameterType.Name == "KeyCode",
                "KeyInput_Value(float& val, float move, KeyCode addKey, KeyCode subKey) -> " + ps);
        }

        Console.WriteLine("== C. fields / members the plugin reflects or patches");
        var mIsFading = F("VRKanojo.Utils.VRCameraFade", "m_IsFading");
        Check(mIsFading != null && mIsFading.FieldType.Name == "Boolean", "VRCameraFade.m_IsFading : bool");
        var mFadeImage = F("VRKanojo.Utils.VRCameraFade", "m_FadeImage");
        Check(mFadeImage != null && mFadeImage.FieldType.Name == "Image", "VRCameraFade.m_FadeImage : Image");
        var fadeType = T("VRKanojo.Utils.VRCameraFade");
        Check(fadeType != null && fadeType.Properties.Any(p => p.Name == "IsFading" && p.GetMethod != null), "VRCameraFade.IsFading getter");
        Check(fadeType != null && fadeType.Properties.Any(p => p.Name == "M_FadeImage" && p.GetMethod != null), "VRCameraFade.M_FadeImage getter");
        var lf = F("LogoScene", "VRCameraFade_");
        Check(lf != null && lf.FieldType.Name == "VRCameraFade", "LogoScene.VRCameraFade_ field");
        var lc = F("LogoScene", "Canvas_");
        Check(lc != null && lc.FieldType.Name == "Canvas", "LogoScene.Canvas_ field");
        var rot = F("IllusionCamera", "rotate");
        Check(rot != null && rot.FieldType.Name == "Vector3", "IllusionCamera.rotate field");
        var rotM = M("IllusionCamera", "Rotate");
        Check(rotM != null && rotM.IsPrivate, "IllusionCamera.Rotate is private (bound via CreateDelegate)");
        var scFade = F("Manager.Scene", "VRCameraFade_");
        Check(scFade != null && scFade.IsPublic, "Manager.Scene.VRCameraFade_ is public field");
        var scType = T("Manager.Scene");
        Check(scType != null && scType.Properties.Any(p => p.Name == "MainCamera_"), "Manager.Scene.MainCamera_ property");
        MethodDefinition isInst = null;
        for (var bt = scType == null ? null : scType.BaseType; bt != null && isInst == null; bt = bt.Resolve() == null ? null : bt.Resolve().BaseType)
            isInst = bt.Resolve().Methods.FirstOrDefault(m => m.Name == "IsInstance");
        if (isInst == null && scType != null) isInst = scType.Methods.FirstOrDefault(m => m.Name == "IsInstance");
        Check(isInst != null && isInst.IsStatic && isInst.IsPublic, "Singleton<Scene>.IsInstance() static public (inherited)");
        var fm = F("VRKanojo.GlobalVRKanojo", "ForceMouse_");
        Check(fm != null && fm.IsStatic, "GlobalVRKanojo.ForceMouse_ static");
        var ovr = T("OVRManager");
        Check(ovr != null && ovr.Properties.Any(p => p.Name == "instance" && p.GetMethod != null && p.GetMethod.IsStatic), "OVRManager.instance static getter");
        var ce = T("VRKanojo.ConfigEffector");
        Check(ce != null && ce.Methods.Any(m => m.Name == "EffectAllDisable"), "ConfigEffector.EffectAllDisable()");

        Console.WriteLine("== D. KeyInput call sites and WASD remap simulation");
        var keyInput = M("IllusionCamera", "KeyInput");
        if (keyInput != null && keyInput.HasBody)
        {
            var il = keyInput.Body.Instructions;
            var sites = new List<int[]>();
            for (int i = 0; i < il.Count; i++)
            {
                var call = il[i];
                var mr = call.Operand as MethodReference;
                if (call.OpCode != OpCodes.Call || mr == null || mr.Name != "KeyInput_Value") continue;
                int add = -1, sub = -1;
                int j = i - 1;
                if (j >= 0 && IsLdc(il[j], out sub)) { }
                j--;
                if (j >= 0 && IsLdc(il[j], out add)) { }
                sites.Add(new[] { add, sub });
            }
            foreach (var s in sites)
                Console.WriteLine("  call site: add=" + s[0] + " (" + NameOf(s[0]) + ") sub=" + s[1] + " (" + NameOf(s[1]) + ")");
            Check(sites.Any(s => s[0] == 275 && s[1] == 276), "KeyInput calls KeyInput_Value(275 RightArrow, 276 LeftArrow) for X axis");
            Check(sites.Any(s => s[0] == 273 && s[1] == 274), "KeyInput calls KeyInput_Value(273 UpArrow, 274 DownArrow) for Z axis");
            foreach (var s in sites)
                Console.WriteLine("  after prefix: add=" + Remap(s[0]) + " (" + NameOf(Remap(s[0])) + ") sub=" + Remap(s[1]) + " (" + NameOf(Remap(s[1])) + ")");
            Check(sites.Any(s => Remap(s[0]) == 100 && Remap(s[1]) == 97), "remap produces D(100)/A(97) for the X-axis site");
            Check(sites.Any(s => Remap(s[0]) == 119 && Remap(s[1]) == 115), "remap produces W(119)/S(115) for the Z-axis site");
            int untouched = sites.Count(s => Remap(s[0]) == s[0] && Remap(s[1]) == s[1]);
            Check(untouched == 5,
                "3 direct call sites remapped at the call (X/Z movement, FOV-down); yaw/pitch go through "
                + "local KeyCode vars, so they are remapped at their ldc.i4 assignment; " + untouched + " stay original");
        }

        Console.WriteLine("== E. patch method signatures Harmony must bind");
        var pfxHmd = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "PatchCheckHmd");
        var hmdPrefix = pfxHmd == null ? null : pfxHmd.Methods.FirstOrDefault(m => m.Name == "Prefix");
        Check(hmdPrefix != null && hmdPrefix.IsStatic && hmdPrefix.ReturnType.Name == "Boolean"
              && hmdPrefix.Parameters.Count == 1 && hmdPrefix.Parameters[0].Name == "__result" && hmdPrefix.Parameters[0].ParameterType.IsByReference,
            "PatchCheckHmd.Prefix(ref bool __result) : bool");

        var pfxLogo = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "PatchLogoStart");
        var lm = pfxLogo == null ? null : pfxLogo.Methods.FirstOrDefault(m => m.Name == "Prefix");
        Check(lm != null && lm.IsStatic && lm.ReturnType.Name == "Boolean" && lm.Parameters.Count == 1 && lm.Parameters[0].ParameterType.Name == "LogoScene",
            "PatchLogoStart.Prefix(LogoScene __instance) : bool");
        var fin = pfxLogo == null ? null : pfxLogo.Methods.FirstOrDefault(m => m.Name == "Finalizer");
        Check(fin != null && fin.IsStatic && fin.ReturnType.Name == "Exception" && fin.Parameters.Count == 1
              && fin.Parameters[0].Name == "__exception" && fin.Parameters[0].ParameterType.Name == "Exception",
            "PatchLogoStart.Finalizer(Exception __exception) : Exception");

        var pfxKeysT = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "PatchKeyInput");
        Check(pfxKeysT != null, "PatchKeyInput class exists");
        var transpiler = pfxKeysT == null ? null : pfxKeysT.Methods.FirstOrDefault(m => m.Name == "Transpiler");
        Check(transpiler != null && transpiler.IsStatic
              && transpiler.ReturnType.Name == "IEnumerable`1"
              && transpiler.Parameters.Count == 1
              && transpiler.Parameters[0].ParameterType.Name == "IEnumerable`1",
            "PatchKeyInput.Transpiler(IEnumerable<CodeInstruction>) : IEnumerable<CodeInstruction>");

        // Map 字典: 12 条重映射, 值必须与 Remap() 一致
        var mapField = pfxKeysT == null ? null : pfxKeysT.Fields.FirstOrDefault(f => f.Name == "Map");
        Check(mapField != null && mapField.FieldType.Name == "Dictionary`2",
            "PatchKeyInput.Map is Dictionary<int,int> (KeyCode 常量表)");
        var mapCctor = pfxKeysT == null ? null : pfxKeysT.Methods.FirstOrDefault(m => m.Name == ".cctor");
        Check(mapCctor != null, "PatchKeyInput has a static .cctor (Map initializer)");
        var mapIl = mapCctor == null ? new List<Instruction>() : mapCctor.Body.Instructions.ToList();
        int mapPairs = 0;
        foreach (var ins in mapIl)
        {
            int v;
            if (!ConstOf(ins, out v)) continue;
            if (KeyMap.ContainsKey(v)) mapPairs++;
        }
        Console.WriteLine("  ldc.i4 operands matching KeyMap in .cctor: " + mapPairs);
        Check(mapPairs >= 12, "Map 内嵌全部 13 组键值: " + mapPairs + " >= 12");
        bool mapOk = true;
        foreach (var kv in KeyMap)
        {
            bool hasKey = false, hasVal = false;
            foreach (var ins in mapIl)
            {
                int v;
                if (!ConstOf(ins, out v)) continue;
                if (v == kv.Key) hasKey = true;
                if (v == kv.Value) hasVal = true;
            }
            if (!hasKey || !hasVal) { mapOk = false; Console.WriteLine("  missing pair: " + kv.Key + " -> " + kv.Value); }
        }
        Check(mapOk, "Map 覆盖: 275/276/273/274/262/260/264/258/261/270/269/61/47 -> 100/97/119/115/54/52/56/50/53/61/45/91/114");

        // 只改 ldc.i4 的 operand, 不改指令条数 -> 分支目标(labels)无需搬移
        bool emitsLdc = false, hasTry = false;
        if (transpiler != null)
        {
            hasTry = transpiler.Body.ExceptionHandlers.Count > 0;
            foreach (var ins in transpiler.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldc_I4_S || ins.OpCode == OpCodes.Ldc_I4)
                    if (ins.Operand is sbyte || ins.Operand is int) emitsLdc = true;
        }
        Check(emitsLdc, "transpiler 会写出 Ldc_I4/Ldc_I4_S 新常量");
        Check(!hasTry, "transpiler 无异常处理块(不破坏原 CFG)");

        // 目标方法是 IllusionCamera.KeyInput
        var kpatch = pfxKeysT == null ? null : pfxKeysT.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch");
        string kt = null, km = null;
        if (kpatch != null && kpatch.ConstructorArguments.Count == 2)
        {
            object a0 = kpatch.ConstructorArguments[0].Value;
            var tr = a0 as TypeReference;
            kt = tr != null ? tr.Name : (a0 as string);
            km = kpatch.ConstructorArguments[1].Value as string;
        }
        Check(kt == "IllusionCamera" && km == "KeyInput", "PatchKeyInput targets IllusionCamera.KeyInput (" + kt + "." + km + ")");

        Console.WriteLine("== G. HarmonyPatch attribute targets resolve to real game methods");
        foreach (var cls in plugin.MainModule.Types.SelectMany(Flat).Where(t => t.CustomAttributes.Any(x => x.AttributeType.Name == "HarmonyPatch")))
        {
            var attr = cls.CustomAttributes.First(x => x.AttributeType.Name == "HarmonyPatch");
            if (attr.ConstructorArguments.Count < 2)
            {
                bool multiTarget = cls.Methods.Any(m => m.Name == "TargetMethods");
                if (!multiTarget)
                    Bad(cls.Name + ": HarmonyPatch without (Type, method) arguments");
                else
                    Check(true, cls.Name + " uses TargetMethods (multi-target patch)");
                continue;
            }
            var tr = attr.ConstructorArguments[0].Value as TypeReference;
            var mn = attr.ConstructorArguments[1].Value as string;
            if (tr == null || mn == null) { Bad(cls.Name + ": cannot decode HarmonyPatch arguments"); continue; }
            var target = gtypes.ContainsKey(tr.FullName) ? gtypes[tr.FullName] : null;
            var tm = target == null ? null : target.Methods.FirstOrDefault(m => m.Name == mn);
            Check(tm != null, cls.Name + " -> [" + tr.FullName + "::" + mn + "] resolves" + (tm != null ? "" : " (TARGET MISSING, Harmony would throw)"));
        }

        Console.WriteLine("== H. why a killed coroutine means a permanent white screen");
        var writers = new List<string>();
        foreach (var t in game.MainModule.Types.SelectMany(Flat))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var i in m.Body.Instructions)
                {
                    var fr = i.Operand as FieldReference;
                    if (i.OpCode == OpCodes.Stfld && fr != null && fr.Name == "m_IsFading")
                        writers.Add(t.Name + "::" + m.Name);
                }
            }
        writers = writers.Distinct().OrderBy(x => x).ToList();
        Console.WriteLine("  writers of m_IsFading: " + string.Join(", ", writers));
        Check(writers.Count == 2 && writers.All(w => w.Contains("BeginFade")),
            "only the 2 fade coroutines ever write m_IsFading -> an interrupted coroutine leaves it stuck true forever");

        // BeginFade is a C# iterator: the real body lives in the compiler-generated MoveNext
        var iterType = game.MainModule.Types.SelectMany(Flat)
            .FirstOrDefault(t => t.Name.StartsWith("<BeginFade>"));
        Check(iterType != null, "found compiler-generated iterator for BeginFade (" + (iterType == null ? "none" : iterType.Name) + ")");
        if (iterType != null)
        {
            var moveNext = iterType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            Check(moveNext != null && moveNext.HasBody, "BeginFade iterator has an IL body to inspect");
            if (moveNext != null && moveNext.HasBody)
            {
                var ins = moveNext.Body.Instructions;
                bool setTrue = ins.Any(i => i.OpCode == OpCodes.Stfld
                    && (i.Operand as FieldReference)?.Name == "m_IsFading"
                    && i.Previous != null && i.Previous.OpCode == OpCodes.Ldc_I4_1);
                bool setFalse = ins.Any(i => i.OpCode == OpCodes.Stfld
                    && (i.Operand as FieldReference)?.Name == "m_IsFading"
                    && i.Previous != null && i.Previous.OpCode == OpCodes.Ldc_I4_0);
                int colorWrites = ins.Count(i => i.Operand is MethodReference mm && mm.Name == "set_color");
                Check(setTrue, "BeginFade sets m_IsFading = true before the yield loop");
                Check(setFalse, "BeginFade sets m_IsFading = false only after the loop completes");
                Check(colorWrites >= 2, "BeginFade animates the curtain alpha itself (Image.set_color x" + colorWrites + ")");
            }
        }

        var logoUpdate = M("LogoScene", "Update");
        if (logoUpdate != null && logoUpdate.HasBody)
        {
            bool readsIsFading = logoUpdate.Body.Instructions.Any(i => i.Operand is MethodReference mm && mm.Name == "get_IsFading");
            Check(readsIsFading, "LogoScene.Update waits on VRCameraFade.IsFading -> stuck flag blocks Title load forever");
        }

        var logCall = logoStart;
        if (logCall != null && logCall.HasBody)
        {
            var ins = logCall.Body.Instructions;
            int idx = ins.ToList().FindIndex(i => i.Operand is MethodReference mm && mm.Name == "get_instance"
                                                  && mm.DeclaringType.Name == "OVRManager");
            bool deref = idx >= 0 && idx + 1 < ins.Count && ins[idx + 1].Operand is MethodReference n2 && n2.Name == "get_trackingOriginType";
            Check(idx >= 0 && deref, "LogoScene.Start dereferences OVRManager.instance.trackingOriginType with no null check (NRE risk in flat mode)");
        }

        Console.WriteLine("== I. 操作指南面板 (F7)");
        var help = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "HelpOverlay");
        Check(help != null, "HelpOverlay type present");
        if (help != null)
        {
            Check(help.Methods.Any(m => m.Name == "OnGUI"), "HelpOverlay.OnGUI (IMGUI 叠层绘制)");
            Check(help.Methods.Any(m => m.Name == "Update"), "HelpOverlay.Update (热键轮询)");
            var guiCalls = new List<string>();
            foreach (var m in help.Methods.Where(m => m.HasBody))
                foreach (var i in m.Body.Instructions)
                {
                    var mr = i.Operand as MethodReference;
                    if (mr != null && mr.DeclaringType.Name == "GUI") guiCalls.Add(m.Name + ":" + mr.Name);
                }
            Console.WriteLine("  GUI.* calls: " + string.Join(", ", guiCalls.Distinct()));
            Check(guiCalls.Contains("OnGUI:Box") && guiCalls.Contains("OnGUI:Label"), "panel draws with GUI.Box + GUI.Label");
            Check(!guiCalls.Any(c => c.Contains("Button") || c.Contains("Window") || c.Contains("TextField") || c.Contains("ScrollView")),
                "no interactive GUI control -> overlay never swallows clicks/keys");
            bool dynFont = help.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Any(i => i.Operand is MethodReference mr2 && mr2.Name == "CreateDynamicFontFromOSFont");
            Check(dynFont, "uses Font.CreateDynamicFontFromOSFont (CJK glyphs; built-in Arial has none)");
            bool parsesKey = help.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Any(i => i.Operand is MethodReference mr3 && mr3.Name == "Parse" && mr3.DeclaringType.Name == "Enum");
            Check(parsesKey, "hotkey parsed from config via Enum.Parse(KeyCode)");
        }
        // 键位占用检查: 游戏本体只在 OVR 样例类里用 F2/F8/F11, 不使用 F7
        var usedKeys = new List<string>();
        foreach (var t in game.MainModule.Types.SelectMany(Flat))
            foreach (var m in t.Methods.Where(m => m.HasBody))
                foreach (var i in m.Body.Instructions)
                    if (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int kv && kv >= 282 && kv <= 293)
                        usedKeys.Add(t.Name + ":" + ((KeyNameF)kv));
        Console.WriteLine("  F-keys used by game: " + (usedKeys.Count == 0 ? "(none)" : string.Join(", ", usedKeys.Distinct())));
        Check(!usedKeys.Any(k => k.EndsWith("F7")), "F7 is free in the game assembly (no conflict)");

        Console.WriteLine("== J. 按住 Alt 释放指针 (v1.2.0)");
        var rt = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsRuntime");
        Check(rt != null, "FlatFpsRuntime type present");
        if (rt != null)
        {
            var readHold = rt.Methods.FirstOrDefault(m => m.Name == "ReadHoldKeys");
            Check(readHold != null && readHold.IsPrivate, "FlatFpsRuntime.ReadHoldKeys() present");
            var freeField = rt.Fields.FirstOrDefault(f => f.Name == "_cursorFree");
            Check(freeField != null && freeField.FieldType.Name == "Boolean", "field _cursorFree : bool");
            Check(rt.Fields.All(f => f.Name != "_cursorLocked"), "old sticky toggle field _cursorLocked removed");

            var upd = rt.Methods.FirstOrDefault(m => m.Name == "Update");
            var free = rt.Methods.FirstOrDefault(m => m.Name == "DoFreeLook");
            if (upd != null && upd.HasBody && free != null)
            {
                var u = upd.Body.Instructions.ToList();
                int iHold = u.FindIndex(i => i.Operand is MethodReference mr && mr.Name == "ReadHoldKeys");
                int iFree = u.FindIndex(i => i.Operand is MethodReference mr && mr.Name == "DoFreeLook");
                Console.WriteLine("  Update order: ReadHoldKeys@IL_" + (iHold >= 0 ? u[iHold].Offset.ToString("x4") : "n/a")
                                  + " -> DoFreeLook@IL_" + (iFree >= 0 ? u[iFree].Offset.ToString("x4") : "n/a"));
                Check(iHold >= 0 && iFree > iHold, "hold state is evaluated BEFORE free-look each frame (no one-frame camera jerk)");
                bool guardsOnFree = u.Any(i => i.Operand is FieldReference fr2 && fr2.Name == "_cursorFree");
                Check(guardsOnFree, "Update skips DoFreeLook while the pointer is released");
            }

            var dc = rt.Methods.FirstOrDefault(m => m.Name == "DoCursor");
            if (dc != null && dc.HasBody)
            {
                bool usesFree = dc.Body.Instructions.Any(i => i.Operand is FieldReference fr3 && fr3.Name == "_cursorFree");
                bool usesKeyDown = dc.Body.Instructions.Any(i => i.Operand is MethodReference mr4 && mr4.Name == "GetKeyDown");
                Check(usesFree && !usesKeyDown, "DoCursor is hold-based (GetKey) not toggle-based (GetKeyDown)");
            }
        }

        var strs = plugin.MainModule.Types.SelectMany(Flat)
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldstr)
            .Select(i => (string)i.Operand).ToList();
        Check(strs.Any(x => x == "LeftAlt,RightAlt"), "CursorHoldKey default = \"LeftAlt,RightAlt\"");
        // v1.11.0: F10 不再是“粘性锁指针开关”，改成“按需输出渲染清单”的调试键
        Check(strs.Any(x => x == "F10"), "DumpKey default = F10 (按需 dump)");
        Check(strs.Any(x => x == "HideGazeRing"), "HideGazeRing 配置存在");
        Check(strs.Any(x => x == "OVRGazePointer"), "隐藏凝视光环用 OVRGazePointer 反射");
        Check(plugin.MainModule.Types.SelectMany(Flat).Any(t => t.Name == "FlatFpsRuntime" && t.Methods.Any(m => m.Name == "HandleDumpKey")), "HandleDumpKey() 存在");
        Check(plugin.MainModule.Types.SelectMany(Flat).Any(t => t.Name == "FlatFpsRuntime" && t.Methods.Any(m => m.Name == "TickHideGazeRing")), "TickHideGazeRing() 存在");
        Check(strs.Any(x => x.Contains("按住 Alt")), "help panel documents 按住 Alt 释放指针");

        // 游戏本体是否读取 Fire2(左Alt)/Fire3 —— 决定裸按 Alt 会不会触发游戏动作
        var fireStrs = new List<string>();
        foreach (var t in game.MainModule.Types.SelectMany(Flat))
            foreach (var m in t.Methods.Where(m => m.HasBody))
                foreach (var i in m.Body.Instructions)
                    if (i.OpCode == OpCodes.Ldstr && i.Operand is string sv && (sv == "Fire2" || sv == "Fire3"))
                        fireStrs.Add(t.Name + ":" + sv);
        Console.WriteLine("  game reads Fire2/Fire3: " + (fireStrs.Count == 0 ? "(none)" : string.Join(", ", fireStrs.Distinct())));
        Check(fireStrs.Count == 0, "game never polls Fire2/Fire3 -> bare LeftAlt triggers nothing in-game");

        Console.WriteLine("== K. 进入游戏后自动显示 5 秒 (v1.3.0)");
        var helpT = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "HelpOverlay");
        if (helpT != null)
        {
            Check(helpT.Methods.Any(m => m.Name == "HandleAutoShow"), "HelpOverlay.HandleAutoShow() present");
            Check(helpT.Methods.Any(m => m.Name == "HandleHotkey"), "HelpOverlay.HandleHotkey() present");
            Check(helpT.Fields.Any(f => f.Name == "_armed" && f.FieldType.Name == "Boolean"), "field _armed : bool (只在进入游戏时自动弹一次)");
            Check(helpT.Fields.Any(f => f.Name == "_autoHideAt" && f.FieldType.Name == "Single"), "field _autoHideAt : float (倒计时)");
            Check(helpT.Fields.Any(f => f.Name == "LoaderScenes" && f.FieldType.Name == "HashSet`1"), "LoaderScenes HashSet<string> 过滤加载场景");

            var hs = helpT.Methods.First(m => m.Name == "HandleAutoShow");
            var il = hs.Body.Instructions;
            bool readsRealtime = il.Any(i => i.Operand is MethodReference r && r.Name == "get_realtimeSinceStartup");
            bool readsCfg = il.Any(i => i.Operand is FieldReference f && f.Name == "HelpAutoHideSeconds");
            Check(readsRealtime && readsCfg, "auto-hide deadline = realtimeSinceStartup + HelpAutoHideSeconds");
            Check(hs.Body.Variables.Any(v => v.VariableType.Name == "HashSet`1") || il.Any(i => i.Operand is MethodReference r2 && r2.Name == "Contains"),
                "loader scenes are excluded via HashSet.Contains");

            var updT = helpT.Methods.First(m => m.Name == "Update");
            var uil = updT.Body.Instructions;
            Check(uil.Any(i => i.Operand is MethodReference r3 && r3.Name == "HandleAutoShow"), "Update() drives HandleAutoShow");
            Check(uil.Any(i => i.Operand is FieldReference f2 && f2.Name == "_autoHideAt"), "Update() enforces the hide deadline");

            // 默认值: ShowOnStart=true, AutoHideSeconds=5
            var awake = plugin.MainModule.Types.SelectMany(Flat).First(t => t.Name == "FlatFpsPlugin").Methods.First(m => m.Name == "Awake");
            var all = awake.Body.Instructions.ToList();
            bool five = all.Any(i => (i.OpCode == OpCodes.Ldc_R8 && (double)i.Operand == 5.0)
                                    || (i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 5f));
            bool one = all.Any(i => i.OpCode == OpCodes.Ldc_I4_1);
            Check(five, "AutoHideSeconds default = 5.0");
            Check(one, "ShowOnStart default = true (ldc.i4.1)");

            // 场景名单
            var scenStrs = helpT.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
            var need = new[] { "Init", "Init2", "CheckHMD", "SetHMD", "Logo2", "Logo", "Attention" };
            Console.WriteLine("  loader scenes: " + string.Join(",", need.Where(n => scenStrs.Contains(n))));
            Check(need.All(n => scenStrs.Contains(n)), "all 7 loader scenes are filtered out");

            // 面板文案
            var lineStrs = helpT.Fields.First(f => f.Name == "Lines").CustomAttributes.Count >= 0
                ? helpT.Methods.Where(m => m.Name.EndsWith(".cctor")).SelectMany(m => m.Body.Instructions)
                      .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList()
                : new List<string>();
            Console.WriteLine("  panel rows: " + lineStrs.Count);
            Check(lineStrs.Any(x => x.Contains("按住 Alt")), "panel row: 按住 Alt 释放鼠标指针");
            Check(!lineStrs.Any(x => x.Contains("F10")), "panel row: F10 已移除");
            var body = helpT.Methods.First(m => m.Name == "BuildBody");
            bool dynHotkey = body.Body.Instructions.Any(i => i.Operand is FieldReference f3 && f3.Name == "HelpHotkey");
            Check(dynHotkey, "BuildBody appends the hotkey line from config (not hard-coded F7)");
        }

        Console.WriteLine("== L. 白屏取证 + 多实例看门狗 (v1.4.0)");
        var ver4 = bip != null && bip.ConstructorArguments.Count >= 3 ? (string)bip.ConstructorArguments[2].Value : "";
        Check(ver4 == "1.41.0", "BepInPlugin version = 1.41.0");

        // ---- v1.23.0 P0/P1/P2 ----
        {
            var allV = new List<TypeDefinition>();
            foreach (var t in plugin.MainModule.Types) { allV.Add(t); foreach (var n2 in t.NestedTypes) allV.Add(n2); }
            var plugV = allV.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            Check(plugV != null && plugV.Fields.Any(f => f.Name == "InteractKey"), "InteractKey config entry exists");
            Check(plugV != null && plugV.Fields.Any(f => f.Name == "LookDistance"), "LookDistance config entry exists");
            Check(plugV != null && plugV.Fields.Any(f => f.Name == "HoverReticle"), "HoverReticle config entry exists");

            var cdec = allV.FirstOrDefault(t => t.Name == "PatchCheckDecision");
            Check(cdec != null, "PatchCheckDecision class exists");
            var cdecM = cdec != null ? cdec.Methods.FirstOrDefault(m => m.Name == "Postfix") : null;
            var cdecC = cdecM != null ? CallsOf(cdecM) : new List<string>();
            Check(cdecM != null && cdecM.Parameters.Any(p => p.Name == "__result" && p.ParameterType is ByReferenceType && ((ByReferenceType)p.ParameterType).ElementType.Name == "Boolean"), "PatchCheckDecision Postfix(ref bool __result)");
            Check(cdecC.Any(c => c.Contains("FlatFpsPlugin::InteractHeld")), "decision postfix delegates to InteractHeld");
            Check(cdecM != null && !cdecC.Any(c => c.EndsWith("::Parse")), "decision postfix no longer parses the key every frame");
            Check(plugV != null && plugV.Fields.Any(f => f.Name == "GazeGrabDistance"), "GazeGrabDistance config entry exists");

            var lstart = allV.FirstOrDefault(t => t.Name == "PatchLookingStart");
            Check(lstart != null, "PatchLookingStart class exists");
            var lstartM = lstart != null ? lstart.Methods.FirstOrDefault(m => m.Name == "Postfix") : null;
            var lstartC = lstartM != null ? CallsOf(lstartM) : new List<string>();
            Check(lstartM != null && lstartC.Any(c => c.EndsWith("::Field") && c.Contains("AccessTools")), "looking-distance postfix locates raycastDis via AccessTools.Field");
            Check(lstartC.Any(c => c.EndsWith("::SetValue")), "looking-distance postfix writes the field");
            Check(lstartC.Any(c => c.EndsWith("::Clamp")), "looking distance is clamped");

            var rtV = allV.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            Check(rtV != null && rtV.Fields.Any(f => f.Name == "HoverInteractable"), "HoverInteractable static flag exists");
            Check(rtV != null && rtV.Fields.Any(f => f.Name == "HoverName"), "HoverName static flag exists");
            var kg2 = allV.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "KeepGazeRingVisible");
            var kg2C = kg2 != null ? CallsOf(kg2) : new List<string>();
            Check(kg2C.Any(c => c.EndsWith("::GetComponentInParent")), "hover flag comes from HitCollision on the hit collider");

            var ovV = allV.FirstOrDefault(t => t.Name == "HelpOverlay");
            var hov = ovV != null ? ovV.Methods.FirstOrDefault(m => m.Name == "DrawHoverReticle") : null;
            var hovC = hov != null ? CallsOf(hov) : new List<string>();
            Check(hov != null, "HelpOverlay.DrawHoverReticle exists");
            Check(hovC.Count(c => c.EndsWith("::DrawTexture")) == 8, "hover reticle draws 8 corner strokes");
            Check(hovC.Any(c => c.EndsWith("::CreateDynamicFontFromOSFont")), "hover hint uses an OS CJK font");
            Check(hovC.Any(c => c.EndsWith("::Label")), "hover hint prints the interact key");
            var ongui = ovV != null ? ovV.Methods.FirstOrDefault(m => m.Name == "OnGUI") : null;
            Check(ongui != null && CallsOf(ongui).Any(c => c.EndsWith("::DrawHoverReticle")), "OnGUI draws the hover reticle");
        }

        // ---- v1.22.0 光环吸附命中点 ----
        {
            var allR = new List<TypeDefinition>();
            foreach (var t in plugin.MainModule.Types) { allR.Add(t); foreach (var n2 in t.NestedTypes) allR.Add(n2); }
            var plugR = allR.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            Check(plugR != null && plugR.Fields.Any(f => f.Name == "GazeRingMode"), "GazeRingMode config entry exists");
            Check(plugR != null && plugR.Fields.Any(f => f.Name == "GazeRingMaxDistance"), "GazeRingMaxDistance config entry exists");
            Check(plugR != null && plugR.Fields.Any(f => f.Name == "GazeRingLog"), "GazeRingLog config entry exists");
            var kg = allR.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "KeepGazeRingVisible");
            var kgC = kg != null ? CallsOf(kg) : new List<string>();
            Check(kg != null && kgC.Any(c => c.EndsWith("::RaycastNonAlloc")), "gaze ring aims with Physics.RaycastNonAlloc");
            Check(kgC.Any(c => c.EndsWith("::get_point")), "gaze ring snaps to raycast hit point");
            Check(kgC.Any(c => c.EndsWith("::IsChildOf")), "gaze ring ignores the camera rig's own colliders");
            Check(kgC.Any(c => c.EndsWith("::get_forward")) && kgC.Any(c => c.EndsWith("::get_position")), "gaze ring ray is camera-relative");
            Check(kg != null && FieldsOf(kg).Any(f => f.EndsWith("::GazeRingMode")), "Aim/Center mode is read");
        }

        // ---- v1.21.0 隔离 EventSystem 桥 + 交互日志 ----
        {
            var plugI = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            Check(plugI != null && plugI.Fields.Any(f => f.Name == "LegacyUiBridge"), "LegacyUiBridge config entry exists");
            Check(plugI != null && plugI.Fields.Any(f => f.Name == "InteractionLog"), "InteractionLog config entry exists");
            var allI = new List<TypeDefinition>();
            foreach (var t in plugin.MainModule.Types) { allI.Add(t); foreach (var n2 in t.NestedTypes) allI.Add(n2); }
            var efu = allI.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "EnsureFlatUi");
            Check(efu != null && FieldsOf(efu).Any(f => f.EndsWith("::LegacyUiBridge")), "EnsureFlatUi is gated by LegacyUiBridge");
            var dpc = allI.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "DoPhysicsClick");
            Check(dpc != null && FieldsOf(dpc).Any(f => f.EndsWith("::InteractionLog")), "DoPhysicsClick logs interactions via InteractionLog");
            Check(dpc != null && CallsOf(dpc).Any(c => c.EndsWith("::IsPointerOverGameObject")), "interaction log includes IsPointerOverGameObject");
            Check(dpc != null && CallsOf(dpc).Any(c => c.EndsWith("::get_lockState")), "interaction log includes cursor lock state");
        }

        // ---- v1.20.0 凝视光环常显 ----
        {
            var plugG = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            Check(plugG != null && plugG.Fields.Any(f => f.Name == "AlwaysShowGazeRing"), "AlwaysShowGazeRing config entry exists");
            Check(plugG != null && plugG.Fields.Any(f => f.Name == "GazeRingDistance"), "GazeRingDistance config entry exists");
            var allT = new List<TypeDefinition>();
            foreach (var t in plugin.MainModule.Types) { allT.Add(t); foreach (var n2 in t.NestedTypes) allT.Add(n2); }
            var gp = allT.FirstOrDefault(t => t.Name == "PatchGazePointerUpdate");
            Check(gp != null, "PatchGazePointerUpdate exists");
            bool gpTargetsGaze = false;
            if (gp != null)
                foreach (var ca in gp.CustomAttributes)
                    if (ca.ConstructorArguments.Count >= 2 && ca.ConstructorArguments[1].Value as string == "Update")
                        gpTargetsGaze = true;
            Check(gpTargetsGaze, "gaze patch targets OVRGazePointer.Update");
            var gpfx = gp == null ? null : gp.Methods.FirstOrDefault(m => m.Name == "Prefix");
            Check(gpfx != null && FieldsOf(gpfx).Any(f => f.EndsWith("::AlwaysShowGazeRing")), "gaze patch honours AlwaysShowGazeRing");
            var keep = allT.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "KeepGazeRingVisible");
            Check(keep != null, "KeepGazeRingVisible exists");
            Check(keep != null && CallsOf(keep).Any(c => c.EndsWith("::Show")), "pinned ring is shown through OVRGazePointer.Show");
            Check(keep != null && CallsOf(keep).Any(c => c.EndsWith("::set_position")), "pinned ring is repositioned every frame");
            Check(keep != null && CallsOf(keep).Any(c => c.EndsWith("::set_localScale")), "pinned ring keeps a constant angular size");
            Check(keep != null && FieldsOf(keep).Any(f => f.EndsWith("::GazeRingDistance")), "pinned ring reads GazeRingDistance");
            var lateG = allT.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "LateUpdate" && CallsOf(m).Any(c => c.EndsWith("::KeepGazeRingVisible")));
            Check(lateG != null, "LateUpdate pins the gaze ring");
        }

        // ---- v1.19.0 中心准星 ----
        {
            var xsPlug = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var helpX = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "HelpOverlay");
            bool hasX = xsPlug != null && xsPlug.Fields.Any(f => f.Name == "Crosshair");
            Check(hasX, "Crosshair config entry exists");
            Check(xsPlug != null && xsPlug.Fields.Any(f => f.Name == "CrosshairSize"), "CrosshairSize config entry exists");
            Check(xsPlug != null && xsPlug.Fields.Any(f => f.Name == "CrosshairAlpha"), "CrosshairAlpha config entry exists");
            Check(xsPlug != null && xsPlug.Fields.Any(f => f.Name == "CrosshairFirstPersonOnly"), "CrosshairFirstPersonOnly config entry exists");
            bool resOk = false;
            foreach (var r in plugin.MainModule.Resources)
                if (r.Name == "VRKanojo.FlatFPS.crosshair.bin") resOk = true;
            Check(resOk, "crosshair image is embedded as VRKanojo.FlatFPS.crosshair.bin");
            var helpTypes = new List<TypeDefinition>();
            if (helpX != null) helpTypes.Add(helpX);
            foreach (var nt in plugin.MainModule.Types) foreach (var n2 in nt.NestedTypes) helpTypes.Add(n2);
            MethodDefinition drawX = null, texX = null, onGuiX = null;
            foreach (var t in helpTypes)
                foreach (var m in t.Methods)
                {
                    if (m.Name == "DrawCrosshair") drawX = m;
                    if (m.Name == "CrosshairTexture") texX = m;
                    if (m.Name == "OnGUI") onGuiX = m;
                }
            Check(drawX != null, "DrawCrosshair method exists");
            Check(texX != null, "CrosshairTexture loads the embedded blob");
            Check(drawX != null && CallsOf(drawX).Any(c => c.EndsWith("::DrawTexture")), "crosshair is painted with GUI.DrawTexture");
            Check(drawX != null && FieldsOf(drawX).Any(f => f.EndsWith("::VrMode")), "crosshair stays off in VR mode");
            Check(drawX != null && FieldsOf(drawX).Any(f => f.EndsWith("::Crosshair")), "crosshair honours its enable switch");
            Check(onGuiX != null && CallsOf(onGuiX).Any(c => c.EndsWith("::DrawCrosshair")), "OnGUI paints the crosshair");
            Check(texX != null && CallsOf(texX).Any(c => c.EndsWith("::GetManifestResourceStream")), "texture comes from the manifest resource");
            Check(texX != null && CallsOf(texX).Any(c => c.EndsWith("::SetPixels32")), "texture is built from raw RGBA with SetPixels32");
        }
        var rt4 = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
        Check(rt4 != null, "FlatFpsRuntime present");
        var scanFades4 = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "ScanFades");        var scanCalls4 = CallsOf(scanFades4);
        Check(scanCalls4.Any(x => x.EndsWith("::FindObjectsOfTypeAll")), "ScanFades rescans every VRCameraFade each second");
        var wd4 = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "Watchdog");
        Check(CallsOf(wd4).Any(x => x.EndsWith("::WatchOne")), "Watchdog drives every fade instance (WatchOne)");
        var fadesField4 = rt4 == null ? null : rt4.Fields.FirstOrDefault(f => f.Name == "_fades");
        Check(fadesField4 != null && fadesField4.FieldType.IsArray && fadesField4.FieldType.Name == "VRCameraFade[]",
            "_fades is VRCameraFade[] (not a single Manager.Scene reference)");
        Check(rt4 != null && rt4.Methods.Any(m => m.Name == "WatchOne"), "WatchOne() per-instance watchdog body");
        var uiM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "EnsureFlatUi");
        var uiC = CallsOf(uiM == null ? null : uiM);
        Check(uiM != null, "EnsureFlatUi() exists (mouse-clickable menus in flat mode, v1.8.0)");
        Check(uiC.Any(x => x.EndsWith("::get_current")), "EnsureFlatUi looks up EventSystem.current");
        Check(uiC.Any(x => x.EndsWith("::AddComponent")), "EnsureFlatUi adds module / raycaster components");
        Check(uiC.Any(x => x.EndsWith("::set_worldCamera")), "EnsureFlatUi points Canvas.worldCamera at the camera (5.4 has no GraphicRaycaster.worldCamera)");
        Check(uiC.Any(x => x.EndsWith("::set_enabled")), "EnsureFlatUi disables foreign raycasters (OVRRaycaster)");
        Check(uiC.Any(x => x.EndsWith("::IndexOf")), "EnsureFlatUi matches OVRRaycaster by type name");
        Check(rt4 != null && rt4.Methods.Any(mm => CallsOf(mm).Any(x => x.EndsWith("::EnsureFlatUi"))),
            "some FlatFpsRuntime method drives EnsureFlatUi (Refresh)");
        Check(FieldsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoCursor"))
                .Any(x => x.EndsWith("::MenuCursorLock")), "DoCursor reads MenuCursorLock (menus stay clickable)");

        Console.WriteLine("== Q. \u6b63\u7247\u573a\u666f\u70b9\u51fb (v1.13.1)");
        var pcM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoPhysicsClick");
        Check(pcM != null, "DoPhysicsClick() exists (raycast-based click for in-game world-space UI)");
        var pcC = CallsOf(pcM);
        Check(pcC.Any(x => x.EndsWith("::Raycast")), "DoPhysicsClick raycasts from the camera");
        Check(pcC.Any(x => x.EndsWith("::ScreenPointToRay")), "DoPhysicsClick builds the ray from the mouse position");
        Check(pcC.Any(x => x.EndsWith("::GetComponentInParent")), "DoPhysicsClick walks up to the owning Button");
        Check(pcC.Any(x => x.EndsWith("::GetEventHandler")), "DoPhysicsClick falls back to IPointerClickHandler lookup");
        Check(pcC.Any(x => x.EndsWith("::InvokeButton")), "DoPhysicsClick reuses InvokeButton");

        bool hasField(string n) { var f = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            return f != null && f.Fields.Any(x => x.Name == n); }
        Check(hasField("PhysicsClick"), "FlatFpsPlugin.PhysicsClick config field exists");
        Check(hasField("KeepOvrInput"), "FlatFpsPlugin.KeepOvrInput config field exists");
        Check(hasField("ClickInGame"), "FlatFpsPlugin.ClickInGame config field exists");
        var plugT = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
        var plugCctor = plugT == null ? null : plugT.Methods.FirstOrDefault(m => m.Name == ".cctor");
        var bindStrs = plugT == null ? new List<string>() : plugT.Methods
            .Where(mm => mm.HasBody).SelectMany(mm => mm.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
        Check(bindStrs.Contains("PhysicsClick") && bindStrs.Contains("ClickInGame") && bindStrs.Contains("KeepOvrInput"),
            "all three new click config keys are Config.Bind-ed");

        var ibM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "InvokeButton");
        var ibC = CallsOf(ibM);
        Check(ibC.Any(x => x.EndsWith("::get_current")), "InvokeButton tolerates a missing EventSystem");
        Check(ibC.Any(x => x.EndsWith("::get_onClick")), "InvokeButton falls back to Button.onClick.Invoke()");

        var gazeM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "TickHideGazeRing");
        var gazeC = CallsOf(gazeM);
        Check(gazeM != null, "TickHideGazeRing() exists");
        Check(gazeC.Any(x => x.EndsWith("::GetComponentsInChildren")), "gaze ring hide enumerates child Renderers");
        Check(gazeC.Any(x => x.EndsWith("::set_enabled")), "gaze ring hide disables Renderer.enabled");
        Check(!gazeC.Any(x => x.EndsWith("::SetActive")),
            "gaze ring hide never calls GameObject.SetActive (that would kill the game's own gaze-click)");
        Check(FieldsOf(gazeM).Any(x => x.EndsWith("::HideGazeRing")), "gaze ring hide is config-gated");

        var gazeM2 = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "TickHideGazeRing");
        var gazeC2 = CallsOf(gazeM2);
        Check(gazeC2.Any(x => x.EndsWith("::GetInstanceID")), "gaze ring hide tracks the instance id (re-hides recreated rings)");
        Check(!gazeC2.Any(x => x.EndsWith("::SetActive")), "gaze ring hide still never calls SetActive");
        var vsM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "ScanViewAttached");
        var vsC = CallsOf(vsM);
        Check(vsM != null, "ScanViewAttached() exists (identifies whatever sticks to the camera)");
        Check(vsC.Any(x => x.EndsWith("::FindObjectsOfTypeAll")), "ScanViewAttached enumerates all Renderers");
        Check(vsC.Any(x => x.EndsWith("::IsChildOf")), "ScanViewAttached checks camera ancestry");
        Check(vsC.Any(x => x.EndsWith("::Distance")), "ScanViewAttached measures distance to the camera");
        bool hasF(string n2) { return rt4 != null && rt4.Fields.Any(f => f.Name == n2); }
        Check(hasF("_gazeTargetId") && hasF("_viewScanDone"), "instance-id + view-scan bookkeeping fields exist");

        Console.WriteLine("== R. 第一/第三人称切换 (v1.15.0)");
        var enM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "EnterMode");
        var enC = CallsOf(enM);
        Check(enM != null, "EnterMode() exists (one-shot setup per view mode)");
        Check(enC.Any(x => x.EndsWith("::SetDistanceAll")), "EnterMode restores the orbit distance for third person");
        Check(enC.Any(x => x.EndsWith("::set_localPosition")), "EnterMode re-centres the camera when leaving free view");

        var fpM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoFirstPerson");
        var fpC = CallsOf(fpM);
        Check(fpM != null, "DoFirstPerson() exists (true VR-style first person)");
        Check(fpC.Any(x => x.EndsWith("::set_localPosition")) && fpC.Any(x => x.EndsWith("::set_localRotation")),
            "first person keeps the camera at the eye and writes its own local pose");
        Check(fpC.Any(x => x.EndsWith("::GetAxis")), "first person reads mouse axes directly");
        Check(fpC.Any(x => x.EndsWith("::RigRoot")), "first person moves the rig (the player), not the camera");
        Check(fpC.Any(x => x.EndsWith("::ProjectOnPlane")), "first person walks along the ground plane");
        Check(FieldsOf(fpM).Any(x => x.EndsWith("::FirstPersonMoveSpeed")), "first person uses its own walk speed config");
        Check(fpC.Any(x => x.EndsWith("::Toast")), "first person can toast the live eye height");
        var eye2M = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoEyeHeight");
        var eye2C = CallsOf(eye2M);
        Check(eye2C.Any(x => x.EndsWith("::get_trackingSpace")) && eye2C.Any(x => x.EndsWith("::set_localPosition")),
            "eye height is applied through trackingSpace");
        Check(FieldsOf(eye2M).Any(x => x.EndsWith("::EyeHeight")), "eye height reads the EyeHeight config");
        Check(FieldsOf(eye2M).Any(x => x.EndsWith("::_eyeOverride")), "eye height honours the runtime override");
        var eye2Strs = eye2M == null ? new List<string>() : eye2M.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
        Check(eye2Strs.Any(x => x.Contains("rigY")), "eye height logs the rig Y it compensated for");
        bool eyeDefaultOk = false;
        foreach (var mm in plugin.MainModule.Types.Where(t => t.Name == "FlatFpsPlugin"))
            foreach (var m in mm.Methods.Where(m => m.HasBody))
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Ldc_R4 && ins.Operand is float && Math.Abs((float)ins.Operand - 1.28f) < 0.001f)
                        eyeDefaultOk = true;
                }
        Check(eyeDefaultOk, "EyeHeight default is 1.28 m (dialled in by the user)");
        var rigM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "RigRoot");
        Check(rigM != null && CallsOf(rigM).Any(x => x.EndsWith("::get_CameraRig_")),
            "RigRoot() resolves Manager.Scene.CameraRig_");
        // ---- VR 模式开关 (v1.18.0) ----
        var vrPlugT = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
        var vrModeF = vrPlugT == null ? null : vrPlugT.Fields.FirstOrDefault(f => f.Name == "VrMode");
        Check(vrModeF != null && vrModeF.IsStatic && vrModeF.IsInitOnly, "VrMode is a static readonly switch on the plugin");
        bool readsEnv = false;
        if (vrPlugT != null)
            foreach (var m in vrPlugT.Methods.Where(m => m.HasBody))
                foreach (var ins in m.Body.Instructions)
                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "GetEnvironmentVariable")
                        readsEnv = true;
        Check(readsEnv, "VrMode comes from the VRKANOJO_VR environment variable");
        var vrChkM = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "PatchCheckHmd");
        var vrChkP = vrChkM == null ? null : vrChkM.Methods.FirstOrDefault(m => m.Name == "Prefix");
        Check(vrChkP != null && FieldsOf(vrChkP).Any(x => x.EndsWith("::VrMode")),
            "CheckHMD prefix steps aside in VR mode (no forced flat)");
        var vrLuM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "LateUpdate");
        Check(vrLuM != null && FieldsOf(vrLuM).Any(x => x.EndsWith("::VrMode")),
            "LateUpdate does not touch the camera pose in VR mode");

        var helpT0 = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "HelpOverlay");
        var toastM = helpT0 == null ? null : helpT0.Methods.FirstOrDefault(mm => mm.Name == "Toast");
        var toastC = CallsOf(toastM);
        Check(toastM != null && toastM.IsStatic, "HelpOverlay.Toast() exists (static entry)");
        Check(toastC.Any(x => x.EndsWith("::get_realtimeSinceStartup")), "toast expiry uses realtime clock");
        var drawToastM = helpT0 == null ? null : helpT0.Methods.FirstOrDefault(mm => mm.Name == "DrawToast");
        var drawToastC = CallsOf(drawToastM);
        Check(drawToastM != null, "DrawToast() exists");
        Check(drawToastC.Any(x => x.EndsWith("::DrawTexture")) && drawToastC.Any(x => x.EndsWith("::Label")),
            "toast draws a background box plus the label");
        Check(drawToastC.Any(x => x.EndsWith("::get_realtimeSinceStartup")), "toast hides itself when expired");
        var onGuiM = helpT0 == null ? null : helpT0.Methods.FirstOrDefault(mm => mm.Name == "OnGUI");
        var onGuiC = CallsOf(onGuiM);
        Check(onGuiC.Any(x => x.EndsWith("::DrawToast")), "OnGUI paints the toast");
        Check(!onGuiC.Any(x => x.EndsWith("::get_Visible")), "toast is painted independently of the help panel state");
        var toastTextM = helpT0 == null ? null : helpT0.Methods.FirstOrDefault(mm => mm.Name == "ViewModeToastText");
        var ttStr = toastTextM == null ? new List<string>() : toastTextM.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
        Check(ttStr.Count(x => x.Contains("视角")) >= 3, "toast text covers all three view modes");

        var hvM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "HandleViewKey");
        var hvC = CallsOf(hvM);
        Check(hvM != null, "HandleViewKey() exists");
        Check(hvC.Any(x => x.EndsWith("::GetKeyDown")), "HandleViewKey reacts to the toggle key");
        Check(hvC.Any(x => x.EndsWith("::EnterMode")), "HandleViewKey runs the per-mode setup after switching");
        Check(hvC.Any(x => x.EndsWith("::Toast")), "HandleViewKey shows the corner toast on every switch");
        Check(FieldsOf(hvM).Any(x => x.EndsWith("::ViewToggleKey")), "HandleViewKey reads the configurable key name");
        var sdM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "SetDistanceAll");
        Check(sdM != null && CallsOf(sdM).Any(x => x.EndsWith("::FindObjectsOfTypeAll")),
            "SetDistanceAll enumerates every IllusionCamera");
        Check(hasField("ViewToggle") && hasField("ViewToggleKey") && hasField("FirstPersonDistance")
            && hasField("ThirdPersonDistance"), "all four view-toggle config keys exist");
        Check(bindStrs.Contains("ViewToggleKey") && bindStrs.Contains("FirstPersonDistance"),
            "view-toggle keys are Config.Bind-ed");
        var camPatchV = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "PatchCameraLateUpdate");
        var camPrefixV = camPatchV == null ? null : camPatchV.Methods.FirstOrDefault(m => m.Name == "Prefix");
        Check(CallsOf(camPrefixV).Any(x => x.EndsWith("::get_ViewMode")),
            "PatchCameraLateUpdate.Prefix hands the camera to us outside third person");
        var helpT2 = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "HelpOverlay");
        var rows2 = helpT2 == null ? new List<string>() : helpT2.Methods.Where(mm => mm.HasBody).SelectMany(mm => mm.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
        Check(rows2.Any(x => x.Contains("切视角") && x.Contains("自由")), "panel row: F cycles first/third/free view");

        var fvM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoFreeView");
        var fvC = CallsOf(fvM);
        Check(fvM != null, "DoFreeView() exists (third mode: fully self-driven camera)");
        Check(fvC.Any(x => x.EndsWith("::GetAxis")), "free view reads mouse axes");
        Check(fvC.Any(x => x.EndsWith("::GetKey")) || fvC.Any(x => x.EndsWith("::GetKeyDown")), "free view reads WASD/PageUp keys");
        Check(fvC.Any(x => x.EndsWith("::set_position")), "free view moves the camera itself");
        Check(fvC.Any(x => x.EndsWith("::set_rotation")), "free view sets the camera rotation itself");
        Check(hasField("FreeMoveSpeed") && hasField("FreeSprintMultiplier") && hasField("FirstPersonMoveSpeed"), "movement speed config keys exist");
        Check(plugin.MainModule.Types.FirstOrDefault(t => t.Name == "FlatFpsRuntime") != null
            && plugin.MainModule.Types.First(t => t.Name == "FlatFpsRuntime").Properties.Any(p => p.Name == "ViewMode"),
            "FlatFpsRuntime.ViewMode is exposed to the prefix");
        var camPatch3 = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "PatchCameraLateUpdate");
        var camPrefix3 = camPatch3 == null ? null : camPatch3.Methods.FirstOrDefault(m => m.Name == "Prefix");
        Check(CallsOf(camPrefix3).Any(x => x.EndsWith("::get_ViewMode")),
            "Prefix consults ViewMode to hand the camera over in free view");
        Check(rows2.Any(x => x.Contains("自由")), "panel row mentions free view");

        var rtCctor = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == ".cctor");
        bool defaultFirst = false;
        if (rtCctor != null)
        {
            var ins3 = rtCctor.Body.Instructions;
            for (int k = 1; k < ins3.Count; k++)
                if (ins3[k].OpCode == OpCodes.Stsfld && ins3[k].Operand is FieldReference fr3 && fr3.Name == "_viewMode"
                    && (ins3[k - 1].OpCode == OpCodes.Ldc_I4_0))
                    defaultFirst = true;
        }
        Check(defaultFirst, "default view mode is 0 = first person");
        Check(rows2.Any(x => x.Contains("默认") && x.Contains("第一")), "panel row marks first person as the default");

        var dclickQ = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoDirectMenuClick");
        Check(FieldsOf(dclickQ).Any(x => x.EndsWith("::ClickInGame")), "DoDirectMenuClick honours ClickInGame");
        var uiFields = FieldsOf(uiM);
        Check(uiFields.Count(x => x.EndsWith("::KeepOvrInput")) >= 2,
            "EnsureFlatUi keeps OVRInputModule/OVRRaycaster unless KeepOvrInput=false");

        Console.WriteLine("== M. 免VR 直连点击 (v1.9.0)");
        var dclick = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoDirectMenuClick");
        Check(dclick != null, "DoDirectMenuClick() exists (bypass EventSystem/raycaster chain)");
        var dcC = CallsOf(dclick);
        Check(dcC.Any(x => x.EndsWith("::GetMouseButtonDown")), "DoDirectMenuClick reacts to LMB down");
        Check(dcC.Any(x => x.EndsWith("::CollectButtons")), "DoDirectMenuClick gathers Buttons via CollectButtons");
        var cbM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "CollectButtons");
        var cbC = CallsOf(cbM);
        Check(cbM != null && cbC.Any(x => x.EndsWith("::FindObjectsOfTypeAll")),
            "CollectButtons enumerates Buttons at runtime");
        Check(cbC.Any(x => x.EndsWith("::GetComponentsInChildren")),
            "CollectButtons also walks Canvas children (FindObjectsOfTypeAll misses DontDestroyOnLoad objects on 5.4)");
        Check(dcC.Any(x => x.EndsWith("::RectangleContainsScreenPoint")), "DoDirectMenuClick hit-tests RectTransform against cursor");
        Check(dcC.Any(x => x.EndsWith("::InvokeButton")), "DoDirectMenuClick dispatches into InvokeButton");
        Check(dcC.Any(x => x.EndsWith("::get_sortingOrder")), "DoDirectMenuClick sorts candidates by canvas sortingOrder");
        Check(FieldsOf(dclick).Any(x => x.EndsWith("::DirectMenuClick")), "DoDirectMenuClick honours [Input] DirectMenuClick");
        var invM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "InvokeButton");
        var invC = CallsOf(invM);
        Check(invM != null, "InvokeButton() exists");
        Check(invC.Any(x => x.EndsWith("::Execute") || x.EndsWith("::ExecuteHierarchy")),
            "InvokeButton dispatches pointer events via ExecuteEvents");
        var invFields = FieldsOf(invM);
        // Unity 5.4 的 ExecuteEvents 把 pointerXxxHandler 暴露成属性(get_xxx), 新版才是静态字段, 两种都算命中
        Func<string, bool> hasHandler = n =>
            invFields.Any(x => x.EndsWith("::" + n)) || invC.Any(x => x.EndsWith("::get_" + n));
        Check(hasHandler("pointerDownHandler") && hasHandler("pointerUpHandler") && hasHandler("pointerClickHandler"),
            "InvokeButton fires down/up/click (Button implements IPointerClickHandler -> onClick)");
        var blockerM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "ClearRaycastBlockers");
        var blkC = CallsOf(blockerM);
        Check(blockerM != null, "ClearRaycastBlockers() exists");
        Check(blkC.Any(x => x.EndsWith("::set_raycastTarget")), "ClearRaycastBlockers clears raycastTarget on transparent full-screen graphics");
        Check(blkC.Any(x => x.EndsWith("::GetWorldCorners")), "ClearRaycastBlockers projects rect corners to screen");
        Check(blkC.Any(x => x.EndsWith("::GetComponentInParent")), "ClearRaycastBlockers never touches Button/Selectable graphics");
        Check(uiC.Any(x => x.EndsWith("::ClearRaycastBlockers")), "EnsureFlatUi runs ClearRaycastBlockers");
        var fadeAwake = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "PatchFadeAwake");
        Check(fadeAwake != null && CallsOf(fadeAwake.Methods.FirstOrDefault(m => m.Name == "Postfix"))
                .Any(x => x.EndsWith("::set_raycastTarget")), "PatchFadeAwake also clears the fade image's raycastTarget");
        var eyeM = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoEyeHeight");
        Check(eyeM != null, "DoEyeHeight() exists (no-HMD standing eye height, v1.7.0)");
        Check(CallsOf(eyeM).Any(x => x.EndsWith("::get_trackingSpace")), "DoEyeHeight reads OVRCameraRig.trackingSpace");
        Check(CallsOf(eyeM).Any(x => x.EndsWith("::set_localPosition")), "DoEyeHeight writes trackingSpace.localPosition");
        Check(CallsOf(eyeM).Any(x => x.EndsWith("::FindObjectOfType")), "DoEyeHeight finds the OVRCameraRig at runtime");
        Check(CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "LateUpdate"))
                .Any(x => x.EndsWith("::DoEyeHeight")), "LateUpdate applies the eye height every frame");
        var escCalls4 = CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "CheckEscalate"));
        Check(escCalls4.Any(x => x.EndsWith("::SetActive")), "CheckEscalate hard-disables the fader panel (SetActive)");
        Check(escCalls4.Any(x => x.EndsWith("::get_canvas")), "CheckEscalate targets Image.canvas of that instance");
        var capCalls4 = CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "CaptureFrame"));
        var capIter = rt4 == null ? null : rt4.NestedTypes.FirstOrDefault(t => t.Name.Contains("CaptureFrame"));
        if (capIter != null)
            capCalls4.AddRange(CallsOf(capIter.Methods.FirstOrDefault(m => m.Name == "MoveNext")));
        Console.WriteLine("  capture iterator: " + (capIter == null ? "<none>" : capIter.Name));
        Check(capCalls4.Any(x => x.StartsWith("WaitForEndOfFrame::")), "CaptureFrame waits for end of frame");
        Check(capCalls4.Any(x => x.EndsWith("::ReadPixels")), "CaptureFrame reads backbuffer (ReadPixels)");
        Check(capCalls4.Any(x => x.EndsWith("::EncodeToPNG")), "CaptureFrame encodes PNG");
        Check(capCalls4.Any(x => x.EndsWith("::WriteAllBytes")), "CaptureFrame writes PNG to disk");
        var dumpCalls4 = CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DumpRenderState"));
        Check(dumpCalls4.Any(x => x.EndsWith("::get_allCameras")), "DumpRenderState logs Camera.allCameras");
        Check(dumpCalls4.Any(x => x.EndsWith("::get_stereoTargetEye")), "DumpRenderState logs stereoTargetEye");
        Check(dumpCalls4.Any(x => x.EndsWith("::get_clearFlags")), "DumpRenderState logs clearFlags/backgroundColor");
        var refreshCalls4 = CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "Refresh"));
        Check(refreshCalls4.Any(x => x.EndsWith("::set_stereoTargetEye")), "Refresh pins Camera.stereoTargetEye in flat mode");
        var awCalls4 = CallsOf(plugType == null ? null : plugType.Methods.FirstOrDefault(m => m.Name == "Awake"));
        Check(awCalls4.Any(x => x == "str:SaveFrames"), "Config [Diagnostics] SaveFrames bound");
        Check(awCalls4.Any(x => x == "str:HardKillSeconds"), "Config [Fade] HardKillSeconds bound");
        Check(awCalls4.Any(x => x == "str:MaxShots"), "Config [Diagnostics] MaxShots bound");
        Check(CallsOf(wd4).Any(x => x.EndsWith("::WatchOrphanFaders")), "Watchdog also runs the orphan white-panel pass");
        var orphan = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "WatchOrphanFaders");
        var orphanCalls = CallsOf(orphan);
        Check(orphan != null, "WatchOrphanFaders() present (fader canvas without VRCameraFade)");
        Check(orphanCalls.Any(x => x.EndsWith("::set_color")), "orphan repair zeroes the panel alpha (Image.color)");
        var orphanScan = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "ScanOrphanFaders");
        Check(CallsOf(orphanScan).Any(x => x.EndsWith("::FindObjectsOfTypeAll")), "orphan scan enumerates all loaded Images");
        var awakePatch = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "PatchFadeAwake");
        Check(awakePatch != null, "PatchFadeAwake present (root-cause fix)");
        var pfAttr = awakePatch == null ? null : awakePatch.CustomAttributes.FirstOrDefault(x => x.AttributeType.Name == "HarmonyPatch");
        bool pfOk = false;
        if (pfAttr != null && pfAttr.ConstructorArguments.Count >= 2)
        {
            var t0 = pfAttr.ConstructorArguments[0].Value as TypeReference;
            var t1 = pfAttr.ConstructorArguments[1].Value as string;
            pfOk = t0 != null && t0.Name == "VRCameraFade" && t1 == "Awake";
        }
        Check(pfOk, "[HarmonyPatch(typeof(VRCameraFade), \"Awake\")] targets the right method");
        var pfPost = awakePatch == null ? null : awakePatch.Methods.FirstOrDefault(m => m.Name == "Postfix");
        var pfCalls = CallsOf(pfPost);
        Check(pfCalls.Any(x => x.EndsWith("::set_color")), "Awake postfix zeroes the opaque panel (Image.color)");
        Check(pfCalls.Any(x => x.EndsWith("::get_IsFading")), "Awake postfix logs the IsFading state for evidence");
        Check(awCalls4.Any(x => x == "str:ClearPanelOnAwake"), "Config [Fade] ClearPanelOnAwake bound");
        var camPatch = plugin.MainModule.Types.FirstOrDefault(t => t.Name == "PatchCameraLateUpdate");
        Check(camPatch != null, "PatchCameraLateUpdate present (menu-scene camera freeze)");
        var cpAttr = camPatch == null ? null : camPatch.CustomAttributes.FirstOrDefault(x => x.AttributeType.Name == "HarmonyPatch");
        bool cpOk = false;
        if (cpAttr != null && cpAttr.ConstructorArguments.Count >= 2)
        {
            var t0 = cpAttr.ConstructorArguments[0].Value as TypeReference;
            var t1 = cpAttr.ConstructorArguments[1].Value as string;
            cpOk = t0 != null && t0.Name == "IllusionCamera" && t1 == "LateUpdate";
        }
        Check(cpOk, "[HarmonyPatch(typeof(IllusionCamera), \"LateUpdate\")] targets the camera mover");
        var cpPre = camPatch == null ? null : camPatch.Methods.FirstOrDefault(m => m.Name == "Prefix");
        Check(CallsOf(cpPre).Any(x => x.EndsWith("::IsGameScene")), "LateUpdate prefix skips the mover outside game scenes");
        var rtLate = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "LateUpdate");
        var lateCalls = CallsOf(rtLate);
        Check(rtLate != null && lateCalls.Any(x => x.EndsWith("::set_localPosition")), "FlatFpsRuntime.LateUpdate resets the menu camera pose");
        Check(lateCalls.Any(x => x.EndsWith("::IsGameScene")), "pose reset only applies outside game scenes");
        var dumpCalls5 = CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DumpRenderState"));
        Check(dumpCalls5.Any(x => x.EndsWith("::Dot")), "DumpRenderState reports canvas front-facing dot (UI in/out of view)");
        Check(dumpCalls5.Any(x => x.EndsWith("::Distance")), "DumpRenderState reports canvas distance from camera");
        Check(awCalls4.Any(x => x == "str:MenuSceneCameraFix"), "Config [Camera] MenuSceneCameraFix bound");
        Check(awCalls4.Any(x => x == "str:GameScenes"), "Config [Camera] GameScenes whitelist bound");
        Check(awCalls4.Any(x => x == "str:FrameGapSeconds"), "Config [Diagnostics] FrameGapSeconds bound");
        var updCalls = CallsOf(rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "Update"));
        Check(updCalls.Any(x => x.EndsWith("::get_unscaledDeltaTime")), "Update measures unscaled frame time for the long-frame report");

        
        // ---- 组 AA (v1.35.0): 手部震动除零(左键交互的拦路虎) ----
        {
            var allAA = plugin.MainModule.Types.SelectMany(Flat).ToList();
            MethodDefinition hvPre = null, hvFin = null, hsFin = null;
            foreach (TypeDefinition td in allAA)
            {
                foreach (MethodDefinition mm in td.Methods)
                {
                    if (td.Name == "PatchHapticsVibration" && mm.Name == "Prefix") hvPre = mm;
                    if (td.Name == "PatchHapticsVibration" && mm.Name == "Finalizer") hvFin = mm;
                    if (td.Name == "PatchHandTriggerStay" && mm.Name == "Finalizer") hsFin = mm;
                }
            }
            Check(hvPre != null, "AA1 haptics Prefix exists");
            Check(hvFin != null, "AA2 haptics Finalizer exists");
            Check(hsFin != null, "AA3 Hand.OnTriggerStay Finalizer exists");
            if (hvPre != null)
            {
                var hvStr = StrOf(hvPre);
                var hvFld = FieldsOf(hvPre);
                var hvCall = CallsOf(hvPre);
                Check(hvPre.Parameters.Count == 1 && hvPre.Parameters[0].ParameterType.Name == "Byte[]",
                      "AA4 haptics Prefix takes byte[] clip");
                Check(hvFld.Any(x => x.EndsWith("::SampleSizeInBytes")) || hvCall.Any(x => x.Contains("get_SampleSizeInBytes")),
                      "AA5 haptics Prefix guards on OVRHaptics.Config.SampleSizeInBytes");
                Check(hvStr.Any(x => x.Contains("haptics") || x.Contains("SampleSizeInBytes")),
                      "AA6 haptics Prefix reports once");
                Check(hvPre.Body.Instructions.Any(x => x.OpCode == OpCodes.Ldc_I4_0 || x.OpCode == OpCodes.Ldc_I4_1),
                      "AA7 haptics Prefix returns a bool constant");
            }
            // 日志降噪: physics click 只能在 ClickDebug 下写
            var dpc = rt4 == null ? null : rt4.Methods.FirstOrDefault(m => m.Name == "DoPhysicsClick");
            var dpc2 = plugin.MainModule.Types.SelectMany(Flat).SelectMany(t => t.Methods)
                .FirstOrDefault(m => m.Name == "DoPhysicsClick");
            var dpcUse = dpc2 == null ? null : dpc2;
            var dpcFld = dpcUse == null ? new List<string>() : FieldsOf(dpcUse);
            Check(dpcUse != null && (dpcFld.Any(x => x.EndsWith("::ClickDebug")) || CallsOf(dpcUse).Any(x => x.Contains("ClickDebug"))),
                  "AA8 physics-click logging gated by ClickDebug");
        }


        // ---- 组 AB (v1.36.0): 左键兜底驱动游戏触摸反应 ----
        {
            var allAB = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var tip = allAB.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "TryInteractiveProc");
            Check(tip != null, "AB1 TryInteractiveProc exists");
            if (tip != null)
            {
                var c = CallsOf(tip);
                Check(c.Any(x => x.Contains("InteractiveProc")), "AB2 calls InteractiveCommandController.InteractiveProc");
                Check(c.Any(x => x.Contains("IsInstance")), "AB3 guards with Singleton.IsInstance");
            }
            var lfh = allAB.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "LogFirstHit");
            Check(lfh != null, "AB4 LogFirstHit exists");
            var dpc = allAB.SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "DoPhysicsClick");
            var dpcCalls = dpc == null ? new List<string>() : CallsOf(dpc);
            Check(dpcCalls.Any(x => x.Contains("TryInteractiveProc")), "AB5 physics click falls back to InteractiveProc");
            Check(dpcCalls.Any(x => x.Contains("LogFirstHit")), "AB6 physics click reports first hit");
            var dpcFlds = dpc == null ? new List<string>() : FieldsOf(dpc);
            Check(dpcFlds.Any(x => x.EndsWith("::ClickProc")), "AB7 fallback gated by ClickProc");
        }
Console.WriteLine();
        // ---- 组 O: v1.24.0 交互键真正接到触摸/爱抚输入 ----
        {
            var allV = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var pluginType = allV.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var rtT0 = allV.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            var cacheT = allV.FirstOrDefault(t => t.Name == "InteractKeyCache");
            Check(cacheT != null, "InteractKeyCache 类型存在");
            var cacheGet = cacheT != null ? cacheT.Methods.FirstOrDefault(m => m.Name == "Get") : null;
            Check(cacheGet != null, "InteractKeyCache.Get() 存在");
            var cacheCalls = cacheGet != null ? CallsOf(cacheGet) : new List<string>();
            Check(cacheCalls.Any(c => c.EndsWith("::Parse")), "缓存内部才做 Enum.Parse");
            Check(cacheGet != null && FieldsOf(cacheGet).Any(f => f.EndsWith("::InteractKey")), "缓存读 InteractKey 配置");

            var iheldM = pluginType.Methods.FirstOrDefault(m => m.Name == "InteractHeld");
            Check(iheldM != null, "InteractHeld() 存在");
            var iheldC = iheldM != null ? CallsOf(iheldM) : new List<string>();
            Check(iheldC.Any(c => c.Contains("InteractKeyCache::Get")), "InteractHeld 用缓存解析");
            Check(iheldC.Any(c => c.EndsWith("::GetKey")), "InteractHeld 轮询 Input.GetKey");

            var touchT = allV.FirstOrDefault(t => t.Name == "PatchTouchStart");
            Check(touchT != null, "PatchTouchStart 存在");
            var tm = touchT != null ? touchT.Methods.FirstOrDefault(m => m.Name == "TargetMethods") : null;
            Check(tm != null, "PatchTouchStart.TargetMethods 存在");
            var tmStrs = new List<string>();
            if (tm != null)
            {
                tmStrs.AddRange(tm.Body.Instructions.Where(i => i.OpCode.Code == Code.Ldstr).Select(i => i.Operand as string));
                foreach (var nt in touchT.NestedTypes)
                    foreach (var mm in nt.Methods)
                        if (mm.HasBody)
                            tmStrs.AddRange(mm.Body.Instructions.Where(i => i.OpCode.Code == Code.Ldstr).Select(i => i.Operand as string));
            }
            Check(tmStrs.Contains("H_ActStart") && tmStrs.Contains("GazeTouchIn_L"), "目标 = VK_Input.H_ActStart + GazeTouchIn_L");
            var touchPost = touchT != null ? touchT.Methods.FirstOrDefault(m => m.Name == "Postfix") : null;
            Check(touchPost != null && touchPost.Parameters.Any(p => p.Name == "__result" && p.ParameterType is ByReferenceType), "PatchTouchStart.Postfix(ref bool __result)");
            Check(touchPost != null && CallsOf(touchPost).Any(c => c.Contains("FlatFpsPlugin::InteractHeld")), "触摸 Postfix 用交互键");

            var grabT = allV.FirstOrDefault(t => t.Name == "PatchGazeGrabDistance");
            Check(grabT != null, "PatchGazeGrabDistance 存在");
            var tr = grabT != null ? grabT.Methods.FirstOrDefault(m => m.Name == "Transpiler") : null;
            Check(tr != null, "PatchGazeGrabDistance.Transpiler 存在");
            var trStrs = tr != null ? tr.Body.Instructions.Where(i => i.OpCode.Code == Code.Ldstr).Select(i => i.Operand as string).ToList() : new List<string>();
            Check(trStrs.Contains("GazeGrabRuntime"), "Transpiler 引用 GazeGrabRuntime 字段名");
            Check(tr != null && tr.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldsfld), "Transpiler 生成 ldsfld");
            Check(tr != null && tr.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldc_R4), "Transpiler 识别 ldc.r4 常量");

            var grabField = pluginType.Fields.FirstOrDefault(f => f.Name == "GazeGrabRuntime");
            Check(grabField != null && grabField.IsPublic && grabField.IsStatic && grabField.FieldType.Name == "Single", "GazeGrabRuntime 是 public static float");
            var syncM = pluginType.Methods.FirstOrDefault(m => m.Name == "SyncGazeGrab");
            Check(syncM != null, "SyncGazeGrab 存在");
            Check(syncM != null && FieldsOf(syncM).Any(f => f.EndsWith("::GazeGrabDistance")), "SyncGazeGrab 读 GazeGrabDistance 配置");
            var rtUpd = rtT0 != null ? rtT0.Methods.FirstOrDefault(m => m.Name == "Update" && m.HasBody) : null;
            Check(rtUpd != null && CallsOf(rtUpd).Any(c => c.Contains("FlatFpsPlugin::SyncGazeGrab")), "运行时 Update 每帧同步抓取距离");
        }

        // ---- 组 P: v1.25.0 光环跟随视角(没命中就隐藏) ----
        {
            var allP = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var pt = allP.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var rtP = allP.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            Check(pt != null && pt.Fields.Any(f => f.Name == "GazeRingHideWhenNoHit"), "GazeRingHideWhenNoHit 配置字段存在");
            Check(rtP != null && rtP.Fields.Any(f => f.Name == "_gazeLastHit"), "_gazeLastHit 字段存在(命中变化才打日志)");
            var kg = rtP != null ? rtP.Methods.FirstOrDefault(m => m.Name == "KeepGazeRingVisible") : null;
            Check(kg != null, "KeepGazeRingVisible 存在");
            var kgC = kg != null ? CallsOf(kg) : new List<string>();
            Check(kgC.Any(c => c.EndsWith("::set_enabled")), "没命中时会把 Renderer 关掉(不再挂假准星)");
            Check(kgC.Any(c => c.EndsWith("::RaycastNonAlloc")), "仍然每帧从相机打射线(跟随视角)");
            var kgFields = kg != null ? FieldsOf(kg) : new List<string>();
            Check(kgFields.Any(f => f.EndsWith("::GazeRingHideWhenNoHit")), "读 GazeRingHideWhenNoHit 开关");
            Check(kgFields.Any(f => f.EndsWith("::GazeRingMaxDistance")), "读 GazeRingMaxDistance");
            var lu = rtP != null ? rtP.Methods.FirstOrDefault(m => m.Name == "LateUpdate") : null;
            var luC = lu != null ? CallsOf(lu) : new List<string>();
            int iFp = luC.FindIndex(c => c.Contains("DoFirstPerson"));
            int iRing = luC.FindIndex(c => c.Contains("KeepGazeRingVisible"));
            Check(iFp >= 0 && iRing > iFp, "光环定位排在视角旋转之后(不再落后一帧): fp=" + iFp + " ring=" + iRing);
            var maxD = pt != null ? pt.Methods.FirstOrDefault(m => m.Name == "get_GazeRingMaxDistance") : null;
            bool literal8 = kg != null && kg.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldc_R4 && Convert.ToSingle(i.Operand) == 8f);
            Check(literal8, "代码里的兜底最大距离是 8 米");
        }

        // ---- 组 Q: v1.26.0 凝视触摸链开关 ----
        {
            var allQ = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var pt = allQ.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var rtQ = allQ.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            Check(pt != null && pt.Fields.Any(f => f.Name == "ForceNonController"), "ForceNonController 配置字段存在");
            Check(pt != null && pt.Fields.Any(f => f.Name == "ForceEnableGaze"), "ForceEnableGaze 配置字段存在");
            var ab = rtQ != null ? rtQ.Methods.FirstOrDefault(m => m.Name == "ApplyTouchBridge") : null;
            Check(ab != null, "ApplyTouchBridge 存在");
            var abC = ab != null ? CallsOf(ab) : new List<string>();
            var abF = ab != null ? FieldsOf(ab) : new List<string>();
            Check(StrOf(ab).Any(x => x == "controlType"), "ApplyTouchBridge reflects controlType");
            Check(StrOf(ab).Any(x => x == "enableGaze"), "ApplyTouchBridge reflects enableGaze");
            Check(abC.Any(c => c.Contains("FindObjectsOfTypeAll")), "ApplyTouchBridge 会枚举 BodyTouchManager");
            Check(abC.Any(c => c.EndsWith("::SetValue")), "ApplyTouchBridge 用反射写字段");
            var ip = rtQ != null ? rtQ.Methods.FirstOrDefault(m => m.Name == "HandleInteractProbe") : null;
            Check(ip != null, "HandleInteractProbe 存在(按 E 打诊断)");
            var upd = rtQ != null ? rtQ.Methods.FirstOrDefault(m => m.Name == "Update") : null;
            var updC = upd != null ? CallsOf(upd) : new List<string>();
            Check(updC.Any(c => c.Contains("HandleInteractProbe")), "Update 每帧调用 HandleInteractProbe");
            var rf = rtQ != null ? rtQ.Methods.FirstOrDefault(m => m.Name == "DoSceneDiagnostics") : null;
            var rfC = rf != null ? CallsOf(rf) : new List<string>();
            Check(rfC.Any(c => c.Contains("ApplyTouchBridge")), "场景切换时调用 ApplyTouchBridge");
        }

        // ---- 组 R: v1.27.0 第一人称 yaw 转到 rig ----
        {
            var allR = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var ptR = allR.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var rtR = allR.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            Check(ptR != null && ptR.Fields.Any(f => f.Name == "FirstPersonRotateRig"), "FirstPersonRotateRig 配置字段存在");
            Check(rtR != null && rtR.Fields.Any(f => f.Name == "_rigYawLogged"), "yaw-rig 日志只打一次的标记存在");
            var fpR = rtR != null ? rtR.Methods.FirstOrDefault(mm => mm.Name == "DoFirstPerson") : null;
            var fpF = fpR != null ? FieldsOf(fpR) : new List<string>();
            var fpRC = fpR != null ? CallsOf(fpR) : new List<string>();
            Check(fpF.Any(x => x.EndsWith("::FirstPersonRotateRig")), "DoFirstPerson 读 FirstPersonRotateRig");
            Check(fpRC.Count(x => x.EndsWith("::Rotate")) == 0 && fpRC.Count(x => x.EndsWith("::set_localRotation")) >= 2,
                "DoFirstPerson 分别写 rig 与相机的 localRotation(共 2 处以上)");
            Check(fpRC.Count(x => x.EndsWith("::Euler")) >= 2, "DoFirstPerson 用 Quaternion.Euler 两次(yaw 给 rig / pitch 给相机)");
            Check(fpF.Any(x => x.EndsWith("::_fpYaw")), "DoFirstPerson 使用累计 _fpYaw");
            var emR = rtR != null ? rtR.Methods.FirstOrDefault(mm => mm.Name == "EnterMode") : null;
            Check(emR != null && FieldsOf(emR).Any(x => x.EndsWith("::FirstPersonRotateRig")), "EnterMode 也读 FirstPersonRotateRig(进入时不跳方向)");
        }

        // ---- 组 S: v1.27.1 三个 BUG 的回归守卫 ----
        {
            var allS = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var pfx = allS.FirstOrDefault(t => t.Name == "PatchCameraLateUpdate");
            var pre = pfx != null ? pfx.Methods.FirstOrDefault(mm => mm.Name == "Prefix") : null;
            var preF = pre != null ? FieldsOf(pre) : new List<string>();
            Check(preF.Any(x => x.EndsWith("::VrMode")), "BUGFIX: VR 模式下 Prefix 直接放行(VrMode 先判)");
            var rtS = allS.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            var abS = rtS != null ? rtS.Methods.FirstOrDefault(mm => mm.Name == "ApplyTouchBridge") : null;
            Check(abS != null && FieldsOf(abS).Any(x => x.EndsWith("::VrMode")), "BUGFIX: VR 模式下不改 controlType/enableGaze");
            var gh = rtS != null ? rtS.Methods.FirstOrDefault(mm => mm.Name == "TickHideGazeRing") : null;
            Check(StrOf(gh).Any(x => x == "VRKanojo.OVRGazePointer"), "BUGFIX: 凝视光环类型名带命名空间(以前恒 null)");
        }

        // ---- 组 T: v1.28.0 VR 入口下自建 UI / 自建逻辑全部静音 ----
        {
            var allT = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var rtT = allT.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            var hoT = allT.FirstOrDefault(t => t.Name == "HelpOverlay");
            foreach (var pair in new[] {
                new { Owner = rtT, Name = "KeepGazeRingVisible" },
                new { Owner = hoT, Name = "OnGUI" },
                new { Owner = hoT, Name = "HandleHotkey" } })
            {
                var mm = pair.Owner != null ? pair.Owner.Methods.FirstOrDefault(x => x.Name == pair.Name) : null;
                Check(mm != null && FieldsOf(mm).Any(f => f.EndsWith("::VrMode")),
                    "VR 静音: " + pair.Name + " 先判 VrMode");
            }
            Check(StrOf(hoT != null ? hoT.Methods.FirstOrDefault(x => x.Name == "Awake") : null) != null, "HelpOverlay.Awake 仍在(VR 下只弹一句提示)");
        }

        // ---- 组 U: v1.28.1 VR 下一个 Harmony 补丁都不打 ----
        {
            var allU = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var ptU = allU.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var awU = ptU != null ? ptU.Methods.FirstOrDefault(mm => mm.Name == "Awake") : null;
            var awF = awU != null ? FieldsOf(awU) : new List<string>();
            var awC = awU != null ? CallsOf(awU) : new List<string>();
            Check(awF.Any(x => x.EndsWith("::VrMode")), "Awake 读 VrMode");
            Check(awC.Any(x => x.Contains("PatchAll")), "Awake 仍有 PatchAll(VR 之外才走)");
            var il = new List<Instruction>();
            if (awU != null && awU.HasBody) il.AddRange(awU.Body.Instructions);
            int iVr = il.FindIndex(x => x.OpCode == OpCodes.Ldsfld && x.Operand != null && x.Operand.ToString().Contains("VrMode"));
            int iPa = il.FindIndex(x => x.Operand != null && x.Operand.ToString().Contains("PatchAll"));
            Check(iVr >= 0 && iPa > iVr, "VrMode 判断在 PatchAll 之前(VR 直接跳过打补丁)");
        }

        // ---- 组 V: v1.29.0 超分降分辨率后鼠标坐标换算 ----
        {
            var allV2 = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var rtV = allV2.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            var mousePixel = rtV == null ? null : rtV.Methods.FirstOrDefault(mm => mm.Name == "MousePixel");
            Check(mousePixel != null, "[V] MousePixel() 存在");
            var mpCalls = mousePixel == null ? new List<string>() : CallsOf(mousePixel);
            var mpFlds = mousePixel == null ? new List<string>() : FieldsOf(mousePixel);
            Check(mpCalls.Any(c => c.Contains("get_mousePosition")), "[V] MousePixel 读 Input.mousePosition");
            Check(mpCalls.Any(c => c.Contains("get_targetTexture")) && mpCalls.Any(c => c.Contains("Screen")),
                "[V] MousePixel 按 targetTexture/Screen 换算");

            int mpRefs = 0;
            foreach (var t in allV2)
                foreach (var mm in t.Methods)
                {
                    var cs = mm.HasBody ? CallsOf(mm) : new List<string>();
                    mpRefs += cs.Count(c => c.EndsWith("::MousePixel"));
                }
            Check(mpRefs >= 3, "[V] 点击路径调用 MousePixel (" + mpRefs + " 处)");

            int rawMouse = 0;
            foreach (var name in new[] { "DoDirectMenuClick", "DoPhysicsClick", "InvokeButton" })
            {
                var mm = rtV == null ? null : rtV.Methods.FirstOrDefault(x => x.Name == name);
                if (mm == null || !mm.HasBody) continue;
                rawMouse += CallsOf(mm).Count(c => c.Contains("get_mousePosition"));
            }
            Check(rawMouse == 0, "[V] 点击路径不再直接读 Input.mousePosition (" + rawMouse + " 处)");
        }

        // ---- 组 W: v1.30.0 鼠标当手 + 输入法隔离 ----
        {
            var allW = plugin.MainModule.Types.SelectMany(Flat).ToList();
            var rtW = allW.FirstOrDefault(t => t.Name == "FlatFpsRuntime");
            var plugW = allW.FirstOrDefault(t => t.Name == "FlatFpsPlugin");
            var awakeW = plugW == null ? null : plugW.Methods.FirstOrDefault(x => x.Name == "Awake");
            var awakeStr = StrOf(awakeW);
            Check(awakeStr.Any(x => x == "MouseHandGrab") && awakeStr.Any(x => x == "DisableIme"),
                "[W] Awake 里 Bind 了 MouseHandGrab / DisableIme");
            var fieldNamesW = plugW == null ? new List<string>()
                : plugW.Fields.Select(f => f.Name).ToList();
            Check(fieldNamesW.Contains("MouseHandGrab") && fieldNamesW.Contains("DisableIme"),
                "[W] 两个配置静态字段存在");

            var mmW = rtW == null ? null : rtW.Methods.FirstOrDefault(x => x.Name == "HandleMouseHand");
            Check(mmW != null, "[W] HandleMouseHand() 存在");
            var mmCallsW = mmW == null ? new List<string>() : CallsOf(mmW);
            var mmStrW = StrOf(mmW);
            Check(mmCallsW.Any(c => c.Contains("::Raycast")), "[W] 用 Physics.Raycast 找可抓取物");
            Check(mmCallsW.Any(c => c.EndsWith("::FindGrabbable")) &&
                  mmCallsW.Any(c => c.EndsWith("::FindHand")), "[W] 走 FindGrabbable / FindHand");
            Check(mmStrW.Any(x => x == "GrabBegin") && mmStrW.Any(x => x == "ForceRelease"),
                "[W] 反射调 GrabBegin / ForceRelease");
            Check(mmCallsW.Any(c => c.Contains("get_mouseButtonDown") || c.Contains("GetMouseButton")),
                "[W] 由左键驱动");
            Check(mmCallsW.Any(c => c.EndsWith("::SetGrabFlag")), "[W] 同步 Hand.GrabFlag");

            var sgfW = rtW == null ? null : rtW.Methods.FirstOrDefault(x => x.Name == "SetGrabFlag");
            Check(sgfW != null && StrOf(sgfW).Any(x => x == "GrabFlag"), "[W] SetGrabFlag 写私有 GrabFlag");

            var fgW = rtW == null ? null : rtW.Methods.FirstOrDefault(x => x.Name == "FindGrabbable");
            var fgStrW = StrOf(fgW);
            Check(fgStrW.Any(x => x == "Grabbable") && fgStrW.Any(x => x == "BodyCaressGrabbable"),
                "[W] FindGrabbable 认 Grabbable 且跳过爱抚类");

            var imeW = rtW == null ? null : rtW.Methods.FirstOrDefault(x => x.Name == "EnsureImeOff");
            Check(imeW != null, "[W] EnsureImeOff() 存在");
            var imeCallsW = imeW == null ? new List<string>() : CallsOf(imeW);
            Check(imeCallsW.Any(c => c.Contains("ImmAssociateContext")), "[W] 调 ImmAssociateContext(NULL)");
            Check(imeCallsW.Any(c => c.Contains("realtimeSinceStartup")), "[W] 节流 2 秒");
            var pInvokeW = rtW == null ? 0 : rtW.Methods.Count(x => x.HasPInvokeInfo);
            Check(pInvokeW >= 3, "[W] imm32/user32 的 P/Invoke 声明 (" + pInvokeW + " 个)");

            var updW = rtW == null ? null : rtW.Methods.FirstOrDefault(x => x.Name == "Update");
            var updCallsW = updW == null ? new List<string>() : CallsOf(updW);
            Check(updCallsW.Any(c => c.EndsWith("::HandleMouseHand")) &&
                  updCallsW.Any(c => c.EndsWith("::EnsureImeOff")), "[W] Update 每帧驱动两者");
        }

        // ---- group X: v1.31.0 鼠标当手(瞬移) + 输入法彻底摘除 ----
        var rtX = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsRuntime");
        Check(rtX != null, "X1 FlatFpsRuntime found");
        var handX = rtX == null ? null : rtX.Methods.FirstOrDefault(x => x.Name == "HandleMouseHand");
        Check(handX != null, "X2 HandleMouseHand exists");
        var hxCalls = CallsOf(handX);
        var hxFlds = FieldsOf(handX);
        Check(hxCalls.Contains("Transform::set_position"), "X3 teleports the real Hand (Transform.set_position)");
        Check(handX != null && handX.HasBody && handX.Body.Instructions.Any(i =>
                  (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda) &&
                  (i.Operand as FieldReference) != null &&
                  ((FieldReference)i.Operand).Name == "_handTarget"),
              "X4 reads the pending teleport target");
        Check(hxCalls.Contains("Physics::Raycast"), "X5 raycasts for the aim point");
        var rtXFields = rtX == null ? new List<string>() : rtX.Fields.Select(f => f.Name).ToList();
        Check(rtXFields.Contains("_handTarget"), "X6 _handTarget field");
        Check(rtXFields.Contains("_handHome"), "X7 _handHome field (restore on release)");
        Check(rtXFields.Contains("_handHeld"), "X8 _handHeld latch");

        var v131Plug = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsPlugin");
        var v131Flds = v131Plug == null ? new List<string>() : v131Plug.Fields.Select(f => f.Name).ToList();
        Check(v131Flds.Contains("MouseHandTeleport"), "X9 MouseHandTeleport config");
        Check(v131Flds.Contains("MouseHandReach"), "X10 MouseHandReach config");

        var imeX = rtX == null ? null : rtX.Methods.FirstOrDefault(x => x.Name == "EnsureImeOff");
        Check(imeX != null, "X11 EnsureImeOff exists");
        var imeCalls = CallsOf(imeX);
        Check(imeCalls.Contains("IntPtr::op_Equality") || imeCalls.Contains("imm32::ImmAssociateContext") ||
              imeCalls.Any(c => c.Contains("ImmAssociateContext")), "X12 detaches the IME context");
        Check(imeCalls.Any(c => c.EndsWith("ImmDisableIME")), "X13 ImmDisableIME() once per session");
        Check(imeCalls.Any(c => c.EndsWith("::set_imeCompositionMode")), "X14 forces Unity IMECompositionMode.Off");
        Check(StrOf(imeX).Any(t => t == "UnityWndClass"), "X15 finds the Unity window by class name");
        var imeLdc = imeX == null || !imeX.HasBody
            ? new List<Instruction>()
            : imeX.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldc_R4).ToList();
        Check(imeLdc.Any(i => Math.Abs((float)i.Operand - 0.25f) < 0.0001f),
              "X16 IME re-check cadence is 0.25s (was 2s)");
        Check(rtX != null && rtX.Methods.Count(m => m.HasBody && CallsOf(m).Contains("FlatFpsRuntime::EnsureImeOff")) > 0,
              "X17 EnsureImeOff driven every frame");

        // ---- group Y: v1.32.0 左键走游戏凝视交互 ----
        var rtY = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsRuntime");
        var tyY = rtY == null ? null : rtY.Methods.FirstOrDefault(x => x.Name == "TryGazeTouch");
        Check(tyY != null, "Y1 TryGazeTouch exists");
        Check(rtY != null && rtY.Methods.SelectMany(m => StrOf(m)).Any(t => t == "CheckGazeTouch"), "Y2 invokes BodyTouchManager.CheckGazeTouch");
        var rlY = rtY == null ? null : rtY.Methods.FirstOrDefault(x => x.Name == "ReleaseGazeTouch");
        Check(rtY != null && rtY.Methods.SelectMany(m => StrOf(m)).Any(t => t == "HandForceRelease"), "Y3 release via HandForceRelease");
        var icY = rtY == null ? null : rtY.Methods.FirstOrDefault(x => x.Name == "IsCaressGrabbed");
        Check(icY != null && StrOf(icY).Any(t => t == "useHand"), "Y4 reads useHand to confirm the grab");
        var rsY = rtY == null ? null : rtY.Methods.FirstOrDefault(x => x.Name == "ResolveTouch");
        Check(rsY != null && StrOf(rsY).Any(t => t == "HANDEDNESS"), "Y5 resolves HANDEDNESS enum");
        Check(rsY != null && CallsOf(rsY).Any(c => c.Contains("FindObjectsOfTypeAll")), "Y6 finds BodyTouchManager at runtime");
        var hmY = rtY == null ? null : rtY.Methods.FirstOrDefault(x => x.Name == "HandleMouseHand");
        Check(hmY != null && CallsOf(hmY).Contains("FlatFpsRuntime::TryGazeTouch"), "Y7 LMB down goes through gaze touch first");
        Check(hmY != null && CallsOf(hmY).Contains("FlatFpsRuntime::ReleaseGazeTouch"), "Y8 LMB up releases the caress");
        var plugY = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsPlugin");
        Check(plugY != null && plugY.Fields.Any(f => f.Name == "MouseBodyCaress"), "Y9 MouseBodyCaress config");

        // ---- group Z: v1.33.0 准星射线自己找部件(消除偏移) ----
        var rtZ = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsRuntime");
        var tyZ = rtZ == null ? null : rtZ.Methods.FirstOrDefault(x => x.Name == "TryGazeTouch");
        Check(tyZ != null && CallsOf(tyZ).Contains("Camera::ViewportPointToRay"), "Z1 aim ray from screen centre (ViewportPointToRay)");
        Check(tyZ != null && !CallsOf(tyZ).Any(c => c.EndsWith("::get_camTrans")), "Z2 no longer uses the game camTrans ray for the decision");
        Check(rtZ != null && rtZ.Methods.Any(m => m.Name == "_touchBodyCaress"), "Z3 bodyCaress array reader exists");
        Check(rtZ != null && rtZ.Methods.Any(m => m.Name == "GrabColliderOf"), "Z4 GrabColliderOf helper");
        Check(rtZ != null && rtZ.Methods.Any(m => m.Name == "IsGrabbed"), "Z5 IsGrabbed helper");
        Check(rtZ != null && rtZ.Methods.Any(m => m.Name == "ProbeGameCamHit"), "Z6 ProbeGameCamHit (diagnostic only)");
        Check(rtZ != null && rtZ.Methods.SelectMany(m => StrOf(m)).Any(t => t == "bodyCaress"), "Z7 reads bodyCaress by name");
        Check(rtZ != null && rtZ.Methods.SelectMany(m => StrOf(m)).Any(t => t == "GrabCollider"), "Z8 reads GrabCollider by name");
        var plugZ = plugin.MainModule.Types.SelectMany(Flat).FirstOrDefault(t => t.Name == "FlatFpsPlugin");
        Check(plugZ != null && plugZ.Fields.Any(f => f.Name == "GazeAimLog"), "Z9 GazeAimLog config");

        Console.WriteLine(fails == 0 ? "RESULT: ALL CHECKS PASSED (0 failures)" : "RESULT: " + fails + " CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    // 静态字段/委托读取编译成 ldsfld/ldsflda, 不会出现在 CallsOf 里, 单独扫一遍
    static List<string> StrOf(MethodDefinition m)
    {
        var r = new List<string>();
        if (m == null || !m.HasBody) return r;
        foreach (var ins in m.Body.Instructions)
            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string) r.Add((string)ins.Operand);
        return r;
    }

    static List<string> FieldsOf(MethodDefinition m)
    {
        var list = new List<string>();
        if (m == null || !m.HasBody) return list;
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Ldsfld || i.OpCode == OpCodes.Ldsflda)
            {
                var fr = i.Operand as FieldReference;
                if (fr != null) list.Add(fr.DeclaringType.Name + "::" + fr.Name);
            }
        }
        return list;
    }

    static List<string> CallsOf(MethodDefinition m)
    {
        var list = new List<string>();
        if (m == null || !m.HasBody) return list;
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode == OpCodes.Newobj || i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            {
                var mr = i.Operand as MethodReference;
                if (mr != null) list.Add(mr.DeclaringType.Name + "::" + mr.Name);
            }
            else if (i.OpCode == OpCodes.Ldstr)
            {
                var s = i.Operand as string;
                if (s != null) list.Add("str:" + s);
            }
        }
        return list;
    }

    static bool IsLdc(Instruction i, out int v)
    {
        v = 0;
        if (i.OpCode == OpCodes.Ldc_I4) { v = (int)i.Operand; return true; }
        if (i.OpCode == OpCodes.Ldc_I4_S) { v = (sbyte)i.Operand; return true; }
        if (i.OpCode == OpCodes.Ldc_I4_0) { v = 0; return true; }
        if (i.OpCode == OpCodes.Ldc_I4_1) { v = 1; return true; }
        return false;
    }

    static readonly Dictionary<int, int> KeyMap = new Dictionary<int, int>
    {
        { 275, 100 }, { 276, 97 }, { 273, 119 }, { 274, 115 },
        { 262, 54 }, { 260, 52 }, { 264, 56 }, { 258, 50 }, { 261, 53 },
        { 270, 61 }, { 269, 45 }, { 61, 91 }, { 47, 114 }
    };

    static bool ConstOf(Instruction ins, out int value)
    {
        value = 0;
        if (ins.OpCode == OpCodes.Ldc_I4 && ins.Operand is int) { value = (int)ins.Operand; return true; }
        if (ins.OpCode == OpCodes.Ldc_I4_S && ins.Operand is sbyte) { value = (sbyte)ins.Operand; return true; }
        if (ins.OpCode == OpCodes.Ldc_I4_0) { value = 0; return true; }
        if (ins.OpCode == OpCodes.Ldc_I4_1) { value = 1; return true; }
        if (ins.OpCode == OpCodes.Ldc_I4_2) { value = 2; return true; }
        return false;
    }

    static int Remap(int k)
    {
        int v;
        return KeyMap.TryGetValue(k, out v) ? v : k;
    }

    static string NameOf(int k)
    {
        switch (k)
        {
            case 275: return "RightArrow";
            case 276: return "LeftArrow";
            case 273: return "UpArrow";
            case 274: return "DownArrow";
            case 100: return "D";
            case 97: return "A";
            case 119: return "W";
            case 115: return "S";
            case 280: return "PageUp";
            case 281: return "PageDown";
            case 279: return "End";
            case 278: return "Home";
            case 262: return "Keypad6";
            case 260: return "Keypad4";
            case 258: return "Keypad2";
            case 264: return "Keypad8";
            case 93: return "RightBracket";
            case 61: return "Equals";
            case 46: return "Period";
            case 92: return "Backslash";
        }
        return "?";
    }

    enum KeyNameF { F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287, F7 = 288, F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293 }

    static IEnumerable<TypeDefinition> Flat(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in Flat(n)) yield return x;
    }
}
