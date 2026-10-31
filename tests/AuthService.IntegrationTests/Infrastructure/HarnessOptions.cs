using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>How a test wants its copy of the service to differ from the default.</summary>
public sealed class HarnessOptions
{
    /// <summary>Configuration values, such as <c>Email:Provider</c>. They override the harness defaults.</summary>
    public Dictionary<string, string?> Settings { get; init; } = [];

    /// <summary>Replace or add services, for example to make the email provider fail.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
}
