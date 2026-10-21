using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PCL2McDownloadPatcher
{
    /// <summary>
    /// 还原 PCL2 中被裁剪的 MC 下载服务（ModSecret.RestrictMcDownload 的五处编译期裁剪）。
    /// 全部补丁都在 IL 层完成：常量已经被内联，改字段无效，只能重写方法体/分支。
    /// </summary>
    internal static class Program
    {
        /// <summary>被裁剪时统一使用的提示文本（补丁点 1、2 的定位锚点）。</summary>
        private const string RestrictMessage = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";

        /// <summary>检测用的稳定子串（版本间标点/后缀可能变，这半句不会变）。</summary>
        private const string RestrictNeedle = "为遵守相关法律法规";

        /// <summary>VB 编译器写进日志的源码路径，保持与官方构建一致。</summary>
        private const string ModModpackSourcePath =
            "E:\\Projects\\PCL\\本体代码\\Plain Craft Launcher 2\\Modules\\Minecraft\\ModModpack.vb";

        private static ModuleDefMD _module = null!;

        private static readonly Dictionary<string, IMethod> Methods = new(StringComparer.Ordinal);
        private static readonly List<IMethod> AllMethods = new();
        private static readonly HashSet<string> MethodFullNames = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, IField> Fields = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ITypeDefOrRef> Types = new(StringComparer.Ordinal);

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Length >= 1 && args[0] is "-h" or "--help")
            {
                Usage();
                return 0;
            }

            if (args.Length >= 1 && args[0] is "-v" or "--version")
            {
                var version = typeof(Program).Assembly.GetName().Version;
                Console.WriteLine("PCL2-MC-Download-Patcher " +
                                  (version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}"));
                return 0;
            }

            if (args.Length >= 3 && args[0] == "--diff")
                return Diff(args[1], args[2]);

            if (args.Length >= 2 && args[0] == "--scan")
                return RunOne(Path.GetFullPath(args[1]), null, scanOnly: true);

            if (args.Length >= 2 && args[0] == "--batch")
                return RunBatch(Path.GetFullPath(args[1]), args.Length > 2 ? Path.GetFullPath(args[2]) : null);

            if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal))
            {
                Usage();
                return 2;
            }

            string input = Path.GetFullPath(args[0]);
            string output = args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + "-McDownloadRestored.exe");

            return RunOne(input, output, scanOnly: false);
        }

        private static void Usage()
        {
            Console.WriteLine("PCL2-MC-Download-Patcher —— PCL2 MC 下载限制还原工具");
            Console.WriteLine("自动检测 + IL 原位缝补：不重新编译，也不改动下载以外的逻辑。");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  PCL2-MC-Download-Patcher <程序.exe> [输出.exe]     自动检测并修补");
            Console.WriteLine("  PCL2-MC-Download-Patcher --scan <程序.exe>          只检测，不写文件");
            Console.WriteLine("  PCL2-MC-Download-Patcher --batch <目录> [输出目录]  批量处理目录下的 exe");
            Console.WriteLine("  PCL2-MC-Download-Patcher --diff <a.exe> <b.exe>     逐方法比较两个程序集");
            Console.WriteLine("  PCL2-MC-Download-Patcher --version                  显示版本");
            Console.WriteLine();
            Console.WriteLine("退出码: 0 成功 / 无需处理   1 失败   2 参数错误   3 --diff 有差异   4 已修补但有未定位项");
        }

        private static int RunOne(string input, string? output, bool scanOnly)
        {
            if (!File.Exists(input))
            {
                Console.WriteLine("找不到输入文件: " + input);
                return 2;
            }

            Console.WriteLine("输入: " + input);
            if (!scanOnly && output != null) Console.WriteLine("输出: " + output);
            Console.WriteLine();

            try
            {
                ResetIndex();
                _module = ModuleDefMD.Load(input);
                BuildIndex();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 无法按 .NET 程序集读取（可能是原生程序、单文件打包或已损坏）: " + ex.Message);
                return 1;
            }

            Console.WriteLine("目标程序集: " + (_module.Assembly?.FullName ?? "(含本机代码，无程序集清单)"));
            if (_module.Assembly == null)
            {
                Console.WriteLine("[FAIL] 这不是一个 .NET 程序集，无法按 IL 缝补。");
                return 1;
            }
            Console.WriteLine();

            bool restricted = ModuleContains(RestrictNeedle);
            var sites = DetectSites();

            Console.WriteLine("检测结果:");
            foreach (var site in sites)
            {
                string mark = site.State switch
                {
                    SiteState.NeedsPatch => "[待修补]",
                    SiteState.AlreadyPatched => "[已还原]",
                    _ => "[未定位]",
                };
                Console.WriteLine($"  {mark} {site.Name}" + (site.Reason == null ? "" : "  —— " + site.Reason));
            }
            Console.WriteLine();

            var todo = sites.Where(s => s.State == SiteState.NeedsPatch).ToList();
            var unknown = sites.Where(s => s.State == SiteState.NotFound).ToList();

            if (sites.All(s => s.State == SiteState.NotFound))
            {
                Console.WriteLine("[FAIL] 这不是 PCL2 程序集，或该版本结构差异过大（5 个限制点都无法定位）。");
                return 1;
            }

            if (todo.Count == 0)
            {
                if (!restricted)
                    Console.WriteLine("[OK] 未检测到 MC 下载限制，无需修补。");
                else
                    Console.WriteLine("[OK] 检测到限制文本，但 5 个限制点都已是还原状态，无需重复修补。");
                return 0;
            }

            if (!restricted)
                Console.WriteLine("[提示] 没有找到限制提示文本，但仍检测到可还原的结构，继续处理。");

            foreach (var site in todo)
            {
                try
                {
                    site.Apply();
                    Console.WriteLine("[OK] " + site.Name);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FAIL] " + site.Name);
                    Console.WriteLine("       " + ex.Message);
                    return 1;
                }
            }

            Console.WriteLine();
            Console.WriteLine("校验 IL ...");
            try
            {
                VerifyIl(_module);
                Console.WriteLine("[OK] IL 基本校验通过");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] IL 校验失败: " + ex.Message);
                return 1;
            }

            Console.WriteLine("校验改动范围 ...");
            var expected = todo.Where(s => s.Method != null).Select(s => s.Method!.FullName).ToHashSet(StringComparer.Ordinal);
            try
            {
                var unexpected = FindUnexpectedChanges(input, expected);
                if (unexpected.Count > 0)
                {
                    Console.WriteLine("[FAIL] 出现了预期之外的改动，已中止:");
                    foreach (var name in unexpected.Take(10)) Console.WriteLine("       " + name);
                    return 1;
                }
                Console.WriteLine($"[OK] 只有 {expected.Count} 个预期方法被修改，其余方法原样保留");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 改动范围校验失败: " + ex.Message);
                return 1;
            }

            if (unknown.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("[WARN] 有 " + unknown.Count + " 个限制点未能定位，可能仍有残留:");
                foreach (var s in unknown) Console.WriteLine("       " + s.Name + (s.Reason == null ? "" : "（" + s.Reason + "）"));
            }

            if (scanOnly)
            {
                Console.WriteLine();
                Console.WriteLine("（--scan 模式，不写文件）");
                return 0;
            }

            var options = new ModuleWriterOptions(_module)
            {
                Logger = DummyLogger.NoThrowInstance,
            };
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;

            _module.Write(output!, options);
            Console.WriteLine();
            Console.WriteLine("已写出: " + output);
            return unknown.Count > 0 ? 4 : 0;
        }

        private static int RunBatch(string directory, string? outDir)
        {
            if (!Directory.Exists(directory))
            {
                Console.WriteLine("找不到目录: " + directory);
                return 2;
            }

            var files = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                Console.WriteLine("目录里没有 exe: " + directory);
                return 2;
            }

            int ok = 0, skipped = 0, failed = 0;
            foreach (var file in files)
            {
                Console.WriteLine(new string('=', 70));
                Console.WriteLine(Path.GetFileName(file));

                bool hasRestriction;
                try
                {
                    var probe = ModuleDefMD.Load(file);
                    hasRestriction = probe.Assembly != null && ProbeContains(probe, RestrictNeedle);
                }
                catch
                {
                    Console.WriteLine("  跳过：不是 .NET 程序集");
                    skipped++;
                    continue;
                }

                if (!hasRestriction)
                {
                    Console.WriteLine("  跳过：未检测到限制文本");
                    skipped++;
                    continue;
                }

                string target = outDir == null
                    ? Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + "-McDownloadRestored.exe")
                    : Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + "-McDownloadRestored.exe");

                if (outDir != null) Directory.CreateDirectory(outDir);

                int code = RunOne(file, target, scanOnly: false);
                if (code == 0) ok++; else failed++;
                Console.WriteLine();
            }

            Console.WriteLine(new string('=', 70));
            Console.WriteLine($"完成：成功 {ok}，跳过 {skipped}，失败 {failed}");
            return failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // 自动检测：先判断每个限制点处于什么状态，只修补需要的
        // ------------------------------------------------------------------
        private enum SiteState { NeedsPatch, AlreadyPatched, NotFound }

        private sealed class Site
        {
            public string Name = "";
            public SiteState State;
            public string? Reason;
            public MethodDef? Method;
            public Action Apply = () => { };
        }

        private static Site MakeSite(string name, MethodDef? method, Action apply,
            Func<MethodDef, bool> needsPatch, Func<MethodDef, bool> already)
        {
            var site = new Site { Name = name, Apply = apply, Method = method };
            if (method == null)
            {
                site.State = SiteState.NotFound;
                site.Reason = "目标方法不存在";
            }
            else if (needsPatch(method))
            {
                site.State = SiteState.NeedsPatch;
            }
            else if (already(method))
            {
                site.State = SiteState.AlreadyPatched;
            }
            else
            {
                site.State = SiteState.NotFound;
                site.Reason = "指令形状与已知版本都不同";
            }
            return site;
        }

        private static List<Site> DetectSites()
        {
            var modmodpack = _module.Find("PCL.ModModpack", false);
            var m1 = modmodpack?.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.MethodSig?.Params.Count == 0);
            var m2 = modmodpack?.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.MethodSig?.Params.Count == 3);
            var pdi = _module.Find("PCL.PageDownloadInstall", false);
            var m3 = pdi?.Methods.FirstOrDefault(m => m.Name == "LoaderInit" && m.MethodSig?.Params.Count == 0);
            var pdm = _module.Find("PCL.PageDownloadModify", false);
            var m4 = pdm?.Methods.FirstOrDefault(m => m.Name == "LoadInstanceFinished" && m.MethodSig?.Params.Count == 1);
            var pdr = _module.Find("PCL.PageDownloadResourceDetail", false);
            var m5 = pdr?.Methods.FirstOrDefault(m => m.Name == "InitializeStackResourceVersions" && m.MethodSig?.Params.Count == 1);

            return new List<Site>
            {
                MakeSite("1. ModModpack.ModpackInstall() —— 整合包文件选择入口",
                    m1, Patch1_ModpackInstallEntry,
                    needsPatch: m => HasLdstr(m, RestrictNeedle),
                    already: m => HasCallNamed(m, "SelectFile")),

                MakeSite("2. ModModpack.ModpackInstall(String,String,String) —— 整合包类型分发",
                    m2, Patch2_ModpackInstallDispatch,
                    needsPatch: m => HasLdstr(m, RestrictNeedle) && HasThrow(m),
                    already: m => HasCallNamed(m, "InstallPack")),

                MakeSite("3. PageDownloadInstall.LoaderInit() —— MC 版本列表下载器",
                    m3, Patch3_LoaderInit,
                    needsPatch: m => HasNewObjNamed(m, "LoaderTask`2") && !HasFieldNamed(m, "DlClientListLoader"),
                    already: m => HasFieldNamed(m, "DlClientListLoader")),

                MakeSite("4. PageDownloadModify.LoadInstanceFinished() —— 修改页版本卡片",
                    m4, Patch4_ModifyCardVisible,
                    needsPatch: m => CardVisibility(m) == 2,
                    already: m => CardVisibility(m) == 0),

                MakeSite("5. PageDownloadResourceDetail.InitializeStackResourceVersions() —— 整合包安装按钮",
                    m5, Patch5_ResourceDetailInstallButton,
                    needsPatch: m => CountLdftnBeforeToListItem(m) == 1,
                    already: m => CountLdftnBeforeToListItem(m) >= 2),
            };
        }

        private static bool HasLdstr(MethodDef method, string needle)
        {
            if (!method.HasBody) return false;
            foreach (var ins in method.Body.Instructions)
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains(needle, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool HasThrow(MethodDef method)
            => method.HasBody && method.Body.Instructions.Any(i => i.OpCode == OpCodes.Throw);

        private static bool HasCallNamed(MethodDef method, string namePart)
        {
            if (!method.HasBody) return false;
            foreach (var ins in method.Body.Instructions)
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                    && ins.Operand is IMethod m && m.Name.ToString().Contains(namePart, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool HasNewObjNamed(MethodDef method, string typeName)
        {
            if (!method.HasBody) return false;
            foreach (var ins in method.Body.Instructions)
                if (ins.OpCode == OpCodes.Newobj && ins.Operand is IMethod m
                    && (m.DeclaringType?.Name == typeName
                        || (m.DeclaringType?.FullName.StartsWith("PCL.ModLoader/" + typeName + "<", StringComparison.Ordinal) ?? false)))
                    return true;
            return false;
        }

        private static bool HasFieldNamed(MethodDef method, string fieldName)
        {
            if (!method.HasBody) return false;
            foreach (var ins in method.Body.Instructions)
                if ((ins.OpCode == OpCodes.Ldsfld || ins.OpCode == OpCodes.Ldfld)
                    && ins.Operand is IField f && f.Name == fieldName)
                    return true;
            return false;
        }

        /// <summary>返回 CardMinecraft.Visibility 被赋的枚举值（2=Collapsed，0=Visible），找不到返回 -1。</summary>
        private static int CardVisibility(MethodDef method)
        {
            if (!method.HasBody) return -1;
            var ins = method.Body.Instructions;
            for (int i = 1; i < ins.Count; i++)
            {
                if (ins[i - 1].OpCode == OpCodes.Callvirt
                    && ins[i - 1].Operand is IMethod g && g.Name == "get_CardMinecraft")
                {
                    if (ins[i].OpCode == OpCodes.Ldc_I4_0) return 0;
                    if (ins[i].OpCode == OpCodes.Ldc_I4_2) return 2;
                }
            }
            return -1;
        }

        /// <summary>版本项 ToListItem 调用前挂的委托数量（未还原=1，已还原=2），找不到返回 -1。</summary>
        private static int CountLdftnBeforeToListItem(MethodDef method)
        {
            if (!method.HasBody) return -1;
            var ins = method.Body.Instructions;

            int best = -1;
            for (int idx = 0; idx < ins.Count; idx++)
            {
                if (ins[idx].OpCode != OpCodes.Callvirt
                    || ins[idx].Operand is not IMethod m
                    || m.Name != "ToListItem"
                    || m.MethodSig?.Params.Count != 3)
                    continue;

                int count = 0;
                for (int i = Math.Max(0, idx - 20); i < idx; i++)
                    if (ins[i].OpCode == OpCodes.Ldftn) count++;

                if (count > best) best = count;
            }
            return best;
        }

        private static bool ModuleContains(string needle) => ProbeContains(_module, needle);

        private static bool ProbeContains(ModuleDef module, string needle)
        {
            foreach (var type in module.GetTypes())
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var ins in method.Body.Instructions)
                        if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains(needle, StringComparison.Ordinal))
                            return true;
                }
            return false;
        }

        private static void ResetIndex()
        {
            Methods.Clear();
            AllMethods.Clear();
            MethodFullNames.Clear();
            Fields.Clear();
            Types.Clear();
        }

        /// <summary>把打好补丁的模块和磁盘上的原文件逐方法比较，确保只动了预期的方法。</summary>
        private static List<string> FindUnexpectedChanges(string originalPath, HashSet<string> expected)
        {
            var original = ModuleDefMD.Load(originalPath);
            var current = _module.GetTypes()
                .SelectMany(t => t.Methods)
                .GroupBy(m => m.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var unexpected = new List<string>();
            foreach (var type in original.GetTypes())
                foreach (var before in type.Methods)
                {
                    if (!current.TryGetValue(before.FullName, out var after))
                    {
                        unexpected.Add(before.FullName + "（方法消失）");
                        continue;
                    }
                    if (CompareMethod(before, after) != null && !expected.Contains(before.FullName))
                        unexpected.Add(before.FullName);
                }
            return unexpected;
        }

        // ------------------------------------------------------------------
        // 索引 / 查找辅助
        // ------------------------------------------------------------------

        private static void BuildIndex()
        {
            foreach (var type in _module.GetTypes())
            {
                AddType(type);
                foreach (var field in type.Fields) AddField(field);
                foreach (var method in type.Methods)
                {
                    AddMethod(method);
                    if (!method.HasBody) continue;
                    foreach (var ins in method.Body.Instructions)
                    {
                        switch (ins.Operand)
                        {
                            case IMethod im:
                                AddMethod(im);
                                if (im.DeclaringType != null) AddType(im.DeclaringType);
                                break;
                            case IField f:
                                AddField(f);
                                if (f.DeclaringType != null) AddType(f.DeclaringType);
                                break;
                            case ITypeDefOrRef td:
                                AddType(td);
                                break;
                        }
                    }
                }
            }

            foreach (var tr in _module.GetTypeRefs()) AddType(tr);
        }

        private static void AddType(ITypeDefOrRef type)
        {
            if (!string.IsNullOrEmpty(type.FullName))
                Types.TryAdd(type.FullName, type);
        }

        private static void AddField(IField field)
        {
            if (field.DeclaringType == null) return;
            Fields.TryAdd(field.DeclaringType.FullName + "::" + field.Name, field);
        }

        private static void AddMethod(IMethod method)
        {
            if (method.DeclaringType == null) return;
            string key = MethodKey(method.DeclaringType.FullName, method.Name, method.MethodSig?.Params.Count ?? -1);
            Methods.TryAdd(key, method);
            if (MethodFullNames.Add(method.FullName))
                AllMethods.Add(method);
        }

        private static string MethodKey(string? declaringType, string name, int paramCount)
            => declaringType + "::" + name + "/" + paramCount;

        private static TypeDef GetType(string fullName)
        {
            var type = _module.Find(fullName, false);
            if (type == null) throw new Exception("找不到类型: " + fullName);
            return type;
        }

        private static MethodDef GetMethod(TypeDef type, string name, int paramCount)
        {
            var method = type.Methods.FirstOrDefault(
                m => m.Name == name && m.MethodSig != null && m.MethodSig.Params.Count == paramCount);
            if (method == null)
                throw new Exception($"找不到方法: {type.FullName}::{name}/{paramCount}");
            return method;
        }

        private static IMethod GetMethodRef(string declaringType, string name, int paramCount)
        {
            if (Methods.TryGetValue(MethodKey(declaringType, name, paramCount), out var method))
                return method;
            throw new Exception($"找不到方法引用: {declaringType}::{name}/{paramCount}");
        }

        private static IMethod GetMethodRefBySignature(string declaringType, string name, string parameterSignature)
        {
            foreach (var method in AllMethods)
            {
                if (method.Name != name) continue;
                if (method.DeclaringType?.FullName != declaringType) continue;
                string fullName = method.FullName;
                int index = fullName.IndexOf("::" + name, StringComparison.Ordinal);
                if (index < 0) continue;
                if (fullName[(index + 2 + name.Length)..] == parameterSignature)
                    return method;
            }
            var candidates = AllMethods
                .Where(x => x.Name == name)
                .Select(x => x.DeclaringType?.FullName + " | " + x.FullName)
                .Distinct()
                .Take(12);
            throw new Exception($"找不到方法引用: {declaringType}::{name}{parameterSignature}\n候选:\n  " + string.Join("\n  ", candidates));
        }

        private static IField GetFieldRef(string declaringType, string name)
        {
            if (Fields.TryGetValue(declaringType + "::" + name, out var field))
                return field;
            throw new Exception($"找不到字段引用: {declaringType}::{name}");
        }

        private static ITypeDefOrRef GetTypeRef(string fullName)
        {
            if (Types.TryGetValue(fullName, out var type))
                return type;
            throw new Exception("找不到类型引用: " + fullName);
        }

        /// <summary>构造委托的 .ctor(object, native int)，必要时新建 MemberRef。</summary>
        private static IMethod GetDelegateCtor(ITypeDefOrRef delegateType)
        {
            if (Methods.TryGetValue(MethodKey(delegateType.FullName, ".ctor", 2), out var ctor))
                return ctor;

            var sig = MethodSig.CreateInstance(_module.CorLibTypes.Void, _module.CorLibTypes.Object, _module.CorLibTypes.IntPtr);
            return new MemberRefUser(_module, ".ctor", sig, delegateType);
        }

        private static int IndexOfFirst(IList<Instruction> instructions, Func<Instruction, bool> predicate)
        {
            for (int i = 0; i < instructions.Count; i++)
                if (predicate(instructions[i])) return i;
            return -1;
        }


        /// <summary>按局部变量下标选最短的 ldloc 形式，保证输出稳定。</summary>
        private static Instruction LoadLocal(Local local) => local.Index switch
        {
            0 => Instruction.Create(OpCodes.Ldloc_0),
            1 => Instruction.Create(OpCodes.Ldloc_1),
            2 => Instruction.Create(OpCodes.Ldloc_2),
            3 => Instruction.Create(OpCodes.Ldloc_3),
            _ => Instruction.Create(OpCodes.Ldloc_S, local),
        };

        private static Instruction StoreLocal(Local local) => local.Index switch
        {
            0 => Instruction.Create(OpCodes.Stloc_0),
            1 => Instruction.Create(OpCodes.Stloc_1),
            2 => Instruction.Create(OpCodes.Stloc_2),
            3 => Instruction.Create(OpCodes.Stloc_3),
            _ => Instruction.Create(OpCodes.Stloc_S, local),
        };
        /// <summary>取指定类型的局部变量，优先用已知版本的索引，找不到就按类型挑第一个。</summary>
        private static Local FindLocalOfType(CilBody body, string typeName, int preferredIndex, string what)
        {
            var vars = body.Variables;
            if (preferredIndex >= 0 && preferredIndex < vars.Count && vars[preferredIndex].Type.FullName == typeName)
                return vars[preferredIndex];
            foreach (var local in vars)
                if (local.Type.FullName == typeName)
                    return local;

            throw new Exception($"无法定位{what}局部变量（期望 {typeName}，实际: " +
                                string.Join(", ", vars.Select(v => v.Type.FullName)) + "）");
        }

        private static Local FindLocalOfType(CilBody body, TypeSig type, int preferredIndex, string what)
            => FindLocalOfType(body, type.FullName, preferredIndex, what);

        /// <summary>PackType 所在局部变量：优先 V_4，否则挑“被 ldc.i4 赋值次数最多”的 Int32 局部。</summary>
        private static Local FindPackTypeLocal(CilBody body)
        {
            var vars = body.Variables;
            if (vars.Count > 4 && vars[4].Type.FullName == "System.Int32")
                return vars[4];

            var counts = new Dictionary<Local, int>();
            var instructions = body.Instructions;
            for (int i = 1; i < instructions.Count; i++)
            {
                Local? target = instructions[i].OpCode.Code switch
                {
                    Code.Stloc_0 => vars.Count > 0 ? vars[0] : null,
                    Code.Stloc_1 => vars.Count > 1 ? vars[1] : null,
                    Code.Stloc_2 => vars.Count > 2 ? vars[2] : null,
                    Code.Stloc_3 => vars.Count > 3 ? vars[3] : null,
                    Code.Stloc_S or Code.Stloc => instructions[i].Operand as Local,
                    _ => null,
                };
                if (target == null || target.Type.FullName != "System.Int32") continue;

                var prev = instructions[i - 1].OpCode.Code;
                bool fromConstant = prev is Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_I4_0 or Code.Ldc_I4_1
                    or Code.Ldc_I4_2 or Code.Ldc_I4_3 or Code.Ldc_I4_4 or Code.Ldc_I4_5
                    or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8 or Code.Ldc_I4_M1;
                if (fromConstant) counts[target] = counts.TryGetValue(target, out var c) ? c + 1 : 1;
            }

            if (counts.Count > 0)
                return counts.OrderByDescending(kv => kv.Value).First().Key;

            foreach (var local in vars)
                if (local.Type.FullName == "System.Int32")
                    return local;

            throw new Exception("无法定位整合包类型（Int32）局部变量");
        }

        private static void RemoveRangeAt(this IList<Instruction> instructions, int index, int count)
        {
            for (int i = index + count - 1; i >= index; i--)
                instructions.RemoveAt(i);
        }

        private static void InsertRangeAt(this IList<Instruction> instructions, int index, IEnumerable<Instruction> items)
        {
            foreach (var item in items)
                instructions.Insert(index++, item);
        }

        // ------------------------------------------------------------------
        // 补丁 1：ModModpack.ModpackInstall()
        //  2.99 里只剩一个 Hint 调用；还原 2.13/2.14 的“选文件 -> 起线程安装”流程。
        // ------------------------------------------------------------------
        private static void Patch1_ModpackInstallEntry()
        {
            var type = GetType("PCL.ModModpack");
            var method = GetMethod(type, "ModpackInstall", 0);
            var body = method.Body;

            const string closureTypeName = "PCL.ModModpack/_Closure$__0-0";
            var closureType = GetType(closureTypeName);
            var closureCtor = GetMethod(closureType, ".ctor", 0);
            var lambda = GetMethod(closureType, "_Lambda$__0", 0);
            var fileField = GetFieldRef(closureTypeName, "$VB$Local_File");

            const string valueTupleTypeName = "System.ValueTuple`2<System.String[],System.String>";
            var valueTupleType = GetTypeRef(valueTupleTypeName);
            var valueTupleCtor = GetMethodRef(valueTupleTypeName, ".ctor", 2);
            var stringType = GetTypeRef("System.String");

            const string oneOfTypeName =
                "MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>";
            var oneOfImplicit = GetMethodRefBySignature(
                oneOfTypeName, "op_Implicit",
                "(System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>)");
            var nullableCtor = GetMethodRef(
                "System.Nullable`1<MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>>",
                ".ctor", 1);

            var selectFile = GetMethodRef("MeloongCore.Wpf.Dialogs", "SelectFile", 4);
            var firstOrDefault = GetMethodRef("System.Linq.Enumerable", "FirstOrDefault", 1);
            var isNullOrEmpty = GetMethodRef("System.String", "IsNullOrEmpty", 1);
            var runInThread = GetMethodRef("PCL.ModBase", "RunInThread", 1);
            var actionCtor = GetMethodRef("System.Action", ".ctor", 2);

            var instructions = body.Instructions;
            instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.InitLocals = true;
            body.MaxStack = 11;

            var done = Instruction.Create(OpCodes.Ret);

            instructions.Add(Instruction.Create(OpCodes.Newobj, closureCtor));
            instructions.Add(Instruction.Create(OpCodes.Stloc_0));
            instructions.Add(Instruction.Create(OpCodes.Ldloc_0));

            // Dialogs.SelectFile("选择整合包压缩文件", False, Nothing,
            //     New ValueTuple(Of String(), String)() {({"rar", "zip", "mrpack"}, "整合包文件")})
            instructions.Add(Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            instructions.Add(Instruction.Create(OpCodes.Ldnull));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            instructions.Add(Instruction.Create(OpCodes.Newarr, valueTupleType));
            instructions.Add(Instruction.Create(OpCodes.Dup));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_3));
            instructions.Add(Instruction.Create(OpCodes.Newarr, stringType));
            instructions.Add(Instruction.Create(OpCodes.Dup));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            instructions.Add(Instruction.Create(OpCodes.Ldstr, "rar"));
            instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));
            instructions.Add(Instruction.Create(OpCodes.Dup));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            instructions.Add(Instruction.Create(OpCodes.Ldstr, "zip"));
            instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));
            instructions.Add(Instruction.Create(OpCodes.Dup));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4_2));
            instructions.Add(Instruction.Create(OpCodes.Ldstr, "mrpack"));
            instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));
            instructions.Add(Instruction.Create(OpCodes.Ldstr, "整合包文件"));
            instructions.Add(Instruction.Create(OpCodes.Newobj, valueTupleCtor));
            instructions.Add(Instruction.Create(OpCodes.Stelem, valueTupleType));
            instructions.Add(Instruction.Create(OpCodes.Call, oneOfImplicit));
            instructions.Add(Instruction.Create(OpCodes.Newobj, nullableCtor));
            instructions.Add(Instruction.Create(OpCodes.Call, selectFile));
            instructions.Add(Instruction.Create(OpCodes.Call, firstOrDefault));
            instructions.Add(Instruction.Create(OpCodes.Stfld, fileField));

            // If Not String.IsNullOrEmpty($VB$Local_File) Then RunInThread(Sub() ...)
            instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
            instructions.Add(Instruction.Create(OpCodes.Ldfld, fileField));
            instructions.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
            instructions.Add(Instruction.Create(OpCodes.Brtrue, done));
            instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
            instructions.Add(Instruction.Create(OpCodes.Ldftn, lambda));
            instructions.Add(Instruction.Create(OpCodes.Newobj, actionCtor));
            instructions.Add(Instruction.Create(OpCodes.Call, runInThread));
            instructions.Add(done);
        }

        // ------------------------------------------------------------------
        // 补丁 2：ModModpack.ModpackInstall(string, string, string)
        //  尾部被替换成无条件 throw；换回 2.13 的 Select Case PackType 分发。
        // ------------------------------------------------------------------
        private static void Patch2_ModpackInstallDispatch()
        {
            var type = GetType("PCL.ModModpack");
            var method = GetMethod(type, "ModpackInstall", 3);
            var body = method.Body;
            var instructions = body.Instructions;

            int throwIndex = -1;
            for (int i = 0; i + 2 < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Ldstr
                    && (string)instructions[i].Operand == RestrictMessage
                    && instructions[i + 1].OpCode == OpCodes.Newobj
                    && instructions[i + 2].OpCode == OpCodes.Throw)
                {
                    throwIndex = i;
                    break;
                }
            }

            if (throwIndex < 0)
                throw new Exception("未找到裁剪用的 throw 指令（可能已经打过补丁）");

            var throwInstruction = instructions[throwIndex];

            var installCurseForge = GetMethod(type, "InstallPackCurseForge", 5);
            var installHmcl = GetMethod(type, "InstallPackHMCL", 3);
            var installMmc = GetMethod(type, "InstallPackMMC", 3);
            var installMcbbs = GetMethod(type, "InstallPackMCBBS", 4);
            var installModrinth = GetMethod(type, "InstallPackModrinth", 5);
            var installLauncherPack = GetMethod(type, "InstallPackLauncherPack", 3);
            var installCompress = GetMethod(type, "InstallPackCompress", 2);

            IMethod loggerInfo = method.Body.Instructions
                .Select(ins => ins.Operand)
                .OfType<IMethod>()
                .FirstOrDefault(m => m.Name == "Info"
                                     && m.MethodSig != null
                                     && m.MethodSig.Params.Count == 3
                                     && m.DeclaringType?.FullName == "MeloongCore.Logger")
                ?? GetMethodRef("MeloongCore.Logger", "Info", 3);

            var packTypeLocal = FindPackTypeLocal(body);
            var resultLocal = FindLocalOfType(body, method.ReturnType, 0, "返回值");
            var archiveLocal = FindLocalOfType(body, "System.IO.Compression.ZipArchive", 1, "ZipArchive");
            var pathLocal = FindLocalOfType(body, "System.String", 2, "路径");

            // 日志里的源码路径直接从现有调用里取，避免跨版本写死
            string sourcePath = instructions
                .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s
                            && s.EndsWith("ModModpack.vb", StringComparison.OrdinalIgnoreCase))
                .Select(i => (string)i.Operand)
                .FirstOrDefault() ?? ModModpackSourcePath;

            var epilogue = new List<Instruction>
            {
                LoadLocal(resultLocal),
                Instruction.Create(OpCodes.Ret),
            };

            List<Instruction> Case(string message, IMethod target, bool withInstanceName, bool withLogo)
            {
                var block = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Ldstr, message),
                    Instruction.Create(OpCodes.Ldc_I4_0),
                    Instruction.Create(OpCodes.Ldstr, sourcePath),
                    Instruction.Create(OpCodes.Call, loggerInfo),
                    Instruction.Create(OpCodes.Ldarg_0),
                    LoadLocal(archiveLocal),
                    LoadLocal(pathLocal),
                };
                if (withInstanceName) block.Add(Instruction.Create(OpCodes.Ldarg_1));
                if (withLogo) block.Add(Instruction.Create(OpCodes.Ldarg_2));
                block.Add(Instruction.Create(OpCodes.Call, target));
                block.Add(StoreLocal(resultLocal));
                block.Add(Instruction.Create(OpCodes.Leave, epilogue[0]));
                return block;
            }

            // PackType: 0 CurseForge, 1 HMCL, 2 MMC, 3 MCBBS, 4 Modrinth,
            //           5-8 未识别, 9 带启动器的压缩包, 其余 = Compress
            var curseForge = Case("整合包种类：CurseForge", installCurseForge, true, true);
            var hmcl = Case("整合包种类：HMCL", installHmcl, false, false);
            var mmc = Case("整合包种类：MMC", installMmc, false, false);
            var mcbbs = Case("整合包种类：MCBBS", installMcbbs, true, false);
            var modrinth = Case("整合包种类：Modrinth", installModrinth, true, true);
            var launcherPack = Case("整合包种类：带启动器的压缩包", installLauncherPack, false, false);
            var compress = Case("整合包种类：未能识别，假定为压缩包", installCompress, false, false);

            var switchInstruction = new Instruction
            {
                OpCode = OpCodes.Switch,
                Operand = new[]
                {
                    curseForge[0], hmcl[0], mmc[0], mcbbs[0], modrinth[0],
                    compress[0], compress[0], compress[0], compress[0], launcherPack[0],
                },
            };

            var dispatch = new List<Instruction>
            {
                LoadLocal(packTypeLocal),
                switchInstruction,
                Instruction.Create(OpCodes.Br, compress[0]),
            };
            dispatch.AddRange(curseForge);
            dispatch.AddRange(hmcl);
            dispatch.AddRange(mmc);
            dispatch.AddRange(mcbbs);
            dispatch.AddRange(modrinth);
            dispatch.AddRange(launcherPack);
            dispatch.AddRange(compress);

            // 所有 Exit Try 的 leave 目标原本就是这条 throw 指令，改为跳到分发开头。
            foreach (var ins in instructions)
            {
                if ((ins.OpCode == OpCodes.Leave || ins.OpCode == OpCodes.Leave_S)
                    && ReferenceEquals(ins.Operand, throwInstruction))
                {
                    ins.Operand = dispatch[0];
                }
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                // 内部 Catch 的 HandlerEnd 指向这条 throw（即分发开头之前）。
                if (ReferenceEquals(handler.HandlerEnd, throwInstruction))
                    handler.HandlerEnd = dispatch[0];
                // 外层 Finally 原本一直到方法末尾，现在末尾多了返回尾巴。
                else if (handler.HandlerEnd == null)
                    handler.HandlerEnd = epilogue[0];
            }

            instructions.RemoveRangeAt(throwIndex, 3);
            instructions.InsertRangeAt(throwIndex, dispatch);
            instructions.InsertRangeAt(instructions.Count, epilogue);
            body.MaxStack = Math.Max(body.MaxStack, (ushort)8);
        }

        // ------------------------------------------------------------------
        // 补丁 3：PageDownloadInstall.LoaderInit()
        //  2.99 用“关闭下载”+ 抛异常的 LoaderTask 替换了 DlClientListLoader。
        // ------------------------------------------------------------------
        private static void Patch3_LoaderInit()
        {
            var type = GetType("PCL.PageDownloadInstall");
            var method = GetMethod(type, "LoaderInit", 0);
            var instructions = method.Body.Instructions;

            // 先找到被塞进去的 LoaderTask 构造，再向前回退到整段对象创建表达式的起点
            int end = IndexOfFirst(instructions, ins =>
                ins.OpCode == OpCodes.Newobj
                && ins.Operand is IMethod ctor
                && ctor.DeclaringType != null
                && (ctor.DeclaringType.Name == "LoaderTask`2"
                    || ctor.DeclaringType.FullName.StartsWith("PCL.ModLoader/LoaderTask`2<", StringComparison.Ordinal)));

            if (end < 0)
                throw new Exception("未找到被替换的 LoaderTask 构造");

            int start = -1;
            for (int i = end - 1; i >= 0 && i >= end - 8; i--)
            {
                var code = instructions[i].OpCode.Code;
                if (code == Code.Ldstr) { start = i; break; }
                if (code == Code.Call || code == Code.Callvirt) break;
                if (code == Code.Newobj) continue;   // 委托构造，允许
            }

            // 兜底：按已知版本的提示文本定位
            if (start < 0)
                start = IndexOfFirst(instructions, ins => ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s == "关闭下载");

            if (start < 0 || start >= end)
                throw new Exception("未能确定被替换 LoaderTask 的起始指令");

            var dlClientListLoader = GetFieldRef("PCL.ModDownload", "DlClientListLoader");

            instructions.RemoveRangeAt(start, end - start + 1);
            instructions.Insert(start, Instruction.Create(OpCodes.Ldsfld, dlClientListLoader));
        }

        // ------------------------------------------------------------------
        // 补丁 4：PageDownloadModify.LoadInstanceFinished(Task)
        //  CardMinecraft.Visibility = Collapsed -> Visible
        // ------------------------------------------------------------------
        private static void Patch4_ModifyCardVisible()
        {
            var type = GetType("PCL.PageDownloadModify");
            var method = GetMethod(type, "LoadInstanceFinished", 1);
            var instructions = method.Body.Instructions;

            for (int i = 1; i < instructions.Count; i++)
            {
                var previous = instructions[i - 1];
                if (previous.OpCode == OpCodes.Callvirt
                    && previous.Operand is IMethod getter
                    && getter.Name == "get_CardMinecraft"
                    && getter.DeclaringType?.FullName == "PCL.PageDownloadInstall"
                    && instructions[i].OpCode == OpCodes.Ldc_I4_2)
                {
                    instructions[i] = Instruction.Create(OpCodes.Ldc_I4_0);
                    return;
                }
            }

            throw new Exception("未找到 CardMinecraft 的 Visibility 赋值（可能已经打过补丁）");
        }

        // ------------------------------------------------------------------
        // 补丁 5：PageDownloadResourceDetail.InitializeStackResourceVersions(StackPanel)
        //  整合包资源详情页的版本列表只挂了 Save_Click；恢复成
        //  If TargetResourceType = ModPack Then ToListItem(Install_Click, Save_Click) Else ToListItem(Save_Click)
        // ------------------------------------------------------------------
        private static void Patch5_ResourceDetailInstallButton()
        {
            var type = GetType("PCL.PageDownloadResourceDetail");
            var method = GetMethod(type, "InitializeStackResourceVersions", 1);
            var body = method.Body;
            var instructions = body.Instructions;

            // 版本项之后必须是 Children.Add，避免误选到其他同名重载调用
            int toListItemIndex = -1;
            for (int i = 0; i + 1 < instructions.Count; i++)
            {
                if (instructions[i].OpCode != OpCodes.Callvirt) continue;
                if (instructions[i].Operand is not IMethod m
                    || m.Name != "ToListItem"
                    || m.MethodSig?.Params.Count != 3) continue;

                var next = instructions[i + 1];
                if (next.OpCode == OpCodes.Callvirt
                    && next.Operand is IMethod add
                    && add.Name == "Add")
                {
                    toListItemIndex = i;
                    break;
                }
            }

            if (toListItemIndex < 8)
                throw new Exception("未找到 ToListItem 调用");

            var toListItem = (IMethod)instructions[toListItemIndex].Operand;
            var listItemHandlerType = toListItem.MethodSig.Params[0].ToTypeDefOrRef();
            var iconHandlerType = toListItem.MethodSig.Params[1].ToTypeDefOrRef();
            var listItemHandlerCtor = GetDelegateCtor(listItemHandlerType);
            var iconHandlerCtor = GetDelegateCtor(iconHandlerType);

            var childrenGetter = instructions[toListItemIndex - 7].Operand as IMethod;
            if (childrenGetter == null || childrenGetter.Name != "get_Children")
                throw new Exception("ToListItem 前的 Children 取值模式与预期不符");

            // Install_Click 的签名是 (MyListItem, EventArgs)，而 ClickEventHandler.Invoke 是
            // (Object, MouseButtonEventArgs)：直接 ldftn 不满足委托变体规则，会抛 ArgumentException。
            // 2.14 编译时 VB 为 AddressOf Install_Click 生成了放宽委托包装方法 _Lambda$__R18-5，
            // 2.99 仍然保留着它，直接复用即可。
            var installClick = GetMethod(type, "_Lambda$__R18-5", 2);
            var saveClick = GetMethod(type, "Save_Click", 2);
            var targetTypeField = GetFieldRef("PCL.PageDownloadResourceDetail", "TargetResourceType");
            var versionLocal = body.Variables[7];
            var isBadDisplayLocal = body.Variables[1];

            // 原始序列（9 条）：ldarg.1 / callvirt get_Children / ldloc.s V_7 / ldarg.0 /
            //                 ldftn Save_Click / newobj ClickEventHandler / ldnull / ldloc.1 /
            //                 callvirt ToListItem
            var originalStart = toListItemIndex - 8;
            if (instructions[originalStart].OpCode != OpCodes.Ldarg_1
                || instructions[toListItemIndex - 4].OpCode != OpCodes.Ldftn
                || instructions[toListItemIndex - 3].OpCode != OpCodes.Newobj
                || instructions[toListItemIndex - 2].OpCode != OpCodes.Ldnull)
            {
                throw new Exception("ToListItem 调用前的指令序列与预期不符");
            }

            var addCall = instructions[toListItemIndex + 1];
            if (addCall.OpCode != OpCodes.Callvirt)
                throw new Exception("ToListItem 之后不是 Children.Add 调用");

            var installBlock = new List<Instruction>
            {
                // If TargetResourceType = ResourceTypes.ModPack Then
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, targetTypeField),
                Instruction.Create(OpCodes.Ldc_I4_2),
            };
            var installStart = new Instruction { OpCode = OpCodes.Nop };
            installBlock.Add(Instruction.Create(OpCodes.Beq, installStart));

            // Else：只挂保存按钮
            installBlock.Add(Instruction.Create(OpCodes.Ldarg_1));
            installBlock.Add(Instruction.Create(OpCodes.Callvirt, childrenGetter));
            installBlock.Add(Instruction.Create(OpCodes.Ldloc_S, versionLocal));
            installBlock.Add(Instruction.Create(OpCodes.Ldarg_0));
            installBlock.Add(Instruction.Create(OpCodes.Ldftn, saveClick));
            installBlock.Add(Instruction.Create(OpCodes.Newobj, listItemHandlerCtor));
            installBlock.Add(Instruction.Create(OpCodes.Ldnull));
            installBlock.Add(Instruction.Create(OpCodes.Ldloc_S, isBadDisplayLocal));
            installBlock.Add(Instruction.Create(OpCodes.Callvirt, toListItem));
            installBlock.Add(Instruction.Create(OpCodes.Br, addCall));

            // Then：安装 + 保存
            installBlock.Add(installStart);
            installBlock.Add(Instruction.Create(OpCodes.Ldarg_1));
            installBlock.Add(Instruction.Create(OpCodes.Callvirt, childrenGetter));
            installBlock.Add(Instruction.Create(OpCodes.Ldloc_S, versionLocal));
            installBlock.Add(Instruction.Create(OpCodes.Ldarg_0));
            installBlock.Add(Instruction.Create(OpCodes.Ldftn, installClick));
            installBlock.Add(Instruction.Create(OpCodes.Newobj, listItemHandlerCtor));
            installBlock.Add(Instruction.Create(OpCodes.Ldarg_0));
            installBlock.Add(Instruction.Create(OpCodes.Ldftn, saveClick));
            installBlock.Add(Instruction.Create(OpCodes.Newobj, iconHandlerCtor));
            installBlock.Add(Instruction.Create(OpCodes.Ldloc_S, isBadDisplayLocal));
            installBlock.Add(Instruction.Create(OpCodes.Callvirt, toListItem));

            instructions.RemoveRangeAt(originalStart, 9);
            instructions.InsertRangeAt(originalStart, installBlock);
            body.MaxStack = Math.Max(body.MaxStack, (ushort)6);
        }

        // ------------------------------------------------------------------
        // --diff：逐方法比较两个程序集，用于证明补丁只改了预期的那几个方法。
        // ------------------------------------------------------------------
        private static int Diff(string a, string b)
        {
            var ma = ModuleDefMD.Load(Path.GetFullPath(a));
            var mb = ModuleDefMD.Load(Path.GetFullPath(b));

            Console.WriteLine("原版: " + Path.GetFullPath(a));
            Console.WriteLine("新版: " + Path.GetFullPath(b));
            Console.WriteLine();

            var methodsB = mb.GetTypes()
                .SelectMany(t => t.Methods)
                .GroupBy(m => m.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var changed = new List<string>();
            int total = 0, missing = 0;

            foreach (var ta in ma.GetTypes())
            {
                foreach (var maMethod in ta.Methods)
                {
                    total++;
                    if (!methodsB.TryGetValue(maMethod.FullName, out var mbMethod))
                    {
                        missing++;
                        changed.Add($"[缺少] {maMethod.FullName}");
                        continue;
                    }

                    var diff = CompareMethod(maMethod, mbMethod);
                    if (diff != null)
                        changed.Add(diff);
                }
            }

            int methodsA = ma.GetTypes().Sum(t => t.Methods.Count);
            Console.WriteLine($"方法数: 原版 {methodsA} / 新版 {methodsB.Count}，比较 {total} 个");
            Console.WriteLine($"指令或异常处理不同的方法: {changed.Count}");
            foreach (var line in changed)
                Console.WriteLine("  " + line);
            Console.WriteLine();

            CompareResources(ma, mb);
            CompareMetadata(ma, mb);

            Console.WriteLine();
            Console.WriteLine(changed.Count == 0 ? "[OK] 两个程序集完全一致" : "[INFO] 差异仅限上面列出的方法");
            return changed.Count == 0 ? 0 : 3;
        }

        /// <summary>返回 null 表示一致，否则返回一行差异摘要。</summary>
        private static string? CompareMethod(MethodDef x, MethodDef y)
        {
            var bx = x.Body;
            var by = y.Body;
            if (bx == null && by == null) return null;
            if (bx == null || by == null) return $"{x.FullName}  (方法体存在性不同)";

            var ix = bx.Instructions;
            var iy = by.Instructions;

            int firstDiff = -1;
            int common = Math.Min(ix.Count, iy.Count);
            for (int i = 0; i < common; i++)
            {
                if (!SameInstruction(ix[i], iy[i], ix, iy))
                {
                    firstDiff = i;
                    break;
                }
            }
            if (firstDiff < 0 && ix.Count != iy.Count)
                firstDiff = common;

            bool ehSame = SameHandlers(bx, by);
            if (firstDiff < 0 && ehSame) return null;

            string detail;
            if (firstDiff >= 0)
            {
                string oldText = firstDiff < ix.Count ? ix[firstDiff].ToString() : "(无)";
                string newText = firstDiff < iy.Count ? iy[firstDiff].ToString() : "(无)";
                detail = $"首条差异 #{firstDiff}: {oldText}  ->  {newText}";
            }
            else
            {
                detail = "仅异常处理表不同";
            }

            return $"{x.FullName}  ({ix.Count} -> {iy.Count} 条)  {detail}";
        }

        private static bool SameInstruction(Instruction a, Instruction b, IList<Instruction> la, IList<Instruction> lb)
        {
            if (a.OpCode != b.OpCode) return false;

            if (a.Operand is Instruction ta && b.Operand is Instruction tb)
                return la.IndexOf(ta) == lb.IndexOf(tb);

            if (a.Operand is Instruction[] aa && b.Operand is Instruction[] ab)
            {
                if (aa.Length != ab.Length) return false;
                for (int i = 0; i < aa.Length; i++)
                    if (la.IndexOf(aa[i]) != lb.IndexOf(ab[i])) return false;
                return true;
            }

            return string.Equals(a.Operand?.ToString(), b.Operand?.ToString(), StringComparison.Ordinal);
        }

        private static bool SameHandlers(CilBody x, CilBody y)
        {
            if (x.ExceptionHandlers.Count != y.ExceptionHandlers.Count) return false;
            for (int i = 0; i < x.ExceptionHandlers.Count; i++)
            {
                var hx = x.ExceptionHandlers[i];
                var hy = y.ExceptionHandlers[i];
                if (hx.HandlerType != hy.HandlerType) return false;
                if (Idx(x, hx.TryStart) != Idx(y, hy.TryStart)) return false;
                if (Idx(x, hx.TryEnd) != Idx(y, hy.TryEnd)) return false;
                if (Idx(x, hx.HandlerStart) != Idx(y, hy.HandlerStart)) return false;
                if (Idx(x, hx.HandlerEnd) != Idx(y, hy.HandlerEnd)) return false;
                if (Idx(x, hx.FilterStart) != Idx(y, hy.FilterStart)) return false;
            }
            return true;
        }

        private static int Idx(CilBody body, Instruction? ins) => ins == null ? -1 : body.Instructions.IndexOf(ins);

        private static void CompareResources(ModuleDef a, ModuleDef b)
        {
            static string Digest(Resource r)
            {
                if (r is not EmbeddedResource er) return "(非嵌入)";
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var s = er.CreateReader().AsStream();
                return Convert.ToHexString(sha.ComputeHash(s));
            }

            var ra = a.Resources.ToDictionary(r => r.Name.ToString(), r => Digest(r), StringComparer.Ordinal);
            var rb = b.Resources.ToDictionary(r => r.Name.ToString(), r => Digest(r), StringComparer.Ordinal);

            Console.WriteLine($"嵌入资源: 原版 {ra.Count} / 新版 {rb.Count}");
            foreach (var kv in ra)
            {
                if (!rb.TryGetValue(kv.Key, out var h))
                    Console.WriteLine($"  [缺少] {kv.Key}");
                else if (h != kv.Value)
                    Console.WriteLine($"  [已改变] {kv.Key}  {kv.Value[..16]}... -> {h[..16]}...");
                else
                    Console.WriteLine($"  [一致] {kv.Key}  SHA256 {kv.Value[..16]}...");
            }
        }

        private static void CompareMetadata(ModuleDef a, ModuleDef b)
        {
            int ta = a.GetTypes().Count(), tb = b.GetTypes().Count();
            int fa = a.GetTypes().Sum(t => t.Fields.Count), fb = b.GetTypes().Sum(t => t.Fields.Count);
            int ra = a.GetAssemblyRefs().Count(), rb = b.GetAssemblyRefs().Count();
            Console.WriteLine($"类型: {ta}/{tb}   字段: {fa}/{fb}   程序集引用: {ra}/{rb}   入口点: {a.EntryPoint?.FullName} / {b.EntryPoint?.FullName}");
        }

        // ------------------------------------------------------------------
        // 补丁后自检：分支目标必须落在同一个方法体内，异常处理区间必须有序。
        // ------------------------------------------------------------------
        private static void VerifyIl(ModuleDef module)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    var body = method.Body;
                    var set = new HashSet<Instruction>(body.Instructions);

                    foreach (var ins in body.Instructions)
                    {
                        switch (ins.Operand)
                        {
                            case Instruction target when !set.Contains(target):
                                throw new Exception($"{type.FullName}::{method.Name} 有越界分支目标 IL_{ins.Offset:X4}");
                            case Instruction[] targets:
                                foreach (var t in targets)
                                    if (!set.Contains(t))
                                        throw new Exception($"{type.FullName}::{method.Name} 有越界 switch 目标");
                                break;
                        }
                    }

                    foreach (var handler in body.ExceptionHandlers)
                    {
                        if (handler.TryStart == null || handler.HandlerStart == null)
                            throw new Exception($"{type.FullName}::{method.Name} 异常处理区间缺少起点");
                        if (handler.TryEnd != null && !set.Contains(handler.TryEnd))
                            throw new Exception($"{type.FullName}::{method.Name} TryEnd 越界");
                        if (handler.HandlerEnd != null && !set.Contains(handler.HandlerEnd))
                            throw new Exception($"{type.FullName}::{method.Name} HandlerEnd 越界");
                    }
                }
            }
        }
    }
}










