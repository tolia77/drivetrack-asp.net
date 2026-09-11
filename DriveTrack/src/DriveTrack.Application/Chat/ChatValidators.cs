using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Chat;

/// <summary>
/// FR-70's two rules over a message being sent.
/// <para>
/// Both are judged over the <em>trimmed</em> text, because trimming is what the service stores: a
/// message of exactly 2000 characters with a trailing newline would otherwise be refused for being
/// one character too long when the string that reaches the column fits, and <c>"   "</c> would be
/// accepted as content when what lands is empty. The service trims before it validates, so this
/// class states the rules and the caller states which string they are about.
/// </para>
/// <para>
/// <c>MaximumLength</c> restates <c>MessageConfiguration</c>'s 2000. The column is the authority;
/// stating it here is what turns a truncation error from PostgreSQL into a 422 naming the field,
/// which is what NFR-4 asks for.
/// </para>
/// <para>
/// Discovered by <c>AddValidatorsFromAssembly</c>, so there is no registration to add.
/// </para>
/// </summary>
public sealed class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    /// <summary>The longest message the <c>messages.text</c> column holds.</summary>
    public const int TextMaximumLength = 2000;

    /// <summary>Declares the rules.</summary>
    public SendMessageCommandValidator() =>
        RuleFor(command => command.Text)
            .NotEmpty().WithMessage(nameof(ErrorCode.CHAT_MESSAGE_TEXT_REQUIRED))
            .MaximumLength(TextMaximumLength)
                .WithMessage(nameof(ErrorCode.CHAT_MESSAGE_TEXT_TOO_LONG));
}
