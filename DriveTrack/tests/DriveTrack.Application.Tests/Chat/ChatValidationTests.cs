using DriveTrack.Application.Chat;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Tests.Chat;

/// <summary>
/// The validation rows of story 8.1's edge-case matrix, asserted against
/// <see cref="SendMessageCommandValidator"/>.
/// <para>
/// Asserted here rather than over the hub because that is where the rule lives and where it is
/// cheapest to be exhaustive: the boundary cases — exactly 2000 accepted, 2001 refused, three
/// spaces refused — are four assertions against a validator and four container starts against a
/// socket. The hub suite still drives one of each end to end, which is what proves the rule is
/// actually reached.
/// </para>
/// <para>
/// Every case states the <em>trimmed</em> text, because trimming is what the service does before it
/// validates: the validator judges the string that will reach the column, and a rule judged against
/// anything else would accept three spaces as content and store nothing.
/// </para>
/// </summary>
public class ChatValidationTests
{
    private static readonly DriverId Thread = new(7);

    [Fact]
    public void A_message_with_text_is_accepted()
    {
        Assert.Empty(Failures("привіт"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_message_with_no_text_is_refused_naming_the_field(string? text)
    {
        // FR-70. An empty box is a refusal to state rather than a value to guess at, which is why
        // the command's Text is nullable and why both shapes land on the same rule.
        Assert.Contains(nameof(SendMessageCommand.Text), Failures(text));
        Assert.Contains(nameof(ErrorCode.CHAT_MESSAGE_TEXT_REQUIRED), Keys(text));
    }

    [Fact]
    public void A_message_of_nothing_but_spaces_is_refused_as_empty()
    {
        // The service trims before it validates, so what reaches this rule is the empty string.
        // Stated as its own case because the matrix states it as its own row: a whitespace message
        // that passed would be a row in the table nobody can read and nobody wrote.
        Assert.Contains(nameof(ErrorCode.CHAT_MESSAGE_TEXT_REQUIRED), Keys("   ".Trim()));
    }

    [Fact]
    public void A_message_of_exactly_the_column_width_is_accepted()
    {
        // The ceiling is inclusive, and saying so is what stops an off-by-one turning the longest
        // legal message into a refusal nobody can explain.
        Assert.Empty(Failures(new string('я', SendMessageCommandValidator.TextMaximumLength)));
    }

    [Fact]
    public void A_message_one_character_wider_than_the_column_is_refused_before_the_commit()
    {
        // The validator's limit is MessageConfiguration's, which is what turns a truncation
        // PostgreSQL would raise into a 422 naming the field (NFR-4).
        var text = new string('я', SendMessageCommandValidator.TextMaximumLength + 1);

        Assert.Contains(nameof(SendMessageCommand.Text), Failures(text));
        Assert.Contains(nameof(ErrorCode.CHAT_MESSAGE_TEXT_TOO_LONG), Keys(text));
    }

    /// <summary>The offending property names of a send, deduplicated.</summary>
    private static string[] Failures(string? text) =>
    [
        .. Validate(text).Select(failure => failure.PropertyName).Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The message keys of a send's failures. A validator's message is a resource key rather than a
    /// sentence, and the key is the half a field name cannot show — it is what tells "too long"
    /// from "missing" on a screen that only ever sees one offending field.
    /// </summary>
    private static string[] Keys(string? text) =>
    [
        .. Validate(text).Select(failure => failure.ErrorMessage).Distinct(StringComparer.Ordinal),
    ];

    private static IEnumerable<FluentValidation.Results.ValidationFailure> Validate(string? text) =>
        new SendMessageCommandValidator()
            .Validate(new SendMessageCommand(Thread, text))
            .Errors;
}
