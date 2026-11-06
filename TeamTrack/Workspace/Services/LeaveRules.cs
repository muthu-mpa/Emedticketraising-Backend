namespace TeamTrack.Workspace.Services
{
    /// <summary>Pure leave / permission rules, kept free of I/O so they can be tested directly.</summary>
    public static class LeaveRules
    {
        public const string Casual = "casual";
        public const string Sick = "sick";
        public const string Earned = "earned";

        public static readonly IReadOnlyDictionary<string, int> YearlyQuota = new Dictionary<string, int>
        {
            [Casual] = 12,
            [Sick] = 6,
            [Earned] = 15,
        };

        public const int PermissionMaxMinutesPerRequest = 120;
        public const int PermissionMaxMinutesPerMonth = 240;
        public const int PermissionStepMinutes = 15;

        public static bool IsLeaveType(string? type) => type != null && YearlyQuota.ContainsKey(type);

        /// <summary>Inclusive day count.</summary>
        public static int CountDays(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber + 1;

        /// <summary>Days of [from, to] that fall inside the given calendar year.</summary>
        public static int DaysInYear(DateOnly from, DateOnly to, int year)
        {
            var start = from > new DateOnly(year, 1, 1) ? from : new DateOnly(year, 1, 1);
            var end = to < new DateOnly(year, 12, 31) ? to : new DateOnly(year, 12, 31);
            return end < start ? 0 : CountDays(start, end);
        }

        public static bool Overlaps(DateOnly aFrom, DateOnly aTo, DateOnly bFrom, DateOnly bTo)
            => aFrom <= bTo && bFrom <= aTo;

        /// <summary>
        /// Validates a leave request. <paramref name="usedApprovedDays"/> returns the approved days already used
        /// for this type in a given year; <paramref name="ownLeave"/> are the user's pending + approved ranges.
        /// Returns an error message, or null when the request is allowed.
        /// </summary>
        public static string? ValidateLeave(
            string? type, DateOnly from, DateOnly to, string? reason,
            Func<int, int> usedApprovedDays,
            IEnumerable<(DateOnly From, DateOnly To)> ownLeave)
        {
            if (!IsLeaveType(type)) return "Leave type must be casual, sick or earned.";
            if (string.IsNullOrWhiteSpace(reason)) return "A reason is required.";
            if (to < from) return "The end date cannot be before the start date.";

            foreach (var range in ownLeave)
            {
                if (Overlaps(from, to, range.From, range.To))
                    return $"This overlaps your existing leave ({range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd}).";
            }

            var err = CheckBalance(type!, from, to, usedApprovedDays);
            return err;
        }

        public static string? CheckBalance(string type, DateOnly from, DateOnly to, Func<int, int> usedApprovedDays)
        {
            for (var year = from.Year; year <= to.Year; year++)
            {
                var requested = DaysInYear(from, to, year);
                var remaining = YearlyQuota[type] - usedApprovedDays(year);
                if (requested > remaining)
                    return $"Not enough {type} leave left for {year}: {requested} day(s) requested, {Math.Max(remaining, 0)} left.";
            }
            return null;
        }

        /// <summary>Parses "HH:mm" or "HH:mm:ss".</summary>
        public static bool TryParseTime(string? value, out TimeOnly time)
            => TimeOnly.TryParseExact(value?.Trim(), new[] { "HH:mm", "H:mm", "HH:mm:ss" },
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);

        /// <summary>
        /// Validates a permission request. <paramref name="usedMinutesInMonth"/> is the user's pending + approved
        /// permission minutes already booked in the month of the request.
        /// </summary>
        public static string? ValidatePermission(TimeOnly start, TimeOnly end, string? reason, int usedMinutesInMonth)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "A reason is required.";
            if (end <= start) return "The end time must be after the start time.";

            var minutes = (int)(end - start).TotalMinutes;
            if (start.Minute % PermissionStepMinutes != 0 || end.Minute % PermissionStepMinutes != 0
                || start.Second != 0 || end.Second != 0)
                return "Times must be in 15-minute steps.";
            if (minutes > PermissionMaxMinutesPerRequest) return "Permission can be at most 2 hours per request.";
            if (usedMinutesInMonth + minutes > PermissionMaxMinutesPerMonth)
                return $"Permission is limited to 4 hours per month; you have {Math.Max(PermissionMaxMinutesPerMonth - usedMinutesInMonth, 0) / 60.0:0.##} hour(s) left.";
            return null;
        }
    }
}
