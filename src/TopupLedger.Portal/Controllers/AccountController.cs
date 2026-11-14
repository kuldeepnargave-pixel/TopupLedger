using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TopupLedger.Application.Interfaces;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

/// <summary>
/// Cookie-based login/logout for the MVC portal. Two roles: ADMIN, VIEWER.
/// Passwords are BCrypt-hashed; the AdminUserRepository reads from admin_users table.
/// </summary>
public class AccountController : Controller
{
    private readonly IAdminUserRepository _users;
    private readonly ILogger<AccountController> _logger;
    private readonly BCryptPasswordHasher _hasher;

    public AccountController(IAdminUserRepository users, ILogger<AccountController> logger, BCryptPasswordHasher hasher)
    {
        _users = users;
        _logger = logger;
        _hasher = hasher;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login() => View();

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _users.GetByUsernameAsync(model.Username);
        if (user == null || !_hasher.Verify(model.Password, user.PasswordHash))
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return View(model);
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, user.Role.ToString().ToUpperInvariant())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = model.RememberMe, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });

        await _users.UpdateLastLoginAsync(user.Id, DateTime.UtcNow);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
}

public sealed class BCryptPasswordHasher
{
    public string Hash(string plaintext) => BCrypt.Net.BCrypt.HashPassword(plaintext, workFactor: 11);
    public bool Verify(string plaintext, string hash) => BCrypt.Net.BCrypt.Verify(plaintext, hash);
}
