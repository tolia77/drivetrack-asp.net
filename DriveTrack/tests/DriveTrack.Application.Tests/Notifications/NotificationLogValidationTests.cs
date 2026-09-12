using DriveTrack.Application.Notifications;

namespace DriveTrack.Application.Tests.Notifications;

/// <summary>
/// The one rule the notification log has of its own: the page it is asked for.
/// <para>
/// Asserted here rather than over HTTP because this is a rule about a value, and a suite that
/// needed Docker to find out that a limit of zero is refused would be a suite nobody runs while
/// they are writing the rule. The endpoint suite asserts that the refusal reaches the wire with the
/// right status and error code; this asserts that it is a refusal at all.
/// </para>
/// </summary>
public class NotificationLogValidationTests
{
    private const int Maximum = ListNotificationAttemptsQueryValidator.MaximumLimit;

    [Theory]
    [InlineData(0, Maximum, true)]
    [InlineData(0, 1, true)]
    [InlineData(Maximum / 2, Maximum, true)]
    [InlineData(-1, Maximum, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, Maximum + 1, false)]
    public void The_paging_parameters_are_validated_before_a_query_is_built(
        int offset,
        int limit,
        bool valid)
    {
        // NFR-27: a limit of two million is refused here rather than handed to the database, and a
        // limit of zero is a refusal rather than "give me nothing". The twin of the delivery list's
        // theory, and the log is the list that most needed it: the table grows by a row per status
        // change per delivery and nothing prunes it.
        var result = new ListNotificationAttemptsQueryValidator()
            .Validate(new ListNotificationAttemptsQuery(offset, limit));

        Assert.Equal(valid, result.IsValid);
    }
}
