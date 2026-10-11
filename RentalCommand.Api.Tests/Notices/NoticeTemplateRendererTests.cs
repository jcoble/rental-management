// RentalCommand.Api.Tests/Notices/NoticeTemplateRendererTests.cs
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Notices;

public class NoticeTemplateRendererTests
{
    private static readonly Dictionary<string, string> Tokens = new()
    {
        ["tenant_name"] = "Jordan Lee",
        ["property_address"] = "12 Oak St",
        ["rent_amount"] = "$1,200",
    };

    [Fact]
    public void MergeFieldHelp_IsTypeSpecificAndExplainsTokensInPlainLanguage()
    {
        var help = NoticeMergeFields.HelpForType("rent-reminder");

        Assert.Contains(help, row => row.Key == NoticeMergeFields.TenantName
            && row.Token == "{{tenant_name}}"
            && !string.IsNullOrWhiteSpace(row.Description)
            && !string.IsNullOrWhiteSpace(row.Example));
        Assert.Contains(help, row => row.Key == NoticeMergeFields.RentAmount);
        Assert.DoesNotContain(help, row => row.Key == NoticeMergeFields.RenewalStartDate);
    }

    [Fact]
    public void Render_replaces_known_tokens()
    {
        var (subject, body) = NoticeTemplateRenderer.Render(
            "Rent for {{tenant_name}}",
            "Hi {{tenant_name}}, rent of {{rent_amount}} for {{property_address}}.",
            Tokens);

        Assert.Equal("Rent for Jordan Lee", subject);
        Assert.Equal("Hi Jordan Lee, rent of $1,200 for 12 Oak St.", body);
    }

    [Fact]
    public void Render_blanks_unknown_or_unmapped_tokens()
    {
        var (subject, body) = NoticeTemplateRenderer.Render(
            "{{unknown_token}}!",
            "Hello {{tenant_name}}{{missing}}",
            Tokens);

        Assert.Equal("!", subject);
        Assert.Equal("Hello Jordan Lee", body);
    }

    [Fact]
    public void Render_tolerates_spaces_and_case_in_token()
    {
        var (_, body) = NoticeTemplateRenderer.Render(
            "x",
            "{{ Tenant_Name }}",
            Tokens);

        Assert.Equal("Jordan Lee", body);
    }

    [Fact]
    public void ForType_returns_late_rent_fields()
    {
        var fields = NoticeMergeFields.ForType("late-rent-late-fee");
        Assert.Contains("overdue_amount", fields);
        Assert.Contains("tenant_name", fields);
    }
}
