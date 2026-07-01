namespace RentalCommand.Api.Simulation;

/// <summary>
/// Marks a controller as part of the dev-only simulation surface. When simulation is NOT active
/// (production, or <c>Simulation:Enabled=false</c>), <see cref="SimulationOnlyConvention"/> strips every
/// route from the controller at startup, so it is unreachable (404) and MVC never activates it — its
/// simulation-only dependencies are therefore never resolved in production.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SimulationOnlyAttribute : Attribute;
