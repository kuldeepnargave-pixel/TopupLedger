namespace TopupLedger.Portal.Models;

public class LoginViewModel
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public class DashboardViewModel
{
    public DateTime BusinessDay { get; set; }
    public long TotalCount { get; set; }
    public long TotalFaceValueFils { get; set; }
    public Dictionary<string, long> StatusCounts { get; set; } = new();
    public Dictionary<string, long> OperatorCounts { get; set; } = new();
}

public class PartnerListViewModel
{
    public List<PartnerViewModel> Partners { get; set; } = new();
    public int TotalPages { get; set; }
    public int CurrentPage { get; set; }
}

public class PartnerViewModel
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public long? DistributorId { get; set; }
    public string? DistributorName { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public class CreatePartnerViewModel
{
    public string Name { get; set; } = string.Empty;
    public long? DistributorId { get; set; }
}

public class PartnerCreatedViewModel
{
    public long PartnerId { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Shown ONLY ONCE at creation.</summary>
    public string PlaintextSecret { get; set; } = string.Empty;
}

public class ManualCreditViewModel
{
    public long PartnerId { get; set; }
    public decimal AmountAed { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class TransactionsSearchViewModel
{
    public string? Msisdn { get; set; }
    public string? ClientRef { get; set; }
    public string? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class TransactionDetailViewModel
{
    public TopupLedger.Domain.Entities.Topup Topup { get; set; } = null!;
    public IReadOnlyList<TopupLedger.Domain.Entities.WalletLedger> LedgerEntries { get; set; } = Array.Empty<TopupLedger.Domain.Entities.WalletLedger>();
    public IReadOnlyList<TopupLedger.Domain.Entities.CallbackHistory> Callbacks { get; set; } = Array.Empty<TopupLedger.Domain.Entities.CallbackHistory>();
}

public class ReconciliationUploadViewModel
{
    public IFormFile? File { get; set; }
    public string Operator { get; set; } = "etisalat";
}

public class SettlementGenerateViewModel
{
    public string BusinessDay { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
}

public class ReportFilterViewModel
{
    public string? From { get; set; }
    public string? To { get; set; }
    public long? PartnerId { get; set; }
    public int? Limit { get; set; }
    public string? BusinessDay { get; set; }
    public int? Days { get; set; }
    public string? AsOf { get; set; }
}
