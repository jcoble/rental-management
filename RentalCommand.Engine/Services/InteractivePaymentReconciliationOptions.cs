using Microsoft.Extensions.Options;

namespace RentalCommand.Engine.Services;

/// <summary>Cadence and bounded-work policy for durable interactive-payment reconciliation.</summary>
public sealed class InteractivePaymentReconciliationOptions
{
    public const string SectionName = "Payments:InteractivePaymentReconciliation";
    public static TimeSpan ProductionCadence => TimeSpan.FromMinutes(1);

    /// <summary>Explicit production cadence; operators may lengthen it through configuration.</summary>
    public TimeSpan PollInterval { get; set; } = ProductionCadence;

    /// <summary>Delay persisted after an unresolved provider result or transport failure.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Confirmed-no-object attempts older than this are released as Failed.</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);

    public int BatchSize { get; set; } = 50;
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class InteractivePaymentReconciliationOptionsValidator
    : IValidateOptions<InteractivePaymentReconciliationOptions>
{
    public ValidateOptionsResult Validate(
        string? name, InteractivePaymentReconciliationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.PollInterval <= TimeSpan.Zero)
            failures.Add(nameof(options.PollInterval) + " must be positive.");
        if (options.RetryDelay <= TimeSpan.Zero)
            failures.Add(nameof(options.RetryDelay) + " must be positive.");
        if (options.Expiration <= TimeSpan.Zero)
            failures.Add(nameof(options.Expiration) + " must be positive.");
        if (options.BatchSize <= 0)
            failures.Add(nameof(options.BatchSize) + " must be positive.");
        if (options.StepTimeout <= TimeSpan.Zero)
            failures.Add(nameof(options.StepTimeout) + " must be positive.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
