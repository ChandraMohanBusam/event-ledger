using AccountService.Contracts;
using AccountService.Validation;
using FluentAssertions;
using Xunit;

namespace AccountService.Tests;

public class TransactionValidatorTests
{
    private static ApplyTransactionRequest Valid()
        => new("evt-1", "CREDIT", 100m, "USD", DateTimeOffset.Parse("2026-05-15T10:00:00Z"));

    [Fact]
    public void Valid_request_has_no_errors()
    {
        TransactionValidator.Validate(Valid()).Should().BeEmpty();
    }

    [Fact]
    public void Zero_amount_is_rejected()
    {
        var errors = TransactionValidator.Validate(Valid() with { Amount = 0m });
        errors.Should().ContainKey(nameof(ApplyTransactionRequest.Amount));
    }

    [Fact]
    public void Negative_amount_is_rejected()
    {
        var errors = TransactionValidator.Validate(Valid() with { Amount = -5m });
        errors.Should().ContainKey(nameof(ApplyTransactionRequest.Amount));
    }

    [Fact]
    public void Unknown_type_is_rejected()
    {
        var errors = TransactionValidator.Validate(Valid() with { Type = "TRANSFER" });
        errors.Should().ContainKey(nameof(ApplyTransactionRequest.Type));
    }

    [Fact]
    public void Missing_fields_are_each_reported()
    {
        var errors = TransactionValidator.Validate(
            new ApplyTransactionRequest(null, null, null, null, null));

        errors.Should().ContainKeys(
            nameof(ApplyTransactionRequest.TransactionId),
            nameof(ApplyTransactionRequest.Type),
            nameof(ApplyTransactionRequest.Amount),
            nameof(ApplyTransactionRequest.Currency),
            nameof(ApplyTransactionRequest.EventTimestamp));
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("CREDIT")]
    [InlineData("Debit")]
    public void Type_parsing_is_case_insensitive(string type)
    {
        TransactionValidator.TryParseType(type, out _).Should().BeTrue();
    }
}
