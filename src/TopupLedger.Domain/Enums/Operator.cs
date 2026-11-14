namespace TopupLedger.Domain.Enums;

public enum Operator
{
    Etisalat = 1,
    Du = 2
}

public static class OperatorExtensions
{
    public static string ToCode(this Operator op) => op switch
    {
        Operator.Etisalat => "etisalat",
        Operator.Du => "du",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    public static Operator FromCode(string code) => code?.ToLowerInvariant() switch
    {
        "etisalat" => Operator.Etisalat,
        "du" => Operator.Du,
        _ => throw new ArgumentException($"Unknown operator code: {code}")
    };
}
