using OpenCredential.AdminWeb.Services;

internal static class GuestAccessPolicyRegression
{
    public static void Run()
    {
        var defaults = new GuestAccessPolicy();
        Check(!defaults.Enabled && !defaults.AllowLocalIssuance && !defaults.AllowPreparedOffline && defaults.DefaultMinutes == 60 && defaults.MaximumMinutes == 120);
        Reject(() => defaults.EffectiveDuration(60, 120));
        Reject(() => new GuestAccessPolicy(Enabled: true, MaximumMinutes: 121).Validate());
        Reject(() => new GuestAccessPolicy(Enabled: true, DefaultMinutes: 61, MaximumMinutes: 60).Validate());
        var policy = new GuestAccessPolicy(Enabled: true);
        Check(policy.EffectiveDuration(120, 45) == TimeSpan.FromMinutes(45));
        Reject(() => policy.EffectiveDuration(121, 120));
        var now = DateTimeOffset.Parse("2026-10-29T10:00:00Z");
        var device = Guid.NewGuid();
        var grant = new GuestGrant(Guid.NewGuid(), device, now.AddMinutes(30), 60);
        Reject(() => grant.Activate(Guid.NewGuid(), now, policy, 120));
        Reject(() => grant.Activate(device, now.AddMinutes(30), policy, 120));
        Reject(() => grant.Activate(device, now, defaults, 120));
        var active = grant.Activate(device, now, policy, 45);
        Check(active.Status == GuestGrantStatus.Active && active.ExpiresUtc == now.AddMinutes(45));
        Reject(() => active.Activate(device, now.AddMinutes(1), policy, 120));
        Reject(() => active.Expire(now.AddMinutes(44)));
        var expired = active.Expire(now.AddMinutes(45));
        Reject(() => expired.Activate(device, now.AddMinutes(46), policy, 120));
        Check(expired.Close().Status == GuestGrantStatus.Closed);
        var revoked = grant.Revoke();
        Reject(() => revoked.Activate(device, now, policy, 120));
        Reject(() => revoked.Close());
        Check(active.Revoke().Close().Status == GuestGrantStatus.Closed);
        Reject(() => (active with { ExpiresUtc = now.AddMinutes(-1) }).Expire(now));
        Console.WriteLine("PASS Guest policy: disabled by default, scoped single-use transitions, local limit, expiry, revocation and no renewal.");
    }

    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Guest policy regression failed."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Invalid guest policy transition was accepted.");
    }
}
