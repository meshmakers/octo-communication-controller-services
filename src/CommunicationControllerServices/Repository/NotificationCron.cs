namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

internal static class NotificationCron
{
    public static bool FiresAtMostOncePerMinute(string cronExpression)
    {
        var fields = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 5)
        {
            return true;
        }

        return fields.Length is 6 or 7 && int.TryParse(fields[0], out var second) && second is >= 0 and <= 59;
    }
}
