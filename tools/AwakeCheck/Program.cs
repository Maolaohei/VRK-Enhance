using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Flags engine calls that are unsafe from BepInEx plugin Awake().
// In Unity 5.4.5f1 the SystemInfo/GfxDevice singletons are still null while
// BepInEx runs plugin Awake -> the icall dereferences null -> 0xC0000005.
class AwakeCheck
{
    static readonly HashSet<string> Banned = new HashSet<string>()
    {
        "UnityEngine.SystemInfo",
        "UnityEngine.VR.VRDevice",
        "UnityEngine.VRSettings",
        "UnityEngine.QualitySettings",
        "UnityEngine.Screen",
        "UnityEngine.Graphics",
        "UnityEngine.Shader",
        "UnityEngine.Material",
        "UnityEngine.Texture",
        "UnityEngine.Texture2D",
        "UnityEngine.RenderTexture",
        "UnityEngine.Camera",
        "UnityEngine.Renderer",
        "UnityEngine.Light",
        "UnityEngine.GL",
        "UnityEngine.ComputeShader",
    };

    static readonly HashSet<string> Watch = new HashSet<string>()
    {
        "UnityEngine.Application",
        "UnityEngine.ResourceRequest",
        "UnityEngine.Resources",
    };

    static int bad, warn, ok;

    static int Main(string[] args)
    {
        foreach (var f in args)
        {
            if (!File.Exists(f)) { Console.WriteLine("missing: " + f); continue; }
            ModuleDefinition m;
            try { m = ModuleDefinition.ReadModule(f); }
            catch (Exception e) { Console.WriteLine("read failed " + f + ": " + e.Message); continue; }

            string name = Path.GetFileName(f);
            var awake = AllTypes(m)
                .SelectMany(t => t.Methods.Where(x => x.Name == "Awake" || x.Name == "OnEnable"))
                .Where(x => x.HasBody)
                .ToList();

            if (awake.Count == 0)
            {
                Console.WriteLine("[{0}] no Awake/OnEnable (nothing to check)", name);
                continue;
            }

            foreach (var meth in awake)
            {
                var hits = new List<string>();
                foreach (var ins in meth.Body.Instructions)
                {
                    var mr = ins.Operand as MethodReference;
                    var fr = ins.Operand as FieldReference;
                    var tr = ins.Operand as TypeReference;
                    string decl = null;
                    if (mr != null) decl = mr.DeclaringType == null ? null : mr.DeclaringType.FullName;
                    else if (fr != null) decl = fr.DeclaringType == null ? null : fr.DeclaringType.FullName;
                    else if (tr != null) decl = tr.FullName;
                    if (decl == null) continue;

                    decl = decl.Split('<')[0].Trim();
                    if (Banned.Contains(decl))
                        hits.Add("BANNED " + meth.Name + ": " + decl + " :: " +
                                 (mr != null ? mr.Name : fr != null ? fr.Name : "(type)") +
                                 "  IL_" + ins.Offset.ToString("X4"));
                    else if (Watch.Contains(decl))
                        hits.Add("watch  " + meth.Name + ": " + decl + " :: " +
                                 (mr != null ? mr.Name : fr != null ? fr.Name : "(type)") +
                                 "  IL_" + ins.Offset.ToString("X4"));
                }

                if (hits.Count == 0)
                {
                    ok++;
                    Console.WriteLine("[{0}] {1}.{2} clean", name, meth.DeclaringType.Name, meth.Name);
                }
                else
                {
                    foreach (var h in hits)
                    {
                        if (h.StartsWith("BANNED")) bad++; else warn++;
                        Console.WriteLine("[{0}] {1}.{2} -> {3}", name, meth.DeclaringType.Name, meth.Name, h);
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("clean={0}  banned={1}  watch={2}", ok, bad, warn);
        return bad == 0 ? 0 : 1;
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types) foreach (var x in Walk(t)) yield return x;
    }

    static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes) foreach (var x in Walk(n)) yield return x;
    }
}
