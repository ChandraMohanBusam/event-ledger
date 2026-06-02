using EventGateway.Contracts;
using EventGateway.Validation;
using FluentAssertions;
using Xunit;

namespace EventGateway.Tests;

public class EventValidatorTests
{
    private static SubmitEventRequest Valid() => new(
        "evt-1", "acct-1", "CREDIT", 100m, "USD",
        DateTimeOffset.Parse("2026-05-15T10:00:00Z"), null);

    [Fact]
    public void Valid_request_has_no_errors()
        => EventValidator.Validate(Valid()).Should().BeEmpty();

    [Fact]
    public void Missing_accountId_is_rejected()
        => EventValidator.Validate(Valid() with { AccountId = null })
            .Should().ContainKey(nameof(SubmitEventRequest.AccountId));

    [Fact]
    public void Zero_amount_is_rejected()
        => EventValidator.Validate(Valid() with { Amount = 0m })
            .Should().ContainKey(nameof(SubmitEventRequest.Amount));

    [Fact]
    public void Unknown_type_is_rejected()
        => EventValidator.Validate(Valid() with { Type = "TRANSFER" })
            .Should().ContainKey(nameof(SubmitEventRequest.Type));

    [Fact]
    public void All_missing_fields_are_each_reported()
    {
        var errors = EventValidator.Validate(
            new SubmitEventRequest(null, null, null, null, null, null, null));

        errors.Should().ContainKeys(
            nameof(SubmitEventRequest.EventId),
            nameof(SubmitEventRequest.AccountId),
            nameof(SubmitEventRequest.Type),
            nameof(SubmitEventRequest.Amount),
            nameof(SubmitEventRequest.Currency),
            nameof(SubmitEventRequest.EventTimestamp));
    }
}
