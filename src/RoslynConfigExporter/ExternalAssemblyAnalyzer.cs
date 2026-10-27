using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynConfigExporter;

// Reads method bodies only: no constructors, property getters or methods are invoked.
internal static class ExternalAssemblyAnalyzer
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(c => c.Value);

    public static void Analyze(string project, Compilation compilation, List<ConfigurationUsage> usages,
        List<ProjectIssue> issues, List<EnvironmentSource> environmentSources, CancellationToken cancellationToken)
    {
        var paths = compilation.References.OfType<PortableExecutableReference>().Select(r => r.FilePath)
            .OfType<string>().Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var context = new InspectionLoadContext(paths);
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("System.", StringComparison.Ordinal) ||
                    name.StartsWith("Microsoft.Extensions.Configuration", StringComparison.Ordinal) ||
                    path.Contains(Path.DirectorySeparatorChar + "packs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    name is "System" or "mscorlib" or "netstandard") continue;
                try
                {
                    if (name == "Serilog.Settings.Configuration") continue; // Public readers use API summaries, including calls from other DLLs.
                    var assembly = Load(context, path);
                    if (!assembly.GetReferencedAssemblies().Any(a => a.Name?.StartsWith("Microsoft.Extensions.Configuration") == true)) continue;
                    // Seed external calls with the sections supplied by application source.
                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        var model = compilation.GetSemanticModel(tree);
                        var evaluator = new KeyEvaluator(model);
                        foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation) continue;
                            var symbol = operation.TargetMethod;
                            if (symbol.ContainingAssembly.Name != assembly.GetName().Name) continue;
                            var typeName = symbol.ContainingType.ToDisplayString();
                            var type = assembly.GetType(typeName);
                            if (type is null) continue;
                            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                                .Where(m => m.Name == symbol.MetadataName && m.GetParameters().Length == symbol.Parameters.Length &&
                                    m.GetGenericArguments().Length == symbol.TypeParameters.Length &&
                                    m.GetParameters().Zip(symbol.Parameters).All(p => p.First.ParameterType.Name == p.Second.Type.MetadataName)).ToArray();
                            if (candidates.Length != 1) continue; // Never select an ambiguous overload.
                            var arguments = new Value[symbol.Parameters.Length];
                            for (var n = 0; n < arguments.Length; n++) arguments[n] = new();
                            foreach (var argument in operation.Arguments)
                            {
                                if (argument.Parameter is null || argument.Value.Syntax is not ExpressionSyntax expression) continue;
                                if (argument.Value.Type.IsConfiguration())
                                {
                                    var prefix = evaluator.ResolveConfigurationPrefix(expression);
                                    arguments[argument.Parameter.Ordinal] = new(Section: prefix.Confidence == Confidence.Exact ? prefix.Text : UnknownParent);
                                }
                                else if (argument.Value.ConstantValue is { HasValue: true, Value: string text })
                                    arguments[argument.Parameter.Ordinal] = new(Text: text);
                            }
                            Scan(project, path, candidates[0], usages, arguments, new(), new HashSet<MethodBase>(), new Value(), environmentSources, false, cancellationToken);
                        }
                    }
                    foreach (var type in assembly.GetTypes())
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                            .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try { Scan(project, path, method, usages, [], new(), new HashSet<MethodBase>(), new Value(), environmentSources, true, cancellationToken); }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            { issues.Add(new(project, "warning", $"Cannot inspect {type.FullName}.{method.Name}: {ex.Message}")); }
                        }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { issues.Add(new(project, "warning", $"Cannot inspect external assembly {path}: {ex.Message}")); }
            }
        }
        finally { context.Unload(); }
    }

    private sealed class InspectionLoadContext(string[] paths) : AssemblyLoadContext("Configuration inspection", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name is "System" or "mscorlib" or "netstandard") return null;
            var path = paths.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), name.Name, StringComparison.OrdinalIgnoreCase));
            if (path is null) return null;
            try { return ExternalAssemblyAnalyzer.Load(this, path); }
            catch (BadImageFormatException) { return null; } // Reference assembly: allow the runtime fallback.
        }
    }

    private static Assembly Load(AssemblyLoadContext context, string path)
    {
        var existing = context.Assemblies.FirstOrDefault(a => a.GetName().Name == Path.GetFileNameWithoutExtension(path));
        if (existing is not null) return existing;
        using var stream = File.OpenRead(path);
        return context.LoadFromStream(stream);
    }

    internal const string UnknownParent = "{unknown-parent}";
    private sealed record Value(string? Text = null, string? Section = null, bool IsNull = false)
    {
        public Dictionary<FieldInfo, Value> Fields { get; } = new();
    }

    private static Value Scan(string project, string path, MethodBase method, List<ConfigurationUsage> usages,
        Value[] arguments, Value instance, HashSet<MethodBase> active, Value staticFields, List<EnvironmentSource> environmentSources, bool baseline, CancellationToken cancellationToken)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null || active.Count >= 12 || !active.Add(method)) return new();
        try
        {
            var stack = new Stack<Value>();
            var locals = new Dictionary<int, Value>();
            Value Pop() => stack.Count > 0 ? stack.Pop() : new();
            for (var i = 0; i < il.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = i;
                var code = Codes[il[i] == 0xfe ? (short)(0xfe00 | il[++i]) : il[i]];
                i++;
                var operand = i;
                i += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4
                };
                if (code == OpCodes.Ldstr) { stack.Push(new(method.Module.ResolveString(BitConverter.ToInt32(il, operand)))); continue; }
                var localIndex = code.Name switch
                {
                    "ldloc.0" or "stloc.0" => 0,
                    "ldloc.1" or "stloc.1" => 1,
                    "ldloc.2" or "stloc.2" => 2,
                    "ldloc.3" or "stloc.3" => 3,
                    "ldloc.s" or "stloc.s" => il[operand],
                    "ldloc" or "stloc" => BitConverter.ToUInt16(il, operand),
                    _ => -1
                };
                if (localIndex >= 0)
                {
                    if (code.Name!.StartsWith("stloc", StringComparison.Ordinal)) locals[localIndex] = Pop();
                    else stack.Push(locals.GetValueOrDefault(localIndex) ?? new());
                    continue;
                }
                var argumentIndex = code.Name switch
                {
                    "ldarg.0" => 0,
                    "ldarg.1" => 1,
                    "ldarg.2" => 2,
                    "ldarg.3" => 3,
                    "ldarg.s" => il[operand],
                    "ldarg" => BitConverter.ToUInt16(il, operand),
                    _ => -1
                };
                if (argumentIndex >= 0)
                {
                    stack.Push(!method.IsStatic && argumentIndex == 0 ? instance :
                        arguments.ElementAtOrDefault(argumentIndex - (method.IsStatic ? 0 : 1)) ?? new());
                    continue;
                }
                if (code == OpCodes.Ldnull) { stack.Push(new(IsNull: true)); continue; }
                if (code == OpCodes.Ldsfld || code == OpCodes.Stsfld)
                {
                    var field = method.Module.ResolveField(BitConverter.ToInt32(il, operand), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                    if (code == OpCodes.Stsfld) staticFields.Fields[field] = Pop();
                    else
                    {
                        if (!staticFields.Fields.ContainsKey(field) && field.DeclaringType?.TypeInitializer is { } initializer && initializer.Module == method.Module)
                            Scan(project, path, initializer, usages, [], new(), active, staticFields, environmentSources, baseline, cancellationToken);
                        stack.Push(staticFields.Fields.GetValueOrDefault(field) ?? new());
                    }
                    continue;
                }
                if (code == OpCodes.Ldfld || code == OpCodes.Stfld)
                {
                    var field = method.Module.ResolveField(BitConverter.ToInt32(il, operand), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                    if (code == OpCodes.Stfld) { var value = Pop(); Pop().Fields[field] = value; }
                    else stack.Push(Pop().Fields.GetValueOrDefault(field) ?? new());
                    continue;
                }
                if (code == OpCodes.Ret) return Pop();
                if (code == OpCodes.Dup) { var value = Pop(); stack.Push(value); stack.Push(value); continue; }
                if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
                {
                    var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, operand), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                    var parameters = called.GetParameters();
                    var args = new Value[parameters.Length];
                    for (var n = args.Length - 1; n >= 0; n--) args[n] = Pop();
                    var receiver = code == OpCodes.Newobj ? new Value() : !called.IsStatic ? Pop() : args.FirstOrDefault() ?? new();
                    var owner = called.DeclaringType?.FullName;
                    if (owner == "Microsoft.Extensions.Configuration.EnvironmentVariablesExtensions" && called.Name == "AddEnvironmentVariables")
                    {
                        var prefixIndex = Array.FindIndex(parameters, p => p.Name == "prefix");
                        var prefix = prefixIndex >= 0 ? args[prefixIndex].IsNull ? "" : args[prefixIndex].Text : parameters.Length == 1 ? "" : null;
                        // A standalone scan must not replace evidence from an application call with an unknown argument.
                        var location = new SourceLocation(path, method.MetadataToken, offset);
                        if (!baseline || !environmentSources.Any(s => s.Project == project && s.Location == location))
                            environmentSources.Add(new(project, prefix, location));
                    }
                    var loggingCall = false;
                    if (owner == LoggingConfigurationAnalyzer.SerilogApi && called.Name is "Configuration" or "ConfigurationSection")
                    {
                        var configIndex = Array.FindIndex(parameters, p => p.ParameterType.FullName is "Microsoft.Extensions.Configuration.IConfiguration" or "Microsoft.Extensions.Configuration.IConfigurationSection");
                        var sectionIndex = Array.FindIndex(parameters, p => p.Name == "sectionName");
                        var optionsIndex = Array.FindIndex(parameters, p => p.Name == "readerOptions");
                        var section = called.Name == "ConfigurationSection" ? "" : "Serilog";
                        if (sectionIndex >= 0) section = args[sectionIndex].Text ?? UnknownParent;
                        else if (optionsIndex >= 0 && !args[optionsIndex].IsNull)
                            section = args[optionsIndex].Fields.FirstOrDefault(f => f.Key.Name == "<SectionName>k__BackingField").Value?.Text ?? UnknownParent;
                        var prefix = configIndex >= 0 ? args[configIndex].Section ?? UnknownParent : UnknownParent;
                        LoggingConfigurationAnalyzer.Add(project, LoggingConfigurationAnalyzer.Join(prefix, section), true,
                            new(path, 0, offset), $"{method.DeclaringType?.FullName}.{method.Name} IL_{offset:x4}", usages);
                        loggingCall = true;
                    }
                    else if (owner == LoggingConfigurationAnalyzer.MicrosoftApi && called.Name == "AddConfiguration" && args.Length == 2)
                    {
                        LoggingConfigurationAnalyzer.Add(project, args[1].Section ?? UnknownParent, false,
                            new(path, 0, offset), $"{method.DeclaringType?.FullName}.{method.Name} IL_{offset:x4}", usages);
                        loggingCall = true;
                    }
                    if (owner == KestrelConfigurationAnalyzer.Api && called.Name == "Configure" && args.Length >= 1)
                    {
                        KestrelConfigurationAnalyzer.Add(project, args[0].Section ?? UnknownParent,
                            new(path, 0, offset), $"{method.DeclaringType?.FullName}.{method.Name} IL_{offset:x4}", usages);
                        loggingCall = true;
                    }
                    var known = owner is "Microsoft.Extensions.Configuration.IConfiguration" or "Microsoft.Extensions.Configuration.IConfigurationSection"
                        or "Microsoft.Extensions.Configuration.ConfigurationBinder" or "Microsoft.Extensions.Configuration.ConfigurationExtensions";
                    Value result = new();
                    if (known && called.Name is "get_Item" or "GetValue" or "GetSection" or "GetRequiredSection" or "GetConnectionString")
                    {
                        var keyIndex = Array.FindIndex(parameters, p => p.ParameterType == typeof(string));
                        var key = keyIndex >= 0 ? args[keyIndex].Text : null;
                        var prefix = receiver.Section ?? UnknownParent;
                        if (called.Name == "GetConnectionString") prefix = Join(prefix, "ConnectionStrings");
                        var fullKey = Join(prefix, key ?? "{dynamic}");
                        var kind = called.Name == "get_Item" ? UsageKind.Indexer : Enum.Parse<UsageKind>(called.Name);
                        usages.Add(new(project, fullKey, kind, fullKey.Contains('{', StringComparison.Ordinal) ? Confidence.Dynamic : Confidence.Inferred,
                            (called as MethodInfo)?.ReturnType.ToString(), null, new(path, 0, offset),
                            $"{method.DeclaringType?.FullName}.{method.Name} IL_{offset:x4}: {called.Name}",
                            "Detected by reflection in external IL; reachability is not established.",
                            Metadata: new(called is MethodInfo returnMethod ? Nullable.GetUnderlyingType(returnMethod.ReturnType) is not null || new NullabilityInfoContext().Create(returnMethod.ReturnParameter).ReadState == NullabilityState.Nullable : null, false, null, null, null), Origin: UsageOrigin.Assembly,
                            IsOpenGeneric: (called as MethodInfo)?.ReturnType.ContainsGenericParameters == true));
                        if (kind is UsageKind.GetSection or UsageKind.GetRequiredSection) result = new(Section: fullKey);
                    }
                    else if (!loggingCall && called.Module == method.Module && !called.ContainsGenericParameters)
                    {
                        result = Scan(project, path, called, usages, args, receiver, active, staticFields, environmentSources, baseline, cancellationToken);
                    }
                    if (code == OpCodes.Newobj) result = receiver;
                    if (code == OpCodes.Newobj || called is MethodInfo info && info.ReturnType != typeof(void)) stack.Push(result);
                    continue;
                }
                // Forget values at control-flow boundaries; never guess across branches.
                if (code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
                { stack.Clear(); locals.Clear(); continue; }
                var pops = code.StackBehaviourPop.ToString();
                if (pops != "Pop0") for (var n = 0; n < pops.Split('_').Length; n++) Pop();
                var pushes = code.StackBehaviourPush.ToString();
                if (pushes != "Push0") for (var n = 0; n < pushes.Split('_').Length; n++) stack.Push(new());
            }
            return new();
        }
        finally { active.Remove(method); }
    }
    private static string Join(string prefix, string key) => string.IsNullOrEmpty(prefix) ? key : prefix + ":" + key;
}
