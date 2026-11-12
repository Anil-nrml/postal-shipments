using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace PostalService.Shipments.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddShipmentsApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<CreateShipmentRequestValidator>(includeInternalTypes: true);
        services.AddScoped<ShipmentService>();
        services.AddScoped<PostOfficeEventHandler>();
        return services;
    }
}
