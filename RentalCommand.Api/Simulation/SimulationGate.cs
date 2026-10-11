using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// Single source of truth for whether the master simulation clock surface is active: the
/// <c>Simulation:Enabled</c> config flag AND a non-Production environment. Everything dev-only keys off
/// this — the <see cref="SimulationTimeProvider"/> + poll refresher (A7), the dev clock/worker
/// controllers (gated via <see cref="SimulationOnlyConvention"/>), and the Engine command worker — so
/// production is always inert (real clock, no dev routes, no command worker).
/// </summary>
public static class SimulationGate
{
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool>("Simulation:Enabled") && !environment.IsProduction();
}
