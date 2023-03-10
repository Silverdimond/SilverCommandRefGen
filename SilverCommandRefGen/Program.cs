using System.Text;
using CommandLine;
using DotNet.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverCommandRefGen;
using static CommandLine.Parser;


using var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((_, services) => services.AddGitHubActionServices())
    .Build();

static TService Get<TService>(IHost host)
    where TService : notnull =>
    host.Services.GetRequiredService<TService>();

var parser = Default.ParseArguments<ActionInputs>(() => new(), args);
parser.WithNotParsed(
    errors =>
    {
        Get<ILoggerFactory>(host)
            .CreateLogger("SilverCraft.SilverCommandRefGen.Program")
            .LogError(
                string.Join(
                    Environment.NewLine, errors.Select(error => error.ToString())));

        Environment.Exit(2);
    });

await parser.WithParsedAsync(
    async options => await StartAnalysisAsync(options, host));
await host.RunAsync();

static async ValueTask StartAnalysisAsync(ActionInputs inputs, IHost host)
{
    using ProjectWorkspace workspace = Get<ProjectWorkspace>(host);
    using CancellationTokenSource tokenSource = new();

    Console.CancelKeyPress += delegate
    {
        tokenSource.Cancel();
    };

    var projectAnalyzer = Get<ProjectMetricDataAnalyzer>(host);

    Matcher matcher = new();
    matcher.AddIncludePatterns(new[] { "**/*.csproj", "**/*.vbproj" });

    Dictionary<string, CodeAnalysisMetricData> metricData = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, SilverCraftSpecificData> specificData = new(StringComparer.OrdinalIgnoreCase);

    var projects = matcher.GetResultsInFullPath(inputs.Directory);

    foreach (var project in projects)
    {
        var metrics =
            await projectAnalyzer.AnalyzeAsync(
                workspace, project, tokenSource.Token);

        foreach (var (path, metric, specific) in metrics)
        {
            metricData[path] = metric;
            specificData[path] = specific;
        }
    }

    var updatedMetrics = false;
    var title = "";
    StringBuilder summary = new();
    if (metricData is { Count: > 0 })
    {
        var fileName = "CODE_METRICS.md";
        var fullPath = Path.Combine(inputs.Directory, fileName);
        var logger = Get<ILoggerFactory>(host).CreateLogger(nameof(StartAnalysisAsync));
        var fileExists = File.Exists(fullPath);

        logger.LogInformation("{Updating} {FileName} markdown file with latest code metric data.", (fileExists ? "Updating" : "Creating"), fileName);

        summary.AppendLine(
            title = $"{(fileExists ? "Updated" : "Created")} {fileName} file, analyzed metrics for {metricData.Count} projects.");

        var contents = metricData.ToMarkDownBody(inputs);
        await File.WriteAllTextAsync(
            fullPath,
            contents,
            tokenSource.Token);

        updatedMetrics = true;
    }
    else
    {
        summary.Append("No metrics were determined.");
    }

    if (specificData is { Count: > 0 })
    {
        var fileName = "SilverCraftSpec.md";
        var fullPath = Path.Combine(inputs.Directory, fileName);
        var logger = Get<ILoggerFactory>(host).CreateLogger(nameof(StartAnalysisAsync));
        var fileExists = File.Exists(fullPath);
        logger.LogInformation("{Updating} {FileName} markdown file with latest silvercraft specific data.", (fileExists ? "Updating" : "Creating"), fileName);
        var contents = specificData.ToMarkDownBody(inputs);
        await File.WriteAllTextAsync(
            fullPath,
            contents,
            tokenSource.Token);

        updatedMetrics = true;
    }
   

    // https://docs.github.com/actions/reference/workflow-commands-for-github-actions#setting-an-output-parameter
    // ::set-output deprecated as mentioned in https://github.blog/changelog/2022-10-11-github-actions-deprecating-save-state-and-set-output-commands/
    var githubOutputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT", EnvironmentVariableTarget.Process);
    if (!string.IsNullOrWhiteSpace(githubOutputFile))
    {
        using (var textWriter = new StreamWriter(githubOutputFile!, true, Encoding.UTF8))
        {
            textWriter.WriteLine($"updated-metrics={updatedMetrics}");
            textWriter.WriteLine($"summary-title={title}");
            textWriter.WriteLine($"summary-details={summary}");
        }
    }
    else
    {
        Console.WriteLine($"::set-output name=updated-metrics::{updatedMetrics}");
        Console.WriteLine($"::set-output name=summary-title::{title}");
        Console.WriteLine($"::set-output name=summary-details::{summary}");
    }

    Environment.Exit(0);
}
