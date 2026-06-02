using EventGateway.Contracts;
using EventGateway.Domain;

namespace EventGateway.Validation;

/// <summary>
/// Field-level validation for a submitted event: presence, positive amount,
/// known type. The Gateway validates before forwarding, so the Account Service
/// receives only well-formed transactions.
/// </summary>
public static class EventValidator
{
    public static Dictionary<string, string[]> Validate(SubmitEventRequest request)
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

        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            Add(nameof(request.EventId), "eventId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            Add(nameof(request.AccountId), "accountId is required.");
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

    public static bool TryParseType(string? value, out EventType type)
    {
        return Enum.TryParse(value, ignoreCase: true, out type)
               && Enum.IsDefined(type);
    }
}
