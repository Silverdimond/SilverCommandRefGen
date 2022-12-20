using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CommandLine;
using DotNet.CodeAnalysis;
using Markdown;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SilverCommandRefGen;
public class ActionInputs
{
    string _repositoryName = null!;
    string _branchName = null!;

    [Option('o', "owner",
        Required = true,
        HelpText = "The owner, for example: \"dotnet\". Assign from `github.repository_owner`.")]
    public string Owner { get; set; } = null!;

    [Option('n', "name",
        Required = true,
        HelpText = "The repository name, for example: \"samples\". Assign from `github.repository`.")]
    public string Name
    {
        get => _repositoryName;
        set => ParseAndAssign(value, str => _repositoryName = str);
    }

    [Option('b', "branch",
        Required = true,
        HelpText = "The branch name, for example: \"refs/heads/main\". Assign from `github.ref`.")]
    public string Branch
    {
        get => _branchName;
        set => ParseAndAssign(value, str => _branchName = str);
    }

    [Option('d', "dir",
        Required = true,
        HelpText = "The root directory to start recursive searching from.")]
    public string Directory { get; set; } = null!;

    [Option('w', "workspace",
        Required = true,
        HelpText = "The workspace directory, or repository root directory.")]
    public string WorkspaceDirectory { get; set; } = null!;

    static void ParseAndAssign(string? value, Action<string>? assign)
    {
        if (value is { Length: > 0 } && assign is not null)
        {
            assign(value.Split("/")[^1]);
        }
    }
}
static class CodeAnalysisMetricDataExtensions
{
    internal static string ToCyclomaticComplexityEmoji(this CodeAnalysisMetricData metric) =>
        metric.CyclomaticComplexity switch
        {
            >= 0 and <= 7 => ":heavy_check_mark:",  // ✔️
            8 or 9 => ":warning:",                  // ⚠️
            10 or 11 => ":radioactive:",            // ☢️
            >= 12 and <= 14 => ":x:",               // ❌
            _ => ":exploding_head:"                 // 🤯
        };

    internal static int CountNamespaces(this CodeAnalysisMetricData metric) =>
        metric.CountKind(SymbolKind.Namespace);

    internal static int CountNamedTypes(this CodeAnalysisMetricData metric) =>
        metric.CountKind(SymbolKind.NamedType);

    static int CountKind(this CodeAnalysisMetricData metric, SymbolKind kind) =>
        metric.Children
            .Flatten(child => child.Children)
            .Count(child => child.Symbol.Kind == kind);

    internal static (int Complexity, string Emoji) FindHighestCyclomaticComplexity(
        this CodeAnalysisMetricData metric) =>
        metric.Children
            .Flatten(child => child.Children)
            .Where(child =>
                child.Symbol.Kind is not SymbolKind.Assembly
                and not SymbolKind.Namespace
                and not SymbolKind.NamedType)
            .Select(m => (Metric: m, m.CyclomaticComplexity))
            .OrderByDescending(_ => _.CyclomaticComplexity)
            .Select(_ => (_.CyclomaticComplexity, _.Metric.ToCyclomaticComplexityEmoji()))
            .FirstOrDefault();

    static IEnumerable<TSource> Flatten<TSource>(
        this IEnumerable<TSource> parent, Func<TSource, IEnumerable<TSource>> childSelector) =>
        parent.SelectMany(
            source => childSelector(source).Flatten(childSelector))
            .Concat(parent);

