namespace Restaurant.Contracts.System;

/// <summary>Returned by GET /api/info — lets a client check it can reach the API.</summary>
public record ApiInfoDto(string Name, string Version, string Environment);
