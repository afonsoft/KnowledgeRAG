namespace KnowledgeHub.Server.Auth;

/// <summary>
/// `Auth:*` configuration (SPEC-20260914-auth-login). `AdminInitialPassword`
/// seeds the `admin` account on an empty Users table — the forced-change gate
/// makes the documented default ("123qwe") safe for first boot.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const string DefaultAdminPassword = "123qwe"; // NOSONAR S2068 — default de primeiro boot documentado, troca obrigatória no 1º login (gate MustChangePassword)

    public string? AdminInitialPassword { get; set; }
    public int LockoutThreshold { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 5;
    public int MinPasswordLength { get; set; } = 8;
    public int SessionHours { get; set; } = 12;

    public string EffectiveAdminPassword =>
        string.IsNullOrWhiteSpace(AdminInitialPassword) ? DefaultAdminPassword : AdminInitialPassword;
}
