namespace OpenCredential.AdminWeb.Services;

// Domain rules only. This type does not authorize a Windows logon or persist an entitlement.
public sealed record GuestAccessPolicy(
    bool Enabled = false,
    bool AllowLocalIssuance = false,
    bool AllowPreparedOffline = false,
    int DefaultMinutes = 60,
    int MaximumMinutes = 120)
{
    public GuestAccessPolicy Validate()
    {
        if (MaximumMinutes is < 1 or > 120 || DefaultMinutes < 1 || DefaultMinutes > MaximumMinutes)
            throw new ArgumentOutOfRangeException(nameof(MaximumMinutes), "Guest duration must be between 1 and 120 minutes and default must not exceed maximum.");
        return this;
    }

    public TimeSpan EffectiveDuration(int requestedMinutes, int localMaximumMinutes)
    {
        Validate();
        if (!Enabled) throw new InvalidOperationException("Guest access is disabled by policy.");
        if (requestedMinutes < 1 || requestedMinutes > MaximumMinutes || localMaximumMinutes is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(requestedMinutes), "Invalid guest duration or local maximum.");
        return TimeSpan.FromMinutes(Math.Min(requestedMinutes, localMaximumMinutes));
    }
}

public enum GuestGrantStatus { Pending, Active, Revoked, Expired, Closed }

// Immutable transitions must ultimately run under a database transaction/CAS; this in-memory model
// alone does not prevent two devices or processes from consuming the same grant concurrently.
public sealed record GuestGrant(
    Guid Id, Guid DeviceId, DateTimeOffset RedeemBeforeUtc, int DurationMinutes,
    GuestGrantStatus Status = GuestGrantStatus.Pending, DateTimeOffset? ActivatedUtc = null,
    DateTimeOffset? ExpiresUtc = null)
{
    public GuestGrant Activate(Guid deviceId, DateTimeOffset nowUtc, GuestAccessPolicy policy, int localMaximumMinutes)
    {
        if (Id == Guid.Empty || DeviceId == Guid.Empty || deviceId != DeviceId)
            throw new InvalidOperationException("Grant is not assigned to this device.");
        if (Status != GuestGrantStatus.Pending || ActivatedUtc != null || ExpiresUtc != null)
            throw new InvalidOperationException("Grant has already been used or is not pending.");
        if (nowUtc >= RedeemBeforeUtc)
            throw new InvalidOperationException("Grant activation window has expired.");
        var duration = policy.EffectiveDuration(DurationMinutes, localMaximumMinutes);
        return this with { Status = GuestGrantStatus.Active, ActivatedUtc = nowUtc.ToUniversalTime(), ExpiresUtc = nowUtc.ToUniversalTime().Add(duration) };
    }

    public GuestGrant Expire(DateTimeOffset nowUtc)
    {
        if (Status != GuestGrantStatus.Active || !ActivatedUtc.HasValue || !ExpiresUtc.HasValue ||
            ExpiresUtc <= ActivatedUtc || nowUtc < ExpiresUtc)
            throw new InvalidOperationException("Grant is not an expired active session.");
        return this with { Status = GuestGrantStatus.Expired };
    }

    public GuestGrant Revoke()
    {
        if (Status is not (GuestGrantStatus.Pending or GuestGrantStatus.Active))
            throw new InvalidOperationException("Grant cannot be revoked in its current state.");
        return this with { Status = GuestGrantStatus.Revoked };
    }

    public GuestGrant Close()
    {
        if (Status is not (GuestGrantStatus.Active or GuestGrantStatus.Expired or GuestGrantStatus.Revoked) || !ActivatedUtc.HasValue)
            throw new InvalidOperationException("Grant has no active or previously active session to close.");
        return this with { Status = GuestGrantStatus.Closed };
    }
}
