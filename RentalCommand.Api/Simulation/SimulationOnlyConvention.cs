using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// Application-model convention that removes every route from <see cref="SimulationOnlyAttribute"/>
/// controllers when simulation is disabled — so the dev clock/worker endpoints simply do not exist in
/// production (clean 404, controller never constructed, its dev-only services never resolved). Registered
/// in Program.cs with the startup gate (<see cref="SimulationGate"/>).
/// </summary>
public sealed class SimulationOnlyConvention : IControllerModelConvention
{
    private readonly bool _simulationEnabled;

    public SimulationOnlyConvention(bool simulationEnabled) => _simulationEnabled = simulationEnabled;

    public void Apply(ControllerModel controller)
    {
        if (_simulationEnabled)
            return;

        if (!controller.Attributes.OfType<SimulationOnlyAttribute>().Any())
            return;

        // No selectors → no endpoints → the route table never maps these actions (404), and MVC never
        // activates the controller, so its Simulation:Enabled-only dependencies are never resolved.
        foreach (var action in controller.Actions)
            action.Selectors.Clear();
        controller.Selectors.Clear();
    }
}
