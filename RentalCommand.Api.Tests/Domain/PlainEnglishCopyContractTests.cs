using System.Reflection;
using FluentAssertions;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PlainEnglishCopyContractTests
{
    [Fact]
    public void Workspace_request_key_validation_preserves_client_operation_parameter_name()
    {
        var method = typeof(WorkspaceLlmCredentialService).GetMethod(
            "MutationIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var invocation = () => method!.Invoke(null, [1, null]);

        var target = invocation.Should().Throw<TargetInvocationException>().Which;
        var argument = target.InnerException.Should().BeOfType<ArgumentException>().Subject;
        argument.ParamName.Should().Be("clientOperationId");
        argument.Message.Should().StartWith(
            "A request key is required and cannot exceed 160 characters.");
    }

    [Fact]
    public void Banking_match_validation_uses_record_language()
    {
        var source = ReadSource("RentalCommand.Api", "Services", "Domain", "BankingService.cs");

        source.Should().Contain("Choose exactly one record to match to this bank transaction.");
        source.Should().Contain("Choose one record to match to this bank transaction.");
        source.Should().NotContain("Choose exactly one bank transaction to reconcile.");
        source.Should().NotContain("Choose one bank transaction to reconcile.");
    }

    [Fact]
    public void Owner_statement_validation_mentions_the_request_key()
    {
        var source = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "OwnerStatementEmailService.cs");

        source.Should().Contain("Complete owner statement email details and a request key are required.");
        source.Should().NotContain("Owner statement recipient, subject, message, and year are required.");
    }

    private static string ReadSource(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Rental Command repository root.");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
    }
}
