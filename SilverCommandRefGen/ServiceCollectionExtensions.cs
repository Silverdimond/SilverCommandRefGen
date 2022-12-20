using DotNet.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace SilverCommandRefGen;

static class ServiceCollectionExtensions
{
    internal static IServiceCollection AddGitHubActionServices(
        this IServiceCollection services) =>
        services.AddSingleton<ProjectMetricDataAnalyzer>()
            .AddDotNetCodeAnalysisServices();
}