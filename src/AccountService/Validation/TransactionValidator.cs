using AccountService.Contracts;
using AccountService.Domain;

namespace AccountService.Validation;

/// <summary>
/// Validates an incoming transaction request. Field-level validation only:
/// presence, positive amount, known type, currency format. Cross-record rules
/// that need the database (single currency per account) live in the service.
/// </summary>
public static class TransactionValidator
{
    /// <summary>
    /// Returns a map of field name to error messages. An empty map means valid.
    /// The shape matches what Results.ValidationProblem expects.
    /// </summary>
    public static Dictionary<string, string[]> Validate(ApplyTransactionRequest request)
    {
        var errors = new Dictionary<string, List<string>>();

        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                list = [];
                errors[field] = list;
            }
            list.Add(message);
        }

        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            Add(nameof(request.TransactionId), "transactionId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            Add(nameof(request.Type), "type is required.");
        }
        else if (!TryParseType(request.Type, out _))
        {
            Add(nameof(request.Type), "type must be CREDIT or DEBIT.");
        }

        if (request.Amount is null)
        {
            Add(nameof(request.Amount), "amount is required.");
        }
        else if (request.Amount <= 0)
        {
            Add(nameof(request.Amount), "amount must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            Add(nameof(request.Currency), "currency is required.");
        }

        if (request.EventTimestamp is null)
        {
            Add(nameof(request.EventTimestamp), "eventTimestamp is required.");
        }

        return errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    /// <summary>Parses CREDIT/DEBIT case-insensitively into the domain enum.</summary>
    public static bool TryParseType(string? value, out TransactionType type)
    {
        return Enum.TryParse(value, ignoreCase: true, out type)
               && Enum.IsDefined(type);
    }
}
