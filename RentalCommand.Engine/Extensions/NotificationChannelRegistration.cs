using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Extensions;

public static class NotificationChannelRegistration
{
    public static IServiceCollection AddNotificationDeliveryChannel(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var deliveryMode = configuration.GetSection(NotificationsConfig.SectionName)
            .GetValue<string?>(nameof(NotificationsConfig.DeliveryMode));
        if (UseExternalDelivery(deliveryMode))
        {
            services.AddHttpClient<INotificationChannel, RoutingNotificationChannel>();
        }
        else
        {
            services.AddScoped<INotificationChannel, CapturedNotificationChannel>();
        }

        return services;
    }

    public static bool UseExternalDelivery(string? deliveryMode) =>
        NotificationDeliveryModes.IsExternal(deliveryMode);
}
