using System.Collections.Immutable;
using System.Text.Json;
using DotNet.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.Extensions.Logging;

namespace SilverCommandRefGen;

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
                    var classes = (await tree.GetRootAsync()).DescendantNodesAndSelf()
                        .Where(x => x.IsKind(SyntaxKind.ClassDeclaration));
                    foreach (var c in classes)
                    {
                        var classDec = (ClassDeclarationSyntax)c;
                        var bases = classDec.BaseList;

                        if (bases?.Types == null) continue;
                        foreach (var b in bases.Types)
                        {
                            var nodeType = compilation.GetSemanticModel(tree).GetTypeInfo(b.Type);
                            if (nodeType.Type != null && (nodeType.Type.Name.Contains("BaseCommandModule") ||nodeType.Type.Name.Contains("ApplicationCommandModule") ))
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
                                        var attributearguments = syntaxGen.GetAttributeArguments(attribute);
                                        string GetFirstArg()
                                        {
                                            return ((AttributeArgumentSyntax)
                                                attributearguments.First()).Expression.ToString();
                                        }
                                        string GetnthArg(int n)
                                        {
                                            return ((AttributeArgumentSyntax)
                                                attributearguments.ElementAt(n)).Expression.ToString();
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
                                            case "SlashCommand" when attributearguments.Any():
                                                command.Name = GetFirstArg();
                                                command.Description = GetnthArg(1);
                                                break;
                                            case "SlashCommand":
                                                Console.Error.WriteLine("Warning: not sure about command name here (slash btw)");
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
                                            { Location = loc.SourceTree?.FilePath[lastloc..].Replace("\\", "/") +$"#L{linespan.StartLinePosition.Line+1}-L{linespan.EndLinePosition.Line+1}" };
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
                                        var parameters = syntaxGen.GetParameters(method);
                                        foreach (var parameter in parameters)
                                        {
                                            var par = ((ParameterSyntax)parameter);
                                            command.Arguments.Add(new Argument(par.Identifier.ToString(),par.Type.ToString()));
                                        }
                                        module.Commands.Add(command);
                                    }
                                }
                            }
                        }
                    }
                }
            var metricData = await CodeAnalysisMetricData.ComputeAsync(
                    compilation!.Assembly,
                    new CodeMetricsAnalysisContext(compilation, cancellation))
                .ConfigureAwait(false);

            builder.Add((project.FilePath!, metricData, scSpcData));
        }

        return builder.ToImmutable();
    }
}