    internal static string ToMermaidClassDiagram(
        this CodeAnalysisMetricData classMetric, string className)
    {
        // https://mermaid-js.github.io/mermaid/#/classDiagram
        StringBuilder builder = new("classDiagram");
        builder.AppendLine();

        className = className.Contains(".")
            ? className.Substring(className.IndexOf(".", StringComparison.Ordinal) + 1)
            : className;

        if (classMetric.Symbol is ITypeSymbol typeSymbol &&
            typeSymbol.Interfaces.Length > 0)
        {
            foreach (var @interface in typeSymbol.Interfaces)
            {
                if (@interface.IsGenericType)
                {
                    var typeArgs = string.Join(",", @interface.TypeArguments.Select(ta => ta.Name));
                    var name = $"{@interface.Name}~{typeArgs}~";
                    builder.AppendLine($"{name} <|-- {className} : implements");
                }
                else
                {
                    var name = @interface.Name;
                    builder.AppendLine($"{name} <|-- {className} : implements"); 
                }
            }
        }

        builder.AppendLine($"class {className}{{");

        static string? ToClassifier(CodeAnalysisMetricData member) =>
            (member.Symbol.IsStatic, member.Symbol.IsAbstract) switch
            {
                (true, _) => "$", (_, true) => "*", _ => null
            };

        static string ToAccessModifier(CodeAnalysisMetricData member)
        {
            // TODO: figure out how to get access modifiers.
            // + Public
            // - Private
            // # Protected
            // ~ Package / Internal

            return member.Symbol switch
            {
                IFieldSymbol field => "-",
                IPropertySymbol prop => "+",
                IMethodSymbol method => "+",
                _ => "+"
            };
        }

        static string ToMemberName(CodeAnalysisMetricData member, string className)
        {
            var accessModifier = ToAccessModifier(member);
            if (member.Symbol.Kind is SymbolKind.Method)
            {
                var method = member.ToDisplayName();
                var ctorMethod = $"{className}.{className}";
                if (method.StartsWith(ctorMethod))
                {
                    var ctor = method.Substring(ctorMethod.Length);
                    return $"{accessModifier}.ctor{ctor} {className}";
                }

                if (member.Symbol is IMethodSymbol methodSymbol)
                {
                    var rtrnType = methodSymbol.ReturnType.ToString()!;
                    if (rtrnType.Contains("."))
                    {
                        goto regex;
                    }

                    var classNameOffset = className.Contains(".")
                        ? className[className.IndexOf(".", StringComparison.Ordinal)..].Length - 1
                        : className.Length;

                    var index = rtrnType.Length + 2 + classNameOffset;
                    var methodSignature = method[index..];
                    return $"{accessModifier}{methodSignature}{ToClassifier(member)} {rtrnType}";
                }

            regex:
                Regex returnType = new(@"^(?<returnType>[\S]+)");
                if (returnType.Match(method) is { Success: true } match)
                {
                    // 2 is hardcoded for the space and "." characters
                    var index = method.IndexOf(" ", StringComparison.Ordinal) + 2 + className.Length;
                    var methodSignature = method[index..];
                    return $"{accessModifier}{methodSignature}{ToClassifier(member)} {match.Groups["returnType"]}";
                }
            }

            return $"{accessModifier}{member.ToDisplayName().Replace($"{className}.", "")}";
        }

        foreach (var member
            in classMetric.Children.OrderBy(
               m => m.Symbol.Kind switch
               {
                   SymbolKind.Field => 1,
                   SymbolKind.Property => 2,
                   SymbolKind.Method => 3,
                   _ => 4
               }))
        {
            _ = member.Symbol.Kind switch
            {
                SymbolKind.Field => builder.AppendLine(
                    $"    {ToMemberName(member, className)}{ToClassifier(member)}"),

                SymbolKind.Property => builder.AppendLine(
                    $"    {ToMemberName(member, className)}{ToClassifier(member)}"),

                SymbolKind.Method => builder.AppendLine(
                    $"    {ToMemberName(member, className)}"),

                _ => null
            };
        }

        builder.AppendLine("}");

        var mermaidCode = builder.ToString();
            //.Replace("<", "~")
            //.Replace(">", "~");

        return mermaidCode;
    }

    internal static string ToDisplayName(this CodeAnalysisMetricData metric) =>
        metric.Symbol.Kind switch
        {
            SymbolKind.Assembly => metric.Symbol.Name,

            SymbolKind.NamedType => DisplayName(metric.Symbol),

            SymbolKind.Method
            or SymbolKind.Field
            or SymbolKind.Event
            or SymbolKind.Property => metric.Symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),

