using System.Reflection;
using FluentAssertions;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Tests.Atomic;

public sealed class AtomicKernelArchitectureBudgetTests
{
    private static readonly string[] AllowedPublicInterfaces =
    [
        "IAtomicCommandContext",
        "IAtomicCommandData",
        "IAuthorizationScopedRequest",
        "IWriteExecutor",
    ];

    [Fact]
    public void Public_kernel_surface_stays_within_the_wholesale_budget()
    {
        var interfaces = typeof(IWriteExecutor).Assembly
            .GetExportedTypes()
            .Where(type => type.IsInterface
                && type.Namespace?.StartsWith(
                    "RentalCommand.Core.Atomic",
                    StringComparison.Ordinal) == true)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        interfaces.Select(type => type.Name).Should().Equal(
            AllowedPublicInterfaces,
            "the replacement has one closed public surface; add no compatibility or speculative interfaces");

        var memberCount = interfaces.Sum(DeclaredContractMemberCount);
        memberCount.Should().BeLessThanOrEqualTo(
            21,
            "the complete public interface surface is capped at twenty-one declared members");

        var executionMethods = interfaces
            .SelectMany(type => type.GetMethods())
            .Where(method => method.Name == nameof(IWriteExecutor.ExecuteAsync))
            .ToArray();
        executionMethods.Should().ContainSingle(
            "the kernel exposes exactly one public command execution method");
        executionMethods[0].DeclaringType.Should().Be(typeof(IWriteExecutor));
    }

    [Fact]
    public void Transactional_write_requires_explicit_replay_authorization()
    {
        var write = typeof(TransactionalWrite<,>);
        write.GetProperty(nameof(TransactionalWrite<IAtomicCommandData, object>.AuthorizeReplayAsync))
            .Should().NotBeNull("every write must explicitly authorize receipt replay");
        write.GetConstructors().Should().ContainSingle();
        write.GetConstructors().Single().GetParameters().Select(parameter => parameter.Name)
            .Should().Contain("authorizeReplayAsync");
    }

    [Fact]
    public void Kernel_has_no_domain_dependencies_and_stays_below_one_thousand_logical_lines()
    {
        var root = RepositoryRoot();
        var files = KernelFiles(root).ToArray();
        var source = string.Join('\n', files.Select(File.ReadAllText));

        source.Should().NotContain("RentalCommand.Core.Entities");
        source.Should().NotContain("RentalCommand.Core.Enums");
        source.Should().NotContain("RentalCommand.Core.Interfaces");
        source.Should().NotContain("IServiceProvider");
        source.Should().NotContain("GetRequiredService");

        var logicalLines = files.Sum(path => LogicalLineCount(File.ReadAllText(path)));
        logicalLines.Should().BeLessThan(
            1_000,
            "non-generated production kernel code has a hard sub-1,000-line ceiling");
    }

