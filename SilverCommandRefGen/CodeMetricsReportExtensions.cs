using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using Markdown;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;

namespace SilverCommandRefGen;

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
                new MarkdownTextListItem(
                    $"Approximately {assemblyMetric.ExecutableLines:#,0} lines of executable code."),
                new MarkdownTextListItem(
                    $"The highest cyclomatic complexity is {FormatComplexity(assemblyHighestComplexity)}."));

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
                    new MarkdownTextListItem(
                        $"Approximately {namespaceMetric.ExecutableLines:#,0} lines of executable code."),
                    new MarkdownTextListItem(
                        $"The highest cyclomatic complexity is {FormatComplexity(namespaceHighestComplexity)}."));

                foreach (var classMetric in namespaceMetric.Children
                             .OrderBy(md => md.Symbol.Name))
                {
                    var (classId, classSymbolName, _, namedTypeHighestComplexity) = ToIdAndAnchorPair(classMetric);
                    OpenCollapsibleSection(
                        document, classId, classSymbolName, namedTypeHighestComplexity.emoji);

                    document.AppendList(
                        new MarkdownTextListItem(
                            $"The `{classSymbolName}` contains {classMetric.Children.Length} members."),
                        new MarkdownTextListItem($"{classMetric.SourceLines:#,0} total lines of source code."),
                        new MarkdownTextListItem(
                            $"Approximately {classMetric.ExecutableLines:#,0} lines of executable code."),
                        new MarkdownTextListItem(
                            $"The highest cyclomatic complexity is {FormatComplexity(namedTypeHighestComplexity)}."));

                    MarkdownTableHeader tableHeader = new(
                        new("Member kind", MarkdownTableTextAlignment.Center),
                        new("Line number", MarkdownTableTextAlignment.Center),
                        new("Maintainability index", MarkdownTableTextAlignment.Center),
                        new("Cyclomatic complexity", MarkdownTableTextAlignment.Center),
                        new("Depth of inheritance", MarkdownTableTextAlignment.Center),
                        new("Class coupling", MarkdownTableTextAlignment.Center),
                        new("Lines of source / executable code", MarkdownTableTextAlignment.Center));

                    var rows = classMetric.Children.OrderBy(md => md.Symbol.Name).Select(memberMetric => ToTableRowFrom(memberMetric, actionInputs)).ToList();

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

    internal static string ToMarkDownBody(
        this Dictionary<string, SilverCraftSpecificData> metricData,
        ActionInputs actionInputs)
    {
        MarkdownDocument document = new();
        DisableMarkdownLinterAndCaptureConfig(document);
        document.AppendHeader("Commands and geeky info", 1);
        document.AppendParagraph(
            $"This file is dynamically maintained by a bot, ~~a silverbot~~. ");
        foreach (var (s, d) in metricData)
        {
            document.AppendHeader(s, 2);
            foreach (var commandModule in d.CommandModules)
            {
                var ghskip =
                    $"github{(Environment.OSVersion.Platform == PlatformID.Win32NT ? "\\" : "/")}workspace";
                var lastloc = commandModule.Name.LastIndexOf(
                    ghskip, StringComparison.Ordinal);
                if (lastloc == -1)
                {
                    lastloc = 0;
                }
                else
                {
                    lastloc += ghskip.Length;
                }

                document.AppendHeader(commandModule.Name[lastloc..].Replace("\\", "/"), 3);
                foreach (var command in commandModule.Commands.GroupBy(x => x.Name))
                {
                    document.AppendHeader((command.Key ?? "Unknown command name"), 4);
                    document.AppendParagraph(command.First().Description ?? "Unknown description");
                    foreach (var cmd in command)
                    {
                        if (cmd.Aliases != null)
                        {
                            document.AppendParagraph(string.Join(",", cmd.Aliases.Select(x => $"`{x}`")));
                        }

                        document.AppendHeader("Arguments", 5);
                        StringBuilder builder = new();
                        builder.Append('`').Append(command.Key);
                        foreach (var argument in cmd.Arguments)
                        {
                            builder.Append(' ');
                            if (argument.Optional || argument.RemainingText)
                            {
                                builder.Append('[');
                            }
                            else
                            {
                                builder.Append('<');
                            }

                            builder.Append(argument.Name);
                            if (argument.RemainingText)
                            {
                                builder.Append("...");
                            }

                            if (argument.Optional || argument.RemainingText)
                            {
                                builder.Append(']');
                            }
                            else
                            {
                                builder.Append('>');
                            }
                        }
                        builder.Append("`");
                        document.AppendParagraph(builder.ToString());
                        document.AppendList(cmd.Arguments.Select(argument =>
                                $"{argument.Name} - {argument.Description ?? "No description"} ({argument.Type})")
                            .ToArray());
                        document.AppendParagraph(
                            $"https://github.com/{actionInputs.Owner}/{actionInputs.Name}/blob/{actionInputs.Branch}{cmd.Location}");
                    }
                }
            }
        }
        document.AppendCode("json", JsonSerializer.Serialize(metricData));
        AppendMaintainedByBotMessage(document);
        RestoreMarkdownLinter(document);
        return document.ToString();
    }

    static void AppendMetricDefinitions(MarkdownDocument document)
    {
        document.AppendHeader("Metric definitions", 2);
        MarkdownList markdownList = new();
        foreach ((var columnHeader, var defintion)
                 in new[]
                 {
                     ("Maintainability index", "Measures ease of code maintenance. ???? ??? Higher values are better."),
                     ("Cyclomatic complexity", "Measures the number of branches. ???? ?????? Lower values are better."),
                     ("Depth of inheritance",
                         "Measures length of object inheritance hierarchy. ???? ?????? Lower values are better."),
                     ("Class coupling",
                         "Measures the number of classes that are referenced.???? ???? ?????? Lower values are better."),
                     ("Lines of source code",
                         "Exact number of lines of source code. ???? ???? ???? ?????? Lower values are better."),
                     ("Lines of executable code",
                         "Approximates the lines of executable code. ???? ???? ???? ???? ?????? Lower values are better.")
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

    static (string elementId, string displayName, string anchorLink, (int highestComplexity, string emoji))
        ToIdAndAnchorPair(
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