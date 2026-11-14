
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

    public PartnersController(
        TopupLedgerDbContext db,
        IPartnerRepository partners,
        IWalletRepository wallets,
        IPartnerSecretProtector protector,
        WalletAppService walletSvc,
        IUnitOfWork uow,
        ILogger<PartnersController> logger)
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
    public IActionResult Create()
    {
        return View(new CreatePartnerViewModel());
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePartnerViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        // Generate API key and HMAC secret. Show plaintext secret only once.
        string apiKey = "pk_live_" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

        string plaintextSecret = "sk_live_" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

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

        // Create a zero-balance wallet for the new partner.
        var now = DateTime.UtcNow;

        var wallet = new Wallet
        {
            PartnerId = partner.Id,
            BalanceFils = 0,
            Currency = "AED",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
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

        if (partner == null)
            return NotFound();

        var wallet = await _wallets.GetByPartnerAsync(id);

        if (wallet == null)
        {
            _logger.LogWarning(
                "Wallet not found for partner {PartnerId}", id);

            return NotFound("Wallet not found for this partner.");
        }

        ViewBag.PartnerName = partner.Name;
        ViewBag.CurrentBalanceFils = wallet.BalanceFils;

        return View(new ManualCreditViewModel
        {
            PartnerId = id
        });
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Credit(ManualCreditViewModel model)
    {
        if (model.AmountAed <= 0)
        {
            ModelState.AddModelError(
                nameof(model.AmountAed),
                "Amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(model.Reason))
        {
            ModelState.AddModelError(
                nameof(model.Reason),
                "Reason is required.");
        }

        if (!ModelState.IsValid)
        {
            var partner = await _partners.GetByIdAsync(model.PartnerId);
            var wallet = await _wallets.GetByPartnerAsync(model.PartnerId);

            ViewBag.PartnerName = partner?.Name;
            ViewBag.CurrentBalanceFils = wallet?.BalanceFils ?? 0;

            return View(model);
        }

        // Verify partner exists.
        var existingPartner = await _partners.GetByIdAsync(model.PartnerId);

        if (existingPartner == null)
            return NotFound("Partner not found.");

        // Verify wallet exists before starting the transaction.
        var existingWallet = await _wallets.GetByPartnerAsync(model.PartnerId);

        if (existingWallet == null)
        {
            _logger.LogWarning(
                "Cannot manually credit. Wallet not found for partner {PartnerId}",
                model.PartnerId);

            ModelState.AddModelError(
                string.Empty,
                "Wallet not found for this partner.");

            ViewBag.PartnerName = existingPartner.Name;
            ViewBag.CurrentBalanceFils = 0;

            return View(model);
        }

        long amountFils = (long)Math.Round(
            model.AmountAed * 100m,
            MidpointRounding.AwayFromZero);

        var correlationId = $"manual-credit-{Guid.NewGuid():N}";

        await using var txn = await _uow.BeginTransactionAsync();

        try
        {
            // IMPORTANT: Pass PartnerId, not Wallet.Id.
            await _walletSvc.CreditAsync(
                model.PartnerId,
                amountFils,
                0,
                LedgerReason.ManualCredit,
                model.Reason,
                correlationId,
                DateTime.UtcNow);

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
/// </summary>
public sealed class WalletAppService
{
    private readonly TopupLedger.Application.Services.WalletService _inner;

    public WalletAppService(
        TopupLedger.Application.Services.WalletService inner)
    {
        _inner = inner;
    }

    public Task<(Wallet wallet, WalletLedger entry)> CreditAsync(
        long partnerId,
        long amountFils,
        long topupId,
        LedgerReason reason,
        string? reasonText,
        string? correlationId,
        DateTime atUtc,
        CancellationToken ct = default)
    {
        return _inner.CreditAsync(
            partnerId,
            amountFils,
            topupId,
            reason,
            reasonText,
            correlationId,
            atUtc,
            ct);
    }
}
