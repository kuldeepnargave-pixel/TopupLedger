namespace TopupLedger.Portal.Models;

/// <summary>
/// Used by the Error.cshtml Razor view.
/// </summary>
public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
