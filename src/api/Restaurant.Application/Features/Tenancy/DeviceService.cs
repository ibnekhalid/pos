using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Tenancy;

/// <summary>Counter PCs trusted for staff PIN sign-in (PLAN.md section 5).</summary>
public class DeviceService(IApplicationDbContext db, ITenantContext tenant, TenancyService tenancy, AuditLogger audit)
{
    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var allowed = tenant.AllowedBranchIds.ToList();
        return await db.Devices
            .Where(d => allowed.Contains(d.BranchId))
            .OrderBy(d => d.CreatedAt)
            .Select(d => ToDto(d))
            .ToListAsync(cancellationToken);
    }

    public async Task<RegisterDeviceResult> RegisterAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
        {
            throw new ValidationAppException(nameof(request.Name), "Give the device a name (up to 100 characters).");
        }

        var branch = await tenancy.GetAccessibleBranchAsync(request.BranchId, cancellationToken);
        if (request.RegisterId is { } registerId
            && !await db.Registers.AnyAsync(r => r.Id == registerId && r.BranchId == branch.Id, cancellationToken))
        {
            throw new NotFoundException("Register");
        }

        var rawToken = Secrets.NewToken();
        var device = new RegisteredDevice
        {
            TenantId = branch.TenantId,
            BranchId = branch.Id,
            RegisterId = request.RegisterId,
            Name = request.Name.Trim(),
            TokenHash = Secrets.Hash(rawToken)
        };
        db.Devices.Add(device);

        audit.Record(AuditActions.DeviceRegistered, nameof(RegisteredDevice), device.Id, after: ToDto(device), branchId: branch.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new RegisterDeviceResult(ToDto(device), rawToken);
    }

    /// <summary>Renames a counter PC or changes which register (till) it is.</summary>
    public async Task<DeviceDto> UpdateAsync(Guid deviceId, UpdateDeviceRequest request, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
        {
            throw new ValidationAppException(nameof(request.Name), "Give the device a name (up to 100 characters).");
        }
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, cancellationToken)
            ?? throw new NotFoundException("Device");
        tenant.EnsureBranch(device.BranchId);
        if (request.RegisterId is { } registerId
            && !await db.Registers.AnyAsync(r => r.Id == registerId && r.BranchId == device.BranchId, cancellationToken))
        {
            throw new NotFoundException("Register");
        }

        var before = ToDto(device);
        device.Name = request.Name.Trim();
        device.RegisterId = request.RegisterId;
        audit.Record(AuditActions.DeviceUpdated, nameof(RegisteredDevice), device.Id, before, ToDto(device), branchId: device.BranchId);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(device);
    }

    /// <summary>
    /// The PC making the request, from its device token (any signed-in staff member of the same restaurant):
    /// the desktop app reads its register from here. Null when the token is unknown, revoked or another tenant's.
    /// </summary>
    public async Task<DeviceDto?> CurrentAsync(string? deviceToken, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        if (string.IsNullOrWhiteSpace(deviceToken)) return null;
        var hash = Secrets.Hash(deviceToken);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAt == null, cancellationToken);
        return device is null ? null : ToDto(device);
    }

    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new NotFoundException("Device");
        if (!tenant.CanAccessBranch(device.BranchId))
        {
            throw new ForbiddenAccessException("You don't have access to this branch.");
        }
        if (device.RevokedAt is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        device.RevokedAt = now;

        // End the PIN sessions started on it.
        var sessions = await db.RefreshTokens.Where(t => t.DeviceId == device.Id && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }

        audit.Record(AuditActions.DeviceRevoked, nameof(RegisteredDevice), device.Id, branchId: device.BranchId);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DeviceDto ToDto(RegisteredDevice d) => new(d.Id, d.BranchId, d.RegisterId, d.Name, d.CreatedAt, d.LastSeenAt, d.RevokedAt is not null);
}
