using FluentAssertions;
using RentalCommand.Core.Entities;

namespace RentalCommand.Core.Tests.Entities;

public sealed class WorkspaceAccessContextTests
{
    [Fact]
    public void AccessRevision_HasNoPublicSetterAndOnlyAdvancesFromExpectedValue()
    {
        var property = typeof(WorkspaceAccessContext)
            .GetProperty(nameof(WorkspaceAccessContext.AccessRevision));
        property.Should().NotBeNull();
        property!.SetMethod.Should().NotBeNull();
        property.SetMethod!.IsPrivate.Should().BeTrue();

        var context = new WorkspaceAccessContext();
        context.AccessRevision.Should().Be(1);
        context.AdvanceRevision(expectedRevision: 1);
        context.AccessRevision.Should().Be(2);

        var staleAdvance = () => context.AdvanceRevision(expectedRevision: 1);
        staleAdvance.Should().Throw<InvalidOperationException>();
        context.AccessRevision.Should().Be(2);
    }
}
