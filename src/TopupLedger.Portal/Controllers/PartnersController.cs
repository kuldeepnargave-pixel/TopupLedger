using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TopupLedger.Application.Interfaces;
using TopupLedger.Domain.Entities;
using TopupLedger.Domain.Enums;
using TopupLedger.Infrastructure.Data;
using TopupLedger.Portal.Models;

namespace TopupLedger.Portal.Controllers;

[Authorize]
public class PartnersController : Controller
{
    private readonly TopupLedgerDbContext _db;
    private readonly IPartnerRepository _partners;
    private readonly IWalletRepository _wallets;
    private readonly IPartnerSecretProtector _protector;
    private readonly WalletAppService _walletSvc;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<PartnersController> _logger;

    public PartnersController(TopupLedgerDbContext db, IPartnerRepository partners,
        IWalletRepository wallets, IPartnerSecretProtector protector, WalletAppService walletSvc,
        IUnitOfWork uow, ILogger<PartnersController> logger)
    {
        _db = db;
        _partners = partners;
        _wallets = wallets;
        _protector = protector;
        _walletSvc = walletSvc;
        _uow = uow;
        _logger = logger;
    }

    public async Task<IActionResult> Index(int page = 1, int pageSize = 25)
    {
        var list = await _partners.ListAsync(page, pageSize);
        var total = await _partners.CountAsync();
        var partnerIds = list.Select(p => p.Id).ToList();
        var walletBalances = await _db.Wallets.AsNoTracking()
            .Where(w => partnerIds.Contains(w.PartnerId))
            .ToDictionaryAsync(w => w.PartnerId, w => w.BalanceFils);

        var vm = new PartnerListViewModel
        {
            Partners = list.Select(p => new PartnerViewModel
            {
                Id = p.Id,
                Name = p.Name,
                ApiKey = p.ApiKey,
                DistributorId = p.DistributorId,
                Status = p.Status.ToString()
            }).ToList(),
            CurrentPage = page,
            TotalPages = (int)Math.Ceiling((double)total / pageSize)
        };
        return View(vm);
    }

    [HttpGet]
    [Authorize(Roles = "ADMIN")]
    public IActionResult Create() => View(new CreatePartnerViewModel());

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePartnerViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // Generate API key and HMAC secret. Show the plaintext secret ONCE.
        string apiKey = "pk_live_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        string plaintextSecret = "sk_live_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        var (cipher, nonce, tag) = _protector.Encrypt(plaintextSecret);

        var partner = new Partner
        {
            Name = model.Name,
            ApiKey = apiKey,
            SecretCipher = cipher,
            SecretNonce = nonce,
            SecretTag = tag,
            DistributorId = model.DistributorId,
            Status = PartnerStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _partners.CreateAsync(partner);

        // Create a wallet for the partner with zero balance.
        var wallet = new Wallet
        {
            PartnerId = partner.Id,
            BalanceFils = 0,
            Currency = "AED",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync();

        return View("Created", new PartnerCreatedViewModel
        {
            PartnerId = partner.Id,
            ApiKey = apiKey,
            PlaintextSecret = plaintextSecret
        });
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(long id)
    {
        await _partners.UpdateStatusAsync(id, PartnerStatus.Suspended);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(long id)
    {
        await _partners.UpdateStatusAsync(id, PartnerStatus.Active);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> Credit(long id)
    {
        var partner = await _partners.GetByIdAsync(id);
        if (partner == null) return NotFound();
        var wallet = await _wallets.GetByPartnerAsync(id);
        ViewBag.PartnerName = partner.Name;
        ViewBag.CurrentBalanceFils = wallet?.BalanceFils ?? 0;
        return View(new ManualCreditViewModel { PartnerId = id });
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Credit(ManualCreditViewModel model)
    {
        if (model.AmountAed <= 0) ModelState.AddModelError("AmountAed", "Amount must be positive.");
        if (string.IsNullOrWhiteSpace(model.Reason)) ModelState.AddModelError("Reason", "Reason is required.");
        if (!ModelState.IsValid) return View(model);

        long amountFils = (long)Math.Round(model.AmountAed * 100m, MidpointRounding.AwayFromZero);
        var correlationId = $"manual-credit-{Guid.NewGuid():N}";
        await using var txn = await _uow.BeginTransactionAsync();
        try
        {
            await _walletSvc.CreditAsync(
                (await _wallets.GetByPartnerAsync(model.PartnerId))!.Id,
                amountFils, 0, LedgerReason.ManualCredit, model.Reason, correlationId, DateTime.UtcNow);
            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>
/// Thin wrapper around WalletService to expose CreditAsync to the MVC portal.
/// (The portal uses the same WalletService as the API; no duplicate business logic.)
/// </summary>
public sealed class WalletAppService
{
    private readonly TopupLedger.Application.Services.WalletService _inner;
    public WalletAppService(TopupLedger.Application.Services.WalletService inner) => _inner = inner;
    public Task<(Domain.Entities.Wallet wallet, Domain.Entities.WalletLedger entry)> CreditAsync(
        long walletId, long amountFils, long topupId, LedgerReason reason, string? reasonText, string? correlationId, DateTime atUtc, CancellationToken ct = default) =>
        _inner.CreditAsync(walletId, amountFils, topupId, reason, reasonText, correlationId, atUtc, ct);
}
