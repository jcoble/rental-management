using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RentalCommand.Api.Simulation;

namespace RentalCommand.Api.Tests.Simulation;

public class GatingTests
{
    [Fact]
    public void Disabled_BindsSystemTimeProvider()
    {
        using var provider = Build(enabled: false, environment: "Development");

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Enabled_InNonProd_BindsSimulationTimeProvider()
    {
        using var provider = Build(enabled: true, environment: "Development");

        Assert.IsType<SimulationTimeProvider>(provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Enabled_InProduction_StaysOnSystemTimeProvider()
    {
        // Safety net: even if the flag is somehow true in prod, the clock stays real.
        using var provider = Build(enabled: true, environment: "Production");

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    private static ServiceProvider Build(bool enabled, string environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Simulation:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        services.AddSimulationClock(configuration, new FakeHostEnvironment(environment));

        return services.BuildServiceProvider();
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "RentalCommand.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
