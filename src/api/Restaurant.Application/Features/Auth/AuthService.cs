using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Models;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Onboarding;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Auth;

/// <summary>
/// Sign-up, sign-in (password and counter PIN), tenant selection, token refresh and password
/// management. Most of this runs before a tenant is known, so the queries here scope themselves
/// (IgnoreQueryFilters + an explicit tenant/user condition) instead of relying on the global filter.
/// </summary>
public class AuthService(
    IApplicationDbContext db,
    UserManager<AppUser> users,
    IPasswordHasher<AppUser> pinHasher,
    ITokenService tokens,
    ITenantContext tenant,
    MembershipAccessService access,
    TenantProvisioner provisioner,
    AuditLogger audit,
    IPasswordResetNotifier resetNotifier,
    IOptions<AuthSettings> settings,
    IServiceProvider services,
    ILogger<AuthService> logger)
{
    private readonly AuthSettings _settings = settings.Value;

    public async Task<AuthResultDto> RegisterOwnerAsync(RegisterOwnerRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var email = LoginNames.NormalizeEmail(request.Email);
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : LoginNames.NormalizePhone(request.Phone);

        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new ConflictException("An account with this email already exists. Sign in instead.");
        }
        if (phone is not null && await db.Users.AnyAsync(u => u.PhoneNumber == phone, cancellationToken))
        {
            throw new ConflictException("An account with this mobile number already exists. Sign in instead.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = new AppUser { UserName = email, Email = email, PhoneNumber = phone, FullName = request.FullName.Trim() };
        ThrowIfFailed(await users.CreateAsync(user, request.Password));

        var (newTenant, _, membership) = provisioner.Provision(user, request.RestaurantName, request.BranchName, request.BranchAddress, request.City, phone);
        audit.Record(AuditActions.TenantRegistered, nameof(Tenant), newTenant.Id, after: new { newTenant.Name }, tenantId: newTenant.Id, actorUserId: user.Id);
        await db.SaveChangesAsync(cancellationToken);

        var result = await IssueAsync(user, membership, persistent: true, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Registered tenant {TenantId} with owner {UserId}", newTenant.Id, user.Id);
        return result;
    }

    public async Task<AuthResultDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var user = await FindByLoginAsync(LoginNames.Normalize(request.Login), cancellationToken)
            ?? throw new AuthenticationFailedException();

        if (await users.IsLockedOutAsync(user))
        {
            throw new AuthenticationFailedException("Too many failed attempts. Try again in a few minutes.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            throw new AuthenticationFailedException();
        }

        await users.ResetAccessFailedCountAsync(user);

        var memberships = await ActiveMembershipsQuery(user.Id).ToListAsync(cancellationToken);
        if (memberships.Count == 1)
        {
            audit.Record(AuditActions.Login, nameof(AppUser), user.Id, tenantId: memberships[0].TenantId, actorUserId: user.Id);
            return await IssueAsync(user, memberships[0], request.RememberMe, cancellationToken: cancellationToken);
        }

        if (memberships.Count > 1)
        {
            var pending = tokens.CreateAccessToken(user.Id, null, null, pendingSelection: true, TimeSpan.FromMinutes(_settings.PendingSelectionMinutes));
            return new AuthResultDto(true, pending.Token, pending.ExpiresAt, null, null, null, null, await TenantListAsync(user.Id, cancellationToken));
        }

        if (await users.IsInRoleAsync(user, SystemRoleKeys.PlatformAdminRole))
        {
            return await IssueAsync(user, null, request.RememberMe, cancellationToken: cancellationToken);
        }

        throw new AuthenticationFailedException("This account doesn't have access to any restaurant yet.");
    }

    public async Task<AuthResultDto> SelectTenantAsync(SelectTenantRequest request, CancellationToken cancellationToken = default)
    {
        var userId = tenant.RequireUserId();
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new AuthenticationFailedException();

        var membership = await ActiveMembershipsQuery(userId).FirstOrDefaultAsync(m => m.TenantId == request.TenantId, cancellationToken)
            ?? throw new ForbiddenAccessException("You don't have access to this restaurant.");

        audit.Record(AuditActions.Login, nameof(AppUser), user.Id, tenantId: membership.TenantId, actorUserId: user.Id);
        return await IssueAsync(user, membership, request.RememberMe, cancellationToken: cancellationToken);
    }

    public async Task<AuthResultDto> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == Secrets.Hash(request.RefreshToken), cancellationToken)
            ?? throw new AuthenticationFailedException("Your session has ended. Sign in again.");

        if (token.RevokedAt is not null)
        {
            // A rotated token used again after the grace window: someone else has a copy. End every session.
            if (token.ReplacedByTokenHash is not null && now - token.RevokedAt > TimeSpan.FromSeconds(_settings.RefreshReuseGraceSeconds))
            {
                await RevokeAllSessionsAsync(token.UserId, now, cancellationToken);
                logger.LogWarning("Refresh token reuse detected for user {UserId}; all sessions revoked", token.UserId);
            }
            throw new AuthenticationFailedException("Your session has ended. Sign in again.");
        }

        if (token.ExpiresAt <= now)
        {
            throw new AuthenticationFailedException("Your session has ended. Sign in again.");
        }

        token.RevokedAt = now;

        var user = await users.FindByIdAsync(token.UserId.ToString());
        StaffMembership? membership = null;
        if (token.MembershipId is { } membershipId)
        {
            membership = await ActiveMembershipsQuery(token.UserId).FirstOrDefaultAsync(m => m.Id == membershipId, cancellationToken);
        }

        var stillAllowed = user is not null && (membership is not null
            || (token.MembershipId is null && await users.IsInRoleAsync(user, SystemRoleKeys.PlatformAdminRole)));
        if (!stillAllowed)
        {
            await db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException("Your access has changed. Sign in again.");
        }

        Guid? deviceBranchId = null;
        if (token.DeviceId is { } deviceId)
        {
            var device = await db.Devices.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
            if (device is null || device.RevokedAt is not null)
            {
                await db.SaveChangesAsync(cancellationToken);
                throw new AuthenticationFailedException("This device is no longer registered. Sign in again.");
            }
            deviceBranchId = device.BranchId;
        }

        // Remembered sessions slide forward with use; short and PIN sessions keep their end time.
        return await IssueAsync(user!, membership, token.IsPersistent,
            fixedExpiry: token.IsPersistent ? null : token.ExpiresAt,
            replaces: token,
            deviceId: token.DeviceId,
            defaultBranchId: deviceBranchId,
            cancellationToken: cancellationToken);
    }

    public async Task LogoutAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == Secrets.Hash(request.RefreshToken), cancellationToken);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        // Same response whether or not the account exists, so this can't be used to find accounts.
        var user = await users.FindByEmailAsync(LoginNames.NormalizeEmail(request.Email));
        if (user is null)
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        await resetNotifier.SendPasswordResetAsync(user, token, cancellationToken);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var user = await users.FindByEmailAsync(LoginNames.NormalizeEmail(request.Email))
            ?? throw new AuthenticationFailedException("This reset link is invalid or has expired.");

        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
        {
            throw new AuthenticationFailedException("This reset link is invalid or has expired.");
        }
        ThrowIfFailed(result);

        await users.SetLockoutEndDateAsync(user, null);
        await RevokeAllSessionsAsync(user.Id, DateTimeOffset.UtcNow, cancellationToken);
        audit.Record(AuditActions.PasswordReset, nameof(AppUser), user.Id, actorUserId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var user = await users.FindByIdAsync(tenant.RequireUserId().ToString()) ?? throw new AuthenticationFailedException();
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            throw new ValidationAppException(nameof(request.CurrentPassword), "The current password is incorrect.");
        }
        ThrowIfFailed(result);

        audit.Record(AuditActions.PasswordChanged, nameof(AppUser), user.Id);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthResultDto> AcceptInviteAsync(AcceptInviteRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var hash = Secrets.HashInviteCode(request.Code);
        var membership = await db.Memberships.IgnoreQueryFilters()
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.InviteCodeHash == hash, cancellationToken);

        if (membership?.User is null
            || membership.InviteExpiresAt < now
            || membership.Status == MembershipStatus.Disabled
            || !await db.Tenants.AnyAsync(t => t.Id == membership.TenantId && t.Status == TenantStatus.Active, cancellationToken))
        {
            throw new AuthenticationFailedException("This invite code is invalid or has expired.");
        }

        var user = membership.User;
        var passwordReplaced = false;

        if (await users.HasPasswordAsync(user))
        {
            // Someone who also works elsewhere keeps their password: an owner's invite must never be a
            // way into another restaurant's account. Otherwise the invite doubles as an owner-assisted reset.
            var worksElsewhere = await db.Memberships.IgnoreQueryFilters().AnyAsync(
                m => m.UserId == user.Id && m.TenantId != membership.TenantId && m.Status != MembershipStatus.Disabled, cancellationToken);

            if (worksElsewhere)
            {
                if (!await users.CheckPasswordAsync(user, request.Password))
                {
                    throw new AuthenticationFailedException("Enter the password you already use to sign in.");
                }
            }
            else
            {
                var resetToken = await users.GeneratePasswordResetTokenAsync(user);
                ThrowIfFailed(await users.ResetPasswordAsync(user, resetToken, request.Password));
                passwordReplaced = true;
            }
        }
        else
        {
            ThrowIfFailed(await users.AddPasswordAsync(user, request.Password));
        }

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);

        if (passwordReplaced)
        {
            await RevokeAllSessionsAsync(user.Id, now, cancellationToken);
        }

        membership.Status = MembershipStatus.Active;
        membership.InviteCodeHash = null;
        membership.InviteExpiresAt = null;
        audit.Record(AuditActions.StaffInviteAccepted, nameof(StaffMembership), membership.Id, tenantId: membership.TenantId, actorUserId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        access.InvalidateTenant(membership.TenantId);

        return await IssueAsync(user, membership, persistent: true, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceStaffDto>> GetDeviceStaffAsync(string deviceToken, CancellationToken cancellationToken = default)
    {
        var device = await FindActiveDeviceAsync(deviceToken, cancellationToken)
            ?? throw new AuthenticationFailedException("This device isn't registered for PIN sign-in.");

        return await PinMembershipsQuery(device)
            .OrderBy(m => m.DisplayName)
            .Select(m => new DeviceStaffDto(m.Id, m.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public async Task<AuthResultDto> PinLoginAsync(PinLoginRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var device = await FindActiveDeviceAsync(request.DeviceToken, cancellationToken)
            ?? throw new AuthenticationFailedException("This device isn't registered for PIN sign-in.");

        var membership = await PinMembershipsQuery(device)
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == request.MembershipId, cancellationToken)
            ?? throw new AuthenticationFailedException("Incorrect PIN.");

        if (membership.PinLockedUntil > now)
        {
            throw new AuthenticationFailedException("Too many wrong PINs. Try again in a few minutes.");
        }

        if (pinHasher.VerifyHashedPassword(membership.User!, membership.PinHash!, request.Pin) == PasswordVerificationResult.Failed)
        {
            membership.PinFailedCount++;
            if (membership.PinFailedCount >= _settings.PinMaxAttempts)
            {
                membership.PinLockedUntil = now.AddMinutes(_settings.PinLockMinutes);
                membership.PinFailedCount = 0;
            }
            await db.SaveChangesAsync(cancellationToken);
            throw new AuthenticationFailedException("Incorrect PIN.");
        }

        membership.PinFailedCount = 0;
        membership.PinLockedUntil = null;
        device.LastSeenAt = now;
        audit.Record(AuditActions.PinLogin, nameof(AppUser), membership.UserId, tenantId: device.TenantId, branchId: device.BranchId, actorUserId: membership.UserId);

        return await IssueAsync(membership.User!, membership, persistent: false,
            fixedExpiry: now.AddHours(_settings.PinSessionHours),
            deviceId: device.Id,
            defaultBranchId: device.BranchId,
            cancellationToken: cancellationToken);
    }

    public async Task SetPinAsync(SetPinRequest request, CancellationToken cancellationToken = default)
    {
        await Validate(request, cancellationToken);

        var user = await users.FindByIdAsync(tenant.RequireUserId().ToString()) ?? throw new AuthenticationFailedException();
        if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
        {
            throw new ValidationAppException(nameof(request.CurrentPassword), "The current password is incorrect.");
        }

        var membershipId = tenant.RequireMembershipId();
        var membership = await db.Memberships.FirstAsync(m => m.Id == membershipId, cancellationToken);
        membership.PinHash = pinHasher.HashPassword(user, request.Pin);
        membership.PinFailedCount = 0;
        membership.PinLockedUntil = null;

        audit.Record(AuditActions.StaffPinSet, nameof(StaffMembership), membership.Id);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MeDto> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(tenant.RequireUserId().ToString()) ?? throw new AuthenticationFailedException();
        var isPlatformAdmin = await users.IsInRoleAsync(user, SystemRoleKeys.PlatformAdminRole);

        string? tenantName = null;
        var hasPin = false;
        IReadOnlyList<BranchSummaryDto> branches = [];
        if (tenant.TenantId is { } tenantId)
        {
            tenantName = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(cancellationToken);
            hasPin = await db.Memberships.AnyAsync(m => m.Id == tenant.MembershipId && m.PinHash != null, cancellationToken);
            var allowed = tenant.AllowedBranchIds.ToList();
            branches = await db.Branches
                .Where(b => allowed.Contains(b.Id))
                .OrderBy(b => b.CreatedAt)
                .Select(b => new BranchSummaryDto(b.Id, b.Name))
                .ToListAsync(cancellationToken);
        }

        return new MeDto(
            user.Id,
            user.FullName,
            user.Email,
            user.PhoneNumber,
            isPlatformAdmin,
            tenant.TenantId,
            tenantName,
            tenant.MembershipId,
            tenant.HasFullAccess,
            hasPin,
            tenant.Permissions.Order().ToList(),
            branches,
            await TenantListAsync(user.Id, cancellationToken));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<AuthResultDto> IssueAsync(
        AppUser user,
        StaffMembership? membership,
        bool persistent,
        DateTimeOffset? fixedExpiry = null,
        RefreshToken? replaces = null,
        Guid? deviceId = null,
        Guid? defaultBranchId = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var accessToken = tokens.CreateAccessToken(user.Id, membership?.TenantId, membership?.Id, pendingSelection: false,
            TimeSpan.FromMinutes(_settings.AccessTokenMinutes));

        var rawRefreshToken = Secrets.NewToken();
        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            TenantId = membership?.TenantId,
            MembershipId = membership?.Id,
            DeviceId = deviceId,
            TokenHash = Secrets.Hash(rawRefreshToken),
            ExpiresAt = fixedExpiry ?? (persistent ? now.AddDays(_settings.RefreshTokenDays) : now.AddHours(_settings.SessionRefreshTokenHours)),
            IsPersistent = persistent,
            CreatedByIp = tenant.RemoteIp
        };

        if (replaces is not null)
        {
            replaces.ReplacedByTokenHash = refreshToken.TokenHash;
        }

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(cancellationToken);

        if (membership is not null && defaultBranchId is null)
        {
            defaultBranchId = await DefaultBranchAsync(membership, cancellationToken);
        }

        return new AuthResultDto(false, accessToken.Token, accessToken.ExpiresAt, rawRefreshToken, refreshToken.ExpiresAt,
            membership?.TenantId, defaultBranchId, await TenantListAsync(user.Id, cancellationToken));
    }

    private async Task<Guid?> DefaultBranchAsync(StaffMembership membership, CancellationToken cancellationToken)
    {
        var membershipAccess = await access.GetAsync(membership.TenantId, membership.Id, cancellationToken);
        if (membershipAccess is null || membershipAccess.AllowedBranchIds.Count == 0)
        {
            return null;
        }

        var allowed = membershipAccess.AllowedBranchIds.ToList();
        return await db.Branches.IgnoreQueryFilters()
            .Where(b => b.TenantId == membership.TenantId && allowed.Contains(b.Id) && b.IsActive)
            .OrderBy(b => b.CreatedAt)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private IQueryable<StaffMembership> ActiveMembershipsQuery(Guid userId) =>
        db.Memberships.IgnoreQueryFilters().Where(m =>
            m.UserId == userId
            && m.Status == MembershipStatus.Active
            && db.Tenants.Any(t => t.Id == m.TenantId && t.Status == TenantStatus.Active));

    private async Task<IReadOnlyList<TenantMembershipDto>> TenantListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await ActiveMembershipsQuery(userId)
            .Join(db.Tenants, m => m.TenantId, t => t.Id, (m, t) => new
            {
                m.TenantId,
                TenantName = t.Name,
                MembershipId = m.Id,
                Roles = m.Roles.Select(r => new { r.Role!.Name, r.Role.FullAccess }).ToList()
            })
            .OrderBy(x => x.TenantName)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new TenantMembershipDto(r.TenantId, r.TenantName, r.MembershipId, r.Roles.Select(x => x.Name).Order().ToList(), r.Roles.Any(x => x.FullAccess)))
            .ToList();
    }

    private async Task<AppUser?> FindByLoginAsync(string login, CancellationToken cancellationToken) =>
        LoginNames.IsEmail(login)
            ? await users.FindByEmailAsync(login)
            : await db.Users.FirstOrDefaultAsync(u => u.PhoneNumber == login, cancellationToken);

    private async Task<RegisteredDevice?> FindActiveDeviceAsync(string deviceToken, CancellationToken cancellationToken)
    {
        var hash = Secrets.Hash(deviceToken);
        return await db.Devices.IgnoreQueryFilters().FirstOrDefaultAsync(d =>
            d.TokenHash == hash
            && d.RevokedAt == null
            && db.Tenants.Any(t => t.Id == d.TenantId && t.Status == TenantStatus.Active), cancellationToken);
    }

    /// <summary>Active staff of the device's tenant who set a PIN and may use the device's branch.</summary>
    private IQueryable<StaffMembership> PinMembershipsQuery(RegisteredDevice device) =>
        db.Memberships.IgnoreQueryFilters().Where(m =>
            m.TenantId == device.TenantId
            && m.Status == MembershipStatus.Active
            && m.PinHash != null
            && (m.AllBranches || m.Roles.Any(r => r.Role!.FullAccess) || m.Branches.Any(b => b.BranchId == device.BranchId)));

    private async Task RevokeAllSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in active)
        {
            token.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task Validate<T>(T request, CancellationToken cancellationToken) =>
        services.GetRequiredService<IValidator<T>>().ValidateAndThrowAppExceptionAsync(request, cancellationToken);

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        throw new ValidationAppException(result.Errors.Select(e => new FluentValidation.Results.ValidationFailure(
            e.Code.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "Password"
                : e.Code.Contains("Email", StringComparison.OrdinalIgnoreCase) ? "Email" : "Login",
            e.Description)));
    }
}
