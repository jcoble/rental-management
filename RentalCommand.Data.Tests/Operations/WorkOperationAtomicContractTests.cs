using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Operations;

namespace RentalCommand.Data.Tests.Operations;

public sealed class WorkOperationAtomicContractTests
{
    [Theory]
    [InlineData(typeof(CreateWorkOrderHandler), typeof(IAtomicReplayAuthorizer<CreateWorkOrderCommand>))]
    [InlineData(typeof(UpdateWorkOrderHandler), typeof(IAtomicReplayAuthorizer<UpdateWorkOrderCommand>))]
    [InlineData(typeof(DeleteWorkOrderHandler), typeof(IAtomicReplayAuthorizer<DeleteWorkOrderCommand>))]
    [InlineData(typeof(CreateTenantWorkOrderHandler), typeof(IAtomicReplayAuthorizer<CreateTenantWorkOrderCommand>))]
    [InlineData(typeof(CreateAppointmentHandler), typeof(IAtomicReplayAuthorizer<CreateAppointmentCommand>))]
    [InlineData(typeof(UpdateAppointmentHandler), typeof(IAtomicReplayAuthorizer<UpdateAppointmentCommand>))]
    [InlineData(typeof(DeleteAppointmentHandler), typeof(IAtomicReplayAuthorizer<DeleteAppointmentCommand>))]
    public void Every_live_work_order_handler_reauthorizes_receipt_replay(
        Type handlerType, Type replayContract)
    {
        replayContract.IsAssignableFrom(handlerType).Should().BeTrue();
    }

    [Fact]
    public void Receipt_codec_preserves_the_original_response_snapshot()
    {
        var codec = new AtomicJsonResultCodec<OperationMutationResult>("work-order.mutation.v1");
        var expected = new OperationMutationResult(
            OperationMutationOutcome.Applied, 47, "{\"Id\":47,\"Title\":\"Original\"}");

        codec.Deserialize(codec.Serialize(expected)).Should().BeEquivalentTo(expected);
    }
}
