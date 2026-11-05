using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PerfAudit
{
    // 静态"每帧托管分配"审计器。
    // 目的: 在不运行游戏的前提下, 找出 Unity 5.4 (Boehm stop-the-world GC) 里
    // 会被每帧反复调用、并且产生托管堆分配的方法 —— 这是 1% low 帧的头号元凶。
    // 用法: dotnet run -c Release -- <dll 或目录> [<dll 或目录> ...]
    internal static class Program
    {
        private static readonly HashSet<string> FrameMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update", "LateUpdate", "FixedUpdate",
            "OnGUI", "OnRenderImage", "OnPreCull", "OnPreRender", "OnPostRender",
            "OnWillRenderObject", "OnRenderObject", "OnDrawGizmos",
        };

        // 每次调用都会做引擎级查找(非分配, 但是纯 CPU 成本), 单独统计
        private static readonly Dictionary<string, int> HotCallCost = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Resources::FindObjectsOfTypeAll"] = 4000,
            ["Object::FindObjectsOfType"] = 2000,
            ["Object::FindObjectOfType"] = 2000,
            ["GameObject::Find"] = 1200,
            ["GameObject::FindWithTag"] = 800,
            ["Camera::get_main"] = 60,
            ["Component::GetComponent"] = 20,
            ["Component::GetComponents"] = 40,
            ["Component::GetComponentInChildren"] = 60,
            ["Component::GetComponentsInChildren"] = 80,
            ["Component::GetComponentInParent"] = 60,
            ["Transform::Find"] = 200,
            ["Behaviour::get_transform"] = 2,
        };

        private static int Main(string[] args)
        {
            var inputs = args.Length > 0 ? args : new[] { "." };
            var dlls = new List<string>();
            foreach (var input in inputs)
            {
                if (Directory.Exists(input))
                    dlls.AddRange(Directory.GetFiles(input, "*.dll", SearchOption.TopDirectoryOnly));
                else if (File.Exists(input))
                    dlls.Add(input);
                else
                    Console.Error.WriteLine("skip (missing): " + input);
            }

            var reports = new List<AssemblyReport>();
            foreach (var dll in dlls.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                var rep = Audit(dll);
                if (rep != null) reports.Add(rep);
            }

            Console.WriteLine();
            Console.WriteLine("================ SUMMARY (ranked by per-frame managed allocation) ================");
            Console.WriteLine(string.Format("{0,-34} {1,6} {2,12} {3,10} {4,8}", "assembly", "#meth", "alloc/frame", "hot-calls", "loop"));
            foreach (var r in reports.OrderByDescending(x => x.TotalBytes + x.TotalHotCalls))
            {
                Console.WriteLine(string.Format("{0,-34} {1,6} {2,12} {3,10} {4,8}",
                    r.Name, r.MethodCount, r.TotalBytes + " B", r.TotalHotCalls, r.LoopedMethods));
            }

            var worst = reports.OrderByDescending(x => x.TotalBytes + x.TotalHotCalls).FirstOrDefault();
            if (worst != null)
            {
                Console.WriteLine();
                Console.WriteLine("Top offenders in " + worst.Name + ":");
                foreach (var m in worst.Methods.OrderByDescending(x => x.Score).Take(12))
                    Console.WriteLine("  " + m.Display);
            }

            return 0;
        }

        private static AssemblyReport Audit(string path)
        {
            ModuleDefinition module;
            try { module = ModuleDefinition.ReadModule(path); }
            catch (Exception e)
            {
                Console.Error.WriteLine("skip (not managed): " + Path.GetFileName(path) + " -- " + e.GetType().Name);
                return null;
            }

            var rep = new AssemblyReport { Name = Path.GetFileNameWithoutExtension(path) };
            Console.WriteLine();
            Console.WriteLine("=== " + rep.Name + " ===");

            foreach (var type in AllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    if (!IsPerFrame(type, method)) continue;

                    var scan = ScanMethod(method);
                    if (scan.Bytes == 0 && scan.Hot == 0) continue;

                    rep.MethodCount++;
                    rep.TotalBytes += scan.Bytes;
                    rep.TotalHotCalls += scan.Hot;
                    if (scan.Loop) rep.LoopedMethods++;
                    rep.Methods.Add(new MethodFinding
                    {
                        Score = scan.Bytes + scan.Hot,
                        Display = string.Format("{0,-58} {1,6} B {2,6} hot {3}",
                            type.FullName + "::" + method.Name, scan.Bytes, scan.Hot, scan.Loop ? "LOOP" : "    "),
                    });
                }
            }

            foreach (var m in rep.Methods.OrderByDescending(x => x.Score))
                Console.WriteLine("   " + m.Display);

            if (rep.MethodCount == 0)
                Console.WriteLine("   (no per-frame allocation sites)");

            module.Dispose();
            return rep;
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                yield return t;
                foreach (var n in t.NestedTypes) stack.Push(n);
            }
        }

        private static bool IsPerFrame(TypeDefinition type, MethodDefinition method)
        {
            if (FrameMessages.Contains(method.Name)) return true;
            // 协程迭代器: 每帧 MoveNext 一次
            if (method.Name == "MoveNext" && type.Name.IndexOf("c__Iterator", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private struct ScanResult
        {
            public int Bytes;
            public int Hot;
            public bool Loop;
        }

        private static ScanResult ScanMethod(MethodDefinition method)
        {
            var r = new ScanResult();
            var body = method.Body;

            foreach (var ins in body.Instructions)
            {
                if (IsBackwardBranch(ins)) r.Loop = true;

                switch (ins.OpCode.Code)
                {
                    case Code.Newobj:
                        r.Bytes += 48;
                        break;
                    case Code.Newarr:
                    {
                        int n = 8;
                        var prev = ins.Previous;
                        if (prev != null && (prev.OpCode.Code == Code.Ldc_I4 || prev.OpCode.Code == Code.Ldc_I4_S))
                        {
                            try { n = Math.Max(1, Convert.ToInt32(prev.Operand)); } catch { n = 8; }
                        }
                        r.Bytes += 32 + 8 * Math.Min(n, 4096);
                        break;
                    }
                    case Code.Box:
                        r.Bytes += 24;
                        break;
                    case Code.Ldstr:
                        // 字符串字面量在 Mono 里是 interned, ldstr 不分配托管内存 —— 不计入
                        break;
                }

                var target = ins.Operand as MethodReference;
                if (target == null) continue;

                var decl = target.DeclaringType?.FullName ?? string.Empty;
                var name = target.Name;

                if (decl == "System.String" && (name == "Concat" || name == "Format" || name == "Join"))
                    r.Bytes += 96;

                if (name == "ToString" && (decl == "System.Object" || decl == "System.ValueType" || decl == "System.String"))
                    r.Bytes += 48;

                if (decl == "System.Enum" || decl == "System.Type")
                {
                    if (name.StartsWith("Get", StringComparison.Ordinal) || name == "Parse")
                        r.Bytes += 64;
                }

                if (decl.StartsWith("System.Linq.", StringComparison.Ordinal))
                    r.Bytes += 200;

                var hotKey = decl.Substring(decl.LastIndexOf('.') + 1) + "::" + name;
                if (HotCallCost.TryGetValue(hotKey, out var cost))
                    r.Hot += cost;
            }

            return r;
        }

        private static bool IsBackwardBranch(Instruction ins)
        {
            var flow = ins.OpCode.FlowControl;
            if (flow != FlowControl.Branch && flow != FlowControl.Cond_Branch) return false;
            if (ins.Operand is Instruction t) return t.Offset <= ins.Offset;
            if (ins.Operand is Instruction[] many) return many.Any(x => x.Offset <= ins.Offset);
            return false;
        }

        private sealed class MethodFinding
        {
            public int Score;
            public string Display;
        }

        private sealed class AssemblyReport
        {
            public string Name;
            public int MethodCount;
            public int TotalBytes;
            public int TotalHotCalls;
            public int LoopedMethods;
            public readonly List<MethodFinding> Methods = new List<MethodFinding>();
        }
    }
}
