namespace GoogleTasksDesktopWidget.Core;

public enum RecurrenceFrequency { None, Daily, Weekly, Monthly }

public static class RecurrencePolicy
{
    public static bool CanAutomaticallyCreate(RecurrenceFrequency frequency, bool pending) =>
        frequency != RecurrenceFrequency.None && !pending;

    public static DateOnly Next(DateOnly from, RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Daily => from.AddDays(1),
        RecurrenceFrequency.Weekly => from.AddDays(7),
        RecurrenceFrequency.Monthly => from.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency))
    };

    public static DateOnly NextAfter(DateOnly from, DateOnly today, RecurrenceFrequency frequency)
    {
        if (frequency == RecurrenceFrequency.None) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (frequency == RecurrenceFrequency.Monthly)
        {
            var months = 1;
            DateOnly result;
            do result = from.AddMonths(months++);
            while (result <= today);
            return result;
        }

        var step = frequency == RecurrenceFrequency.Daily ? 1 : 7;
        var periods = Math.Max(1, (today.DayNumber - from.DayNumber) / step + 1);
        return from.AddDays(periods * step);
    }

}