            _ => metric.Symbol.ToDisplayString()
        };

    static string DisplayName(ISymbol symbol)
    {
        StringBuilder minimalTypeName =
            new(
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        var containingType = symbol.ContainingType;
        while (containingType is not null)
        {
            minimalTypeName.Insert(0, ".");
            minimalTypeName.Insert(0,
                containingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            containingType = containingType.ContainingType;
        }

        return minimalTypeName.ToString();
    }
}


static class CodeMetricsReportExtensions
{
    internal static string ToMarkDownBody(
        this Dictionary<string, CodeAnalysisMetricData> metricData,
        ActionInputs actionInputs)
    {
        MarkdownDocument document = new();

        DisableMarkdownLinterAndCaptureConfig(document);

        document.AppendHeader("Code Metrics", 1);

        document.AppendParagraph(
            $"This file is dynamically maintained by a bot, *please do not* edit this by hand. It represents various [code metrics](https://aka.ms/dotnet/code-metrics), such as cyclomatic complexity, maintainability index, and so on.");

        List<(string Id, string ClassName, string MermaidCode)> classDiagrams = new();
        foreach ((string filePath, CodeAnalysisMetricData assemblyMetric)
            in metricData.OrderBy(md => md.Key))
        {
            var (assemblyId, assemblyDisplayName, assemblyLink, assemblyHighestComplexity) =
                ToIdAndAnchorPair(assemblyMetric);

            document.AppendParagraph($"<div id='{assemblyId}'></div>");
            document.AppendHeader($"{assemblyDisplayName} {assemblyHighestComplexity.emoji}", 2);

            document.AppendParagraph(
                $"The *{Path.GetFileName(filePath)}* project file contains:");

            static string FormatComplexity((int complexity, string emoji) highestComplexity) =>
                $"{highestComplexity.complexity} {highestComplexity.emoji}";

            document.AppendList(
                new MarkdownTextListItem($"{assemblyMetric.CountNamespaces():#,0} namespaces."),
                new MarkdownTextListItem($"{assemblyMetric.CountNamedTypes():#,0} named types."),
                new MarkdownTextListItem($"{assemblyMetric.SourceLines:#,0} total lines of source code."),
                new MarkdownTextListItem($"Approximately {assemblyMetric.ExecutableLines:#,0} lines of executable code."),
                new MarkdownTextListItem($"The highest cyclomatic complexity is {FormatComplexity(assemblyHighestComplexity)}."));

            foreach (var namespaceMetric
                in assemblyMetric.Children.Where(child => child.Symbol.Kind == SymbolKind.Namespace)
                .OrderBy(md => md.Symbol.Name))
            {
                var (namespaceId, namespaceSymbolName, namespaceLink, namespaceHighestComplexity) =
                    ToIdAndAnchorPair(namespaceMetric);
                OpenCollapsibleSection(
                    document, namespaceId, namespaceSymbolName, namespaceHighestComplexity.emoji);

                document.AppendParagraph(
                    $"The `{namespaceSymbolName}` namespace contains {namespaceMetric.Children.Length} named types.");

                document.AppendList(
                    new MarkdownTextListItem($"{namespaceMetric.CountNamedTypes():#,0} named types."),
                    new MarkdownTextListItem($"{namespaceMetric.SourceLines:#,0} total lines of source code."),
                    new MarkdownTextListItem($"Approximately {namespaceMetric.ExecutableLines:#,0} lines of executable code."),
                    new MarkdownTextListItem($"The highest cyclomatic complexity is {FormatComplexity(namespaceHighestComplexity)}."));

                foreach (var classMetric in namespaceMetric.Children
                    .OrderBy(md => md.Symbol.Name))
                {
                    var (classId, classSymbolName, _, namedTypeHighestComplexity) = ToIdAndAnchorPair(classMetric);
                    OpenCollapsibleSection(
                        document, classId, classSymbolName, namedTypeHighestComplexity.emoji);

                    document.AppendList(
                        new MarkdownTextListItem($"The `{classSymbolName}` contains {classMetric.Children.Length} members."),
                        new MarkdownTextListItem($"{classMetric.SourceLines:#,0} total lines of source code."),
                        new MarkdownTextListItem($"Approximately {classMetric.ExecutableLines:#,0} lines of executable code."),
                        new MarkdownTextListItem($"The highest cyclomatic complexity is {FormatComplexity(namedTypeHighestComplexity)}."));

                    MarkdownTableHeader tableHeader = new(
                        new("Member kind", MarkdownTableTextAlignment.Center),
                        new("Line number", MarkdownTableTextAlignment.Center),
                        new("Maintainability index", MarkdownTableTextAlignment.Center),
                        new("Cyclomatic complexity", MarkdownTableTextAlignment.Center),
                        new("Depth of inheritance", MarkdownTableTextAlignment.Center),
                        new("Class coupling", MarkdownTableTextAlignment.Center),
                        new("Lines of source / executable code", MarkdownTableTextAlignment.Center));

                    List<MarkdownTableRow> rows = new();
                    foreach (var memberMetric in classMetric.Children.OrderBy(md => md.Symbol.Name))
                    {
                        rows.Add(ToTableRowFrom(memberMetric, actionInputs));
                    }

                    document.AppendTable(tableHeader, rows);

                    if (classSymbolName is not "<Program>$")
                    {
                        var encodedName = HttpUtility.HtmlEncode(classSymbolName);
                        var id = $"{encodedName}-class-diagram";
                        var linkToClassDiagram = $"<a href=\"#{id}\">:link: to `{encodedName}` class diagram</a>";
                        document.AppendParagraph(linkToClassDiagram);
                        classDiagrams.Add((id, classSymbolName, classMetric.ToMermaidClassDiagram(classSymbolName)));
                    }

                    document.AppendParagraph(namespaceLink); // Links back to the parent namespace in the MD doc

                    CloseCollapsibleSection(document);
                }

                CloseCollapsibleSection(document);
            }

            document.AppendParagraph(assemblyLink); // Links back to the parent assembly in the MD doc
        }

        AppendMetricDefinitions(document);
        AppendMermaidClassDiagrams(document, classDiagrams);
        AppendMaintainedByBotMessage(document);
        RestoreMarkdownLinter(document);

        return document.ToString();
    }

    static void AppendMetricDefinitions(MarkdownDocument document)
    {
        document.AppendHeader("Metric definitions", 2);

        MarkdownList markdownList = new();
        foreach ((string columnHeader, string defintion)
            in new[]
            {
                ("Maintainability index", "Measures ease of code maintenance. 🧽 ⬆ Higher values are better."),
                ("Cyclomatic complexity", "Measures the number of branches. 🌱 ⬇️ Lower values are better."),
                ("Depth of inheritance", "Measures length of object inheritance hierarchy. 🇿 ⬇️ Lower values are better."),
                ("Class coupling", "Measures the number of classes that are referenced.🇨 🇨 ⬇️ Lower values are better."),
                ("Lines of source code", "Exact number of lines of source code. 🇱 🇴 🇨 ⬇️ Lower values are better."),
                ("Lines of executable code", "Approximates the lines of executable code. 🇱 🇴 🇪 🇨 ⬇️ Lower values are better.")
            })
        {
            MarkdownText header = new($"**{columnHeader}**");
            MarkdownText text = new(defintion);
            markdownList.AddItem($"{header}: {text}");
        }
        document.AppendList(markdownList);
    }

    static void AppendMermaidClassDiagrams(
        MarkdownDocument document, List<(string Id, string Class, string MermaidCode)> diagrams)
    {
        document.AppendHeader("Mermaid class diagrams", 2);

        foreach (var (id, className, code) in diagrams)
        {
            document.AppendParagraph($"<div id=\"{id}\"></div>");
            document.AppendHeader($"`{className}` class diagram", 5);
            document.AppendCode("mermaid", code);
        }
    }

    static void AppendMaintainedByBotMessage(MarkdownDocument document) =>
        document.AppendParagraph(
            new MarkdownEmphasis("**This file is maintained by a github action.**"));

    static MarkdownTableRow ToTableRowFrom(
        CodeAnalysisMetricData metric,
        ActionInputs actionInputs)
    {
        var lineNumberUrl = ToLineNumberUrl(metric.Symbol, metric.ToDisplayName(), actionInputs);
        var maintainability = metric.MaintainabilityIndex.ToString(CultureInfo.InvariantCulture);
        var cyclomaticComplexity = metric.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture);
        var complexityCell = $"{cyclomaticComplexity} {metric.ToCyclomaticComplexityEmoji()}";
        var depthOfInheritance = metric.DepthOfInheritance.GetValueOrDefault().ToString(CultureInfo.InvariantCulture);
        var classCoupling = metric.CoupledNamedTypes.Count.ToString(CultureInfo.InvariantCulture);
        var linesOfCode =
            $"{metric.SourceLines:#,0} / {metric.ExecutableLines:#,0}";

        return new(
            metric.Symbol.Kind.ToString(),
            lineNumberUrl,
            maintainability,
            complexityCell,
            depthOfInheritance,
            classCoupling,
            linesOfCode);
    }

    static (string elementId, string displayName, string anchorLink, (int highestComplexity, string emoji)) ToIdAndAnchorPair(
        CodeAnalysisMetricData metric)
    {
        var displayName = metric.ToDisplayName();
        var id = PrepareElementId(displayName);
        var highestComplexity = metric.FindHighestCyclomaticComplexity();
        var anchorLink = $"<a href=\"#{id}\">:top: back to {HttpUtility.HtmlEncode(displayName)}</a>";

        return (id, displayName, anchorLink, (highestComplexity.Complexity, highestComplexity.Emoji ?? ":question:"));
    }

    static IMarkdownDocument OpenCollapsibleSection(
        IMarkdownDocument document, string elementId, string symbolName, string highestComplexity) =>
        document.AppendParagraph($@"<details>
<summary>
  <strong id=""{PrepareElementId(elementId)}"">
    {HttpUtility.HtmlEncode(symbolName)} {highestComplexity}
  </strong>
</summary>
<br>");

    static string PrepareElementId(string value) =>
        value.ToLower()
            .Replace('.', '-')
            .Replace("<", "")
            .Replace(">", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace(' ', '+');

    static IMarkdownDocument CloseCollapsibleSection(IMarkdownDocument document) =>
        document.AppendParagraph("</details>");

    static IMarkdownDocument DisableMarkdownLinterAndCaptureConfig(
        IMarkdownDocument document) =>
        document.AppendParagraph(@"<!-- markdownlint-capture -->
<!-- markdownlint-disable -->");

    static IMarkdownDocument RestoreMarkdownLinter(
        IMarkdownDocument document) =>
        document.AppendParagraph(@"<!-- markdownlint-restore -->");

    static string ToLineNumberUrl(
        ISymbol symbol, string symbolDisplayName, ActionInputs actionInputs)
    {
        var location = symbol.Locations.FirstOrDefault();
        if (location is null)
        {
            return "N/A";
        }

        var lineNumber = location.GetLineSpan().StartLinePosition.Line + 1;

        if (location.SourceTree is { FilePath.Length: > 0 })
        {
            var fullPath = location.SourceTree?.FilePath;
            var relativePath =
                Path.GetRelativePath(actionInputs.WorkspaceDirectory, fullPath!)
                    .Replace("\\", "/");
            var lineNumberFileReference =
                $"https://github.com/{actionInputs.Owner}/{actionInputs.Name}/blob/{actionInputs.Branch}/{relativePath}#L{lineNumber}";
            
            // Must force anchor link, as GitHub assumes site-relative links.
            return $"<a href='{lineNumberFileReference}' title='{symbolDisplayName}'>{lineNumber:#,0}</a>";
        }

        return $"[{lineNumber:#,0}](# \"{symbolDisplayName}\")";
    }
}

static class ServiceCollectionExtensions
{
    internal static IServiceCollection AddGitHubActionServices(
        this IServiceCollection services) =>
        services.AddSingleton<ProjectMetricDataAnalyzer>()
            .AddDotNetCodeAnalysisServices();
}

public class SilverCraftSpecificData
{
    /// <summary>
    /// List of command modules in project (if any)
    /// </summary>
    public List<CommandModule> CommandModules { get; set; } = new();
}

public class CommandModule
{
    public string Name { get; set; }
    public List<Command> Commands { get; set; } = new();

}

public class Command
{
    public string Location { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string[] Aliases { get; set; }
    public List<string> CustomAttributes { get; set; } = new();
}

sealed class ProjectMetricDataAnalyzer
{
    readonly ILogger<ProjectMetricDataAnalyzer> _logger;

    public ProjectMetricDataAnalyzer(ILogger<ProjectMetricDataAnalyzer> logger) => _logger = logger;

    public async Task<ImmutableArray<(string, CodeAnalysisMetricData,SilverCraftSpecificData)>> AnalyzeAsync(
        ProjectWorkspace workspace, string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        if (File.Exists(path))
        {
            _logger.LogInformation("Computing analytics on {Path}.", path);
        }
        else
        {
            _logger.LogWarning("{Path} doesn\'t exist.", path);
            return ImmutableArray<(string, CodeAnalysisMetricData,SilverCraftSpecificData)>.Empty;
        }

        var projects =
            await workspace.LoadProjectAsync(
                    path, cancellationToken: cancellation)
                .ConfigureAwait(false);
        var builder = ImmutableArray.CreateBuilder<(string, CodeAnalysisMetricData,SilverCraftSpecificData)>();
        SilverCraftSpecificData scSpcData = new();
        foreach (var project in projects)
        {
            var compilation =
                await project.GetCompilationAsync(cancellation)
                    .ConfigureAwait(false);
            var syntaxGen = SyntaxGenerator.GetGenerator(project);

            if (compilation?.SyntaxTrees != null)
                foreach (var tree in compilation?.SyntaxTrees)
                {
                    var classes = tree.GetRoot().DescendantNodesAndSelf()
                        .Where(x => x.IsKind(SyntaxKind.ClassDeclaration));
                    foreach (var c in classes)
                    {
                        var classDec = (ClassDeclarationSyntax)c;
                        var bases = classDec.BaseList;

                        if (bases?.Types == null) continue;
                        foreach (var b in bases.Types)
                        {
                            var nodeType = compilation.GetSemanticModel(tree).GetTypeInfo(b.Type);
                            if (nodeType.Type != null && nodeType.Type.Name.Contains("BaseCommandModule"))
                            {
                                
                                Console.WriteLine(classDec.Identifier.Text + " command module");
                                var module = new CommandModule()
                                {
                                    Name = classDec.Identifier.Text
                                };
                                scSpcData.CommandModules.Add(module);
                                var methods =classDec.DescendantNodesAndSelf()
                                    .Where(x => x.IsKind(SyntaxKind.MethodDeclaration));
                               
                                foreach (var method in methods)
                                {
                                    var loc = method.GetLocation();
                                    var attributes = syntaxGen.GetAttributes(method);

                                    void processAttribute(AttributeSyntax attribute, Command command)
                                    {
                                        Console.WriteLine(attribute.Name);
                                        var attributearguments = syntaxGen.GetAttributeArguments(attribute);
                                        string GetFirstArg()
                                        {
                                            return ((AttributeArgumentSyntax)
                                                attributearguments.First()).Expression.ToString();
                                        }
                                        string[] GetAllArg()
                                        {
                                            return attributearguments.Cast<AttributeArgumentSyntax>().Select(x=>x.Expression.ToString()).ToArray();
                                        }
                                        switch (attribute.Name.ToString())
                                        {
                                            case "Command" when attributearguments.Any():
                                                command.Name = GetFirstArg();
                                                break;
                                            case "Command":
                                                Console.Error.WriteLine("Warning: not sure about command name here");
                                                break;
                                            case "Description" when attributearguments.Any():
                                                command.Description = GetFirstArg();
                                                break;
                                            case "Description":
                                                Console.Error.WriteLine("Warning: not sure about description here");
                                                break;
                                            case "Aliases" when attributearguments.Any():
                                                command.Aliases = GetAllArg();
                                                break;
                                            case "Aliases":
                                                Console.Error.WriteLine("Warning: not sure about aliases here");
                                                break;
                                            default:
                                                Console.Error.WriteLine("Warning: not sure what to do about attribute of type "+ attribute.Name +", will add to list of custom attributes of command." );
                                                command.CustomAttributes.Add(attribute.Name.ToString());
                                                break;
                                        }
                                    }

                                    if (!loc.IsInSource || !attributes.Any()) continue;
                                    {
                                        var ghskip =
                                            $"github{(Environment.OSVersion.Platform == PlatformID.Win32NT ? "\\" : "/")}workspace";
                                        var lastloc = loc.SourceTree!.FilePath.LastIndexOf(
                                            ghskip, StringComparison.Ordinal);
                                        if (lastloc == -1)
                                        {
                                            lastloc = 0;
                                        }
                                        else
                                        {
                                            lastloc += ghskip.Length;
                                        }
                                        var linespan = loc.GetLineSpan();
                                        var command = new Command()
                                            { Location = loc.SourceTree?.FilePath[lastloc..].Replace("\\", "/") +$"#L{linespan.StartLinePosition}-L{linespan.EndLinePosition}" };
                                        foreach (var attribute in attributes)
                                        {
                                            if (attribute is AttributeListSyntax als)
                                            {
                                                foreach (var attribut in als.ChildNodes())
                                                {
                                                    processAttribute((AttributeSyntax)attribut, command);
                                                }
                                            }
                                            else
                                            {
                                                processAttribute((AttributeSyntax)attribute, command);
                                            }
                                        }
                                        Console.WriteLine(JsonSerializer.Serialize(command));
                                        module.Commands.Add(command);
                                    }
                                }
                            }
                        }
                    }
                }

            //TODO ADD NEW FIELDS HERE
            var metricData = await CodeAnalysisMetricData.ComputeAsync(
                    compilation!.Assembly,
                    new CodeMetricsAnalysisContext(compilation, cancellation))
                .ConfigureAwait(false);

            builder.Add((project.FilePath!, metricData,scSpcData));
        }

        return builder.ToImmutable();
    }
}