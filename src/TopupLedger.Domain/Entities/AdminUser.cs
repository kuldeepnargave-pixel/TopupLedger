using TopupLedger.Domain.Enums;

namespace TopupLedger.Domain.Entities;

/// <summary>
/// Admin user for the MVC portal. Passwords are stored BCrypt-hashed.
/// Roles: ADMIN (full control) | VIEWER (read-only; reports except wallet/statement).
/// </summary>
public class AdminUser
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;  // BCrypt hash
    public AdminRole Role { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}
