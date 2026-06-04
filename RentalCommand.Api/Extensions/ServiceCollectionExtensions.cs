using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Voice;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Extensions;

/// <summary>
/// Registers the per-entity domain feature services (scoped). Each domain controller depends on one of
/// these; they own portfolio-scoped CRUD and realtime broadcasts. Later sub-units append their own
/// service registrations here so the wiring stays in one place. Call from <c>Program.cs</c> via
/// <c>builder.Services.AddDomainServices()</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        services.AddOptions<PlaidOptions>()
            .BindConfiguration(PlaidOptions.SectionName);

        // --- controllers-inventory sub-unit ---
        services.AddScoped<IPortfolioService, PortfolioService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<IOwnerEntityService, OwnerEntityService>();
        services.AddScoped<IVendorService, VendorService>();

        // --- controllers-leasing-money sub-unit ---
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ILeaseService, LeaseService>();
        services.AddScoped<ILeaseQaService, LeaseQaService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IExpenseService, ExpenseService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IScheduleEService, ScheduleEService>();
        services.AddScoped<IOwnerStatementService, OwnerStatementService>();
        services.AddScoped<IOwnerStatementEmailService, OwnerStatementEmailService>();
        services.AddScoped<ISecurityDepositService, SecurityDepositService>();
        services.AddScoped<IBankingService, BankingService>();
        services.AddHttpClient<IPlaidBankingProvider, PlaidBankingProvider>();

        // --- controllers-ops-misc sub-unit ---
        services.AddScoped<IWorkOrderService, WorkOrderService>();
        services.AddScoped<IAppointmentService, AppointmentService>();
        services.AddScoped<IInspectionService, InspectionService>();
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<INotificationSettingsService, NotificationSettingsService>();
        services.AddScoped<INoticeDraftService, NoticeDraftService>();
        services.AddScoped<ISmsInboundRentConfirmationService, SmsInboundRentConfirmationService>();
        services.AddScoped<RentalCommand.Api.Services.Security.ISmsWebhookSignatureValidator,
            RentalCommand.Api.Services.Security.SmsWebhookSignatureValidator>();

        // Aggregated read-only KPI rollup for the web dashboard.
        services.AddScoped<IDashboardService, DashboardService>();

        // Portfolio analytics overview (occupancy, rent collection, trend, work orders, lease expiry).
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        // --- scan upload pipeline ---
        services.AddScoped<IScanFileService, ScanFileService>();
        services.AddScoped<IScanService, ScanService>();
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddScoped<IVoiceIntakeService, VoiceIntakeService>();

        // --- AI (phase 3) ---
        services.AddScoped<IDailyBriefingService, DailyBriefingService>();
        services.AddScoped<IPortfolioQaService, PortfolioQaService>();

        // --- document hub ---
        services.AddScoped<IDocumentService, DocumentService>();

        // --- mobile push registration ---
        services.AddScoped<IDeviceService, DeviceService>();

        return services;
    }
}
