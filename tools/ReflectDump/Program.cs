using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// 转储本机 TaleWorlds / RBM 程序集中匹配关键词的类型的全部成员(public + nonpublic)。
// 输出到 tools\dump\reflect.txt;只读元数据,绝不执行目标代码。

string game = @"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord";
string gameBin = Path.Combine(game, @"bin\Win64_Shipping_Client");
string harmonyBin = Path.Combine(game, @"Modules\Bannerlord.Harmony\bin\Win64_Shipping_Client");
string rbmBin = Path.Combine(game, @"Modules\RBM\bin\Win64_Shipping_Client");
string sandboxBin = Path.Combine(game, @"Modules\SandBox\bin\Win64_Shipping_Client");
string sandboxCoreBin = Path.Combine(game, @"Modules\SandBoxCore\bin\Win64_Shipping_Client");

string[] targets =
{
    Path.Combine(gameBin, "TaleWorlds.MountAndBlade.dll"),
    Path.Combine(gameBin, "TaleWorlds.Core.dll"),
    Path.Combine(sandboxBin, "SandBox.dll"),
    Path.Combine(sandboxCoreBin, "SandBoxCore.dll"),
    Path.Combine(rbmBin, "RBMAI.dll"),
    Path.Combine(rbmBin, "RBMCombat.dll"),
    Path.Combine(rbmBin, "RBMConfig.dll"),
};

string[] keywords = args.Length > 0
    ? args
    : new[] { "Agent", "Formation", "Mission", "Team", "Morale", "Panic", "Rout", "Charge", "Tactic", "BattleEnd", "Spawn", "StopRetreat" };

var resolverPaths = new List<string>();
// gameBin 先加,确保核心 TaleWorlds.* 以游戏本体那份为准(各模块可能带副本)
foreach (var d in new[] { gameBin, harmonyBin, rbmBin })
    if (Directory.Exists(d)) resolverPaths.AddRange(Directory.GetFiles(d, "*.dll"));
// 再加所有模块 bin(SandBox/SandBoxCore/StoryMode/CustomBattle 等签名里引用的程序集)
string modulesDir = Path.Combine(game, "Modules");
if (Directory.Exists(modulesDir))
    foreach (var sub in Directory.GetDirectories(modulesDir))
    {
        string b = Path.Combine(sub, @"bin\Win64_Shipping_Client");
        if (Directory.Exists(b)) resolverPaths.AddRange(Directory.GetFiles(b, "*.dll"));
    }
string coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
resolverPaths.AddRange(Directory.GetFiles(coreDir, "*.dll"));
resolverPaths = resolverPaths.GroupBy(Path.GetFileName).Select(g => g.First()).ToList();

var resolver = new PathAssemblyResolver(resolverPaths);
MetadataLoadContext mlc = null;
foreach (var core in new[] { "netstandard", "mscorlib", "System.Private.CoreLib" })
{
    try { mlc = new MetadataLoadContext(resolver, core); break; }
    catch { /* try next core */ }
}
if (mlc == null) mlc = new MetadataLoadContext(resolver);

var sb = new StringBuilder();
int matched = 0;
using (mlc)
{
    foreach (var tp in targets)
    {
        if (!File.Exists(tp)) { sb.AppendLine($"### MISSING {tp}"); continue; }
        Assembly asm;
        try { asm = mlc.LoadFromAssemblyPath(tp); }
        catch (Exception e) { sb.AppendLine($"### LOAD ERR {tp}: {e.Message}"); continue; }

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException re) { types = re.Types.Where(t => t != null).ToArray(); }

        foreach (var t in types.Where(t => t != null && Match(t, keywords)).OrderBy(t => t.FullName))
        {
            DumpType(sb, t);
            matched++;
        }
    }
}

string outPath = @"C:\Users\rangt\Documents\GitHub\workspace\tools\dump\reflect.txt";
Directory.CreateDirectory(Path.GetDirectoryName(outPath));
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"done: {matched} types -> {outPath}");

static bool Match(Type t, string[] kw)
{
    string n = t.FullName ?? t.Name;
    return kw.Any(k => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
}

static void DumpType(StringBuilder sb, Type t)
{
    sb.AppendLine();
    sb.AppendLine($"================ {t.FullName}  (asm={t.Assembly.GetName().Name}) ================");
    sb.AppendLine($"  base: {Safe(() => t.BaseType?.FullName)}");
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    try
    {
        foreach (var f in t.GetFields(BF).OrderBy(x => x.Name))
            sb.AppendLine($"  field  {(f.IsStatic ? "static " : "")}{Safe(() => f.FieldType.Name)} {f.Name}");
    }
    catch (Exception e) { sb.AppendLine($"  <fields err: {e.Message}>"); }

    try
    {
        foreach (var p in t.GetProperties(BF).OrderBy(x => x.Name))
            sb.AppendLine($"  prop   {Safe(() => p.PropertyType.Name)} {p.Name} {{{(p.CanRead ? "get;" : "")}{(p.CanWrite ? "set;" : "")}}}");
    }
    catch (Exception e) { sb.AppendLine($"  <props err: {e.Message}>"); }

    try
    {
        foreach (var m in t.GetMethods(BF).OrderBy(x => x.Name))
        {
            string ps = Safe(() => string.Join(", ", m.GetParameters().Select(p => $"{Safe(() => p.ParameterType.Name)} {p.Name}")));
            sb.AppendLine($"  method {(m.IsStatic ? "static " : "")}{Safe(() => m.ReturnType.Name)} {m.Name}({ps})");
        }
    }
    catch (Exception e) { sb.AppendLine($"  <methods err: {e.Message}>"); }
}

static string Safe(Func<string> f)
{
    try { return f() ?? "?"; } catch { return "?"; }
}
