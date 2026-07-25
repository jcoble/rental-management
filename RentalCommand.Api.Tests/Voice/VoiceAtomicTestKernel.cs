using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Scanning;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Voice;

internal static class VoiceAtomicTestKernel
{
    internal static ServiceProvider Create(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            CreateVoiceScanDraftCommand,
            ScanDraftMutationResult,
            CreateVoiceScanDraftHandler>();
        services.AddAtomicCommandHandler<
            AnswerVoiceScanDraftCommand,
            ScanDraftMutationResult,
            AnswerVoiceScanDraftHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(connectionString)
                .AddInterceptors(SqliteDatabaseClockInterceptor.Instance)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }
}