    [Fact]
    public void Runner_couples_outbox_to_commit_and_the_interceptors_keep_context_transaction_and_rls_separate()
    {
        var root = RepositoryRoot();
        var runner = File.ReadAllText(
            Path.Combine(root, "RentalCommand.Data", "Atomic", "AtomicTransactionRunner.cs"));
        var auditScope = File.ReadAllText(
            Path.Combine(root, "RentalCommand.Data", "Auditing", "AtomicAuditScope.cs"));
        var transactionInterceptor = File.ReadAllText(
            Path.Combine(
                root,
                "RentalCommand.Data",
                "Interceptors",
                "AtomicTransactionLifecycleInterceptor.cs"));
        var registration = File.ReadAllText(
            Path.Combine(
                root,
                "RentalCommand.Data",
                "DependencyInjection",
                "AtomicPersistenceKernelExtensions.cs"));
        var apiProgram = File.ReadAllText(
            Path.Combine(root, "RentalCommand.Api", "Program.cs"));
        var apiRls = File.ReadAllText(
            Path.Combine(root, "RentalCommand.Api", "Data", "RlsConnectionInterceptor.cs"));
        var engineProgram = File.ReadAllText(
            Path.Combine(root, "RentalCommand.Engine", "Program.cs"));
        var engineRls = File.ReadAllText(
            Path.Combine(root, "RentalCommand.Engine", "Data", "EngineRlsInterceptor.cs"));

        runner.Should().Contain("CurrentTransaction");
        auditScope.Should().Contain("ReferenceEquals(_ownerContext, context)");
        auditScope.Should().Contain("GetDbTransaction");
        transactionInterceptor.Should().Contain("DbConnection");
        transactionInterceptor.Should().Contain("DbTransaction");

        registration.Should().Contain("AtomicAuditSaveChangesInterceptor");
        registration.Should().Contain("AtomicTransactionLifecycleInterceptor");
        registration.Should().Contain("WorkspaceAuthorityOwnershipInterceptor");
        registration.Should().NotContain("RlsConnectionInterceptor");
        registration.Should().NotContain("EngineRlsInterceptor");

        apiProgram.Should().Contain(".UseAtomicPersistenceKernel(sp)");
        apiProgram.Should().Contain("RlsConnectionInterceptor");
        apiRls.Should().Contain("RuntimeRole");
        apiRls.Should().Contain("AccessContext");
        apiRls.Should().Contain("AccessRevision");
        engineProgram.Should().Contain(".UseAtomicPersistenceKernel(sp)");
        engineProgram.Should().Contain("EngineRlsInterceptor");
        engineRls.Should().Contain("RuntimeRole");

        var outboxIndex = runner.LastIndexOf("Outbox", StringComparison.Ordinal);
        var commitIndex = runner.LastIndexOf("CommitAsync", StringComparison.Ordinal);
        outboxIndex.Should().BeGreaterThanOrEqualTo(
            0,
            "the durable outbox intent is part of the command transaction");
        commitIndex.Should().BeGreaterThan(outboxIndex,
            "outbox rows commit with business data; only external delivery occurs after commit");
    }

    [Fact]
    public void Command_disposition_has_only_execute_and_replay()
    {
        Enum.GetNames<AtomicCommandDisposition>().Should().Equal(
            nameof(AtomicCommandDisposition.Executed),
            nameof(AtomicCommandDisposition.Replayed));
    }

    [Fact]
    public void Hard_delete_semantic_audits_bind_after_the_entry_is_marked_deleted()
    {
        var root = RepositoryRoot();
        var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root, "RentalCommand.Data"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            for (var auditIndex = 0; auditIndex < lines.Length; auditIndex++)
            {
                if (!lines[auditIndex].Contains(
                        "AuditLogOperation.Deleted",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var windowStart = Math.Max(0, auditIndex - 12);
                var windowEnd = Math.Min(lines.Length - 1, auditIndex + 12);
                var bindIndex = Enumerable.Range(windowStart, auditIndex - windowStart + 1)
                    .LastOrDefault(index => lines[index].Contains(
                        "BindSemanticAudit(",
                        StringComparison.Ordinal), -1);
                var removeIndex = Enumerable.Range(windowStart, windowEnd - windowStart + 1)
                    .FirstOrDefault(index => lines[index].Contains(
                        ".Remove(",
                        StringComparison.Ordinal), -1);
                if (bindIndex >= 0 && removeIndex >= 0 && removeIndex > bindIndex)
                {
                    failures.Add(
                        $"{Path.GetRelativePath(root, path)}:{auditIndex + 1}");
                }
            }
        }

        failures.Should().BeEmpty(
            "a hard-delete entry must be marked Deleted before BindSemanticAudit validates its exact tracked state");
    }

    private static int DeclaredContractMemberCount(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(method => !method.IsSpecialName)
        + type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length
        + type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length;

    private static IEnumerable<string> KernelFiles(string root) =>
        new[]
            {
                Path.Combine(root, "RentalCommand.Core", "Atomic"),
                Path.Combine(root, "RentalCommand.Data", "Atomic"),
            }
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(path =>
                !Path.GetFileName(path).Contains(".g.", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(path).EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals(
                "TransactionalWriteDefaults.cs",
                StringComparison.OrdinalIgnoreCase));

    private static int LogicalLineCount(string source)
    {
        var inBlockComment = false;
        var count = 0;
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.Trim();
            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                    line = line[(line.IndexOf("*/", StringComparison.Ordinal) + 2)..].Trim();
                }
                else
                {
                    continue;
                }
            }

            while (line.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlockComment = true;
                    line = string.Empty;
                    break;
                }

                line = line[(end + 2)..].Trim();
            }

            if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate RentalCommand.sln.");
    }
}
