namespace RentalCommand.Api.DTOs;

/// <summary>Request body for <c>POST /api/v1/devices</c>.</summary>
public record RegisterDeviceRequest(string Token, string Platform);
