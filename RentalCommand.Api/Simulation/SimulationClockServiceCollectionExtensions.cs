using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// One shared registration for the master simulation clock, called by BOTH the API and Engine hosts.
/// Gated by <c>Simulation:Enabled</c> (default false) AND never enabled in Production, so production
/// always binds <see cref="TimeProvider.System"/> — byte-for-byte real-clock behavior.
/// </summary>
public static class SimulationClockServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ambient <see cref="TimeProvider"/> and <see cref="IAppTimeZoneProvider"/>.
    /// When disabled (or in Production): binds <see cref="TimeProvider.System"/> and a config-only
    /// <see cref="AppTimeZoneProvider"/>. When enabled (non-prod): binds the simulation provider stack
    /// (<see cref="ClockStateProvider"/> + 1s <see cref="ClockStateRefresher"/> + <see cref="SimulationTimeProvider"/>).
    /// </summary>
    public static IServiceCollection AddSimulationClock(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
        => services.AddSimulationClock(configuration, environment, pinFrameworkAuthClock: false);

    /// <param name="pinFrameworkAuthClock">
    /// API hosts pass <c>true</c> so that, in simulation mode, the framework auth options that would
    /// otherwise auto-pick up the ambient (sim) <see cref="TimeProvider"/> are pinned back to
    /// <see cref="TimeProvider.System"/> — auth timing must stay real (S1). The Engine has no auth
    /// handlers, so it passes <c>false</c> and never touches those options.
    /// </param>
    public static IServiceCollection AddSimulationClock(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool pinFrameworkAuthClock)
    {
        // Safety net: even if the flag is somehow true in Production, the clock stays real. The whole
        // simulation surface is a non-production dev/test tool.
        var enabled = configuration.GetValue<bool>("Simulation:Enabled") && !environment.IsProduction();

        if (!enabled)
        {
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IAppTimeZoneProvider, AppTimeZoneProvider>(); // config/ET only (null clock state)
            return services;
        }

        services.AddSingleton<IClockStateProvider, ClockStateProvider>();
        services.AddSingleton<IAppTimeZoneProvider, AppTimeZoneProvider>();
        services.AddSingleton<TimeProvider, SimulationTimeProvider>();
        services.AddHostedService<ClockStateRefresher>();

        if (pinFrameworkAuthClock)
        {
            PinFrameworkAuthClockToSystem(services);
        }

        return services;
    }

    /// <summary>
    /// S1 guard: our <see cref="SimulationTimeProvider"/> is the ambient DI <see cref="TimeProvider"/>,
    /// which ASP.NET Core auto-injects into the cookie/external auth handler options and the security-stamp
    /// validator. Auth timing MUST stay real even under a frozen/advanced sim clock, so pin those back to
    /// <see cref="TimeProvider.System"/>. (Bearer <c>exp</c>/<c>nbf</c> uses TokenValidationParameters —
    /// real by default; 2FA and email-confirm/reset read UtcNow directly — unaffected.) Setting the value
    /// via a pre-<c>PostConfigure</c> configure wins against the framework's own <c>??=</c> injection.
    /// Kept in a separate method so its cookie/Identity type references only load in the API host.
    /// </summary>
    private static void PinFrameworkAuthClockToSystem(IServiceCollection services)
    {
        // Covers every Identity cookie scheme (Application / External / TwoFactor*).
        services.ConfigureAll<CookieAuthenticationOptions>(options => options.TimeProvider = TimeProvider.System);
        // Security-stamp principal-refresh interval.
        services.Configure<SecurityStampValidatorOptions>(options => options.TimeProvider = TimeProvider.System);
    }
}
