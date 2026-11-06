using TeamTrack.Workspace.Services;
using Xunit;

namespace TeamTrack.Tests
{
    public class LeaveRulesTests
    {
        private static DateOnly D(int y, int m, int d) => new(y, m, d);
        private static readonly Func<int, int> NoneUsed = _ => 0;
        private static readonly (DateOnly, DateOnly)[] NoLeave = [];

        [Fact]
        public void Yearly_quotas_are_12_6_15()
        {
            Assert.Equal(12, LeaveRules.YearlyQuota["casual"]);
            Assert.Equal(6, LeaveRules.YearlyQuota["sick"]);
            Assert.Equal(15, LeaveRules.YearlyQuota["earned"]);
        }

        [Fact]
        public void Day_count_is_inclusive()
        {
            Assert.Equal(1, LeaveRules.CountDays(D(2026, 3, 2), D(2026, 3, 2)));
            Assert.Equal(3, LeaveRules.CountDays(D(2026, 3, 2), D(2026, 3, 4)));
        }

        [Fact]
        public void Days_are_split_across_year_boundary()
        {
            Assert.Equal(2, LeaveRules.DaysInYear(D(2026, 12, 30), D(2027, 1, 3), 2026));
            Assert.Equal(3, LeaveRules.DaysInYear(D(2026, 12, 30), D(2027, 1, 3), 2027));
            Assert.Equal(0, LeaveRules.DaysInYear(D(2026, 12, 30), D(2027, 1, 3), 2028));
        }

        [Fact]
        public void End_before_start_is_blocked()
            => Assert.NotNull(LeaveRules.ValidateLeave("casual", D(2026, 3, 5), D(2026, 3, 4), "trip", NoneUsed, NoLeave));

        [Fact]
        public void Reason_is_required()
            => Assert.NotNull(LeaveRules.ValidateLeave("casual", D(2026, 3, 2), D(2026, 3, 2), "  ", NoneUsed, NoLeave));

        [Fact]
        public void Unknown_type_is_blocked()
            => Assert.NotNull(LeaveRules.ValidateLeave("holiday", D(2026, 3, 2), D(2026, 3, 2), "x", NoneUsed, NoLeave));

        [Fact]
        public void Days_over_remaining_balance_are_blocked_and_exact_remaining_is_allowed()
        {
            // sick quota 6, 4 already used -> 2 left
            Assert.Null(LeaveRules.ValidateLeave("sick", D(2026, 3, 2), D(2026, 3, 3), "flu", _ => 4, NoLeave));
            Assert.NotNull(LeaveRules.ValidateLeave("sick", D(2026, 3, 2), D(2026, 3, 4), "flu", _ => 4, NoLeave));
        }

        [Fact]
        public void Overlap_with_own_leave_is_blocked_but_adjacent_days_are_fine()
        {
            var existing = new[] { (D(2026, 3, 10), D(2026, 3, 12)) };
            Assert.NotNull(LeaveRules.ValidateLeave("casual", D(2026, 3, 12), D(2026, 3, 13), "x", NoneUsed, existing)); // shares last day
            Assert.NotNull(LeaveRules.ValidateLeave("casual", D(2026, 3, 8), D(2026, 3, 10), "x", NoneUsed, existing));  // shares first day
            Assert.NotNull(LeaveRules.ValidateLeave("casual", D(2026, 3, 11), D(2026, 3, 11), "x", NoneUsed, existing)); // inside
            Assert.Null(LeaveRules.ValidateLeave("casual", D(2026, 3, 13), D(2026, 3, 14), "x", NoneUsed, existing));
            Assert.Null(LeaveRules.ValidateLeave("casual", D(2026, 3, 8), D(2026, 3, 9), "x", NoneUsed, existing));
        }

        private static TimeOnly T(int h, int m) => new(h, m);

        [Fact]
        public void Permission_of_two_hours_is_allowed_but_longer_is_blocked()
        {
            Assert.Null(LeaveRules.ValidatePermission(T(10, 0), T(12, 0), "dentist", 0));
            Assert.NotNull(LeaveRules.ValidatePermission(T(10, 0), T(12, 15), "dentist", 0));
        }

        [Fact]
        public void Permission_end_must_be_after_start()
        {
            Assert.NotNull(LeaveRules.ValidatePermission(T(10, 0), T(10, 0), "x", 0));
            Assert.NotNull(LeaveRules.ValidatePermission(T(11, 0), T(10, 0), "x", 0));
        }

        [Fact]
        public void Permission_must_use_15_minute_steps()
        {
            Assert.Null(LeaveRules.ValidatePermission(T(10, 15), T(11, 45), "x", 0));
            Assert.NotNull(LeaveRules.ValidatePermission(T(10, 10), T(11, 0), "x", 0));
        }

        [Fact]
        public void Permission_is_limited_to_four_hours_per_month()
        {
            Assert.Null(LeaveRules.ValidatePermission(T(10, 0), T(11, 30), "x", 150));    // 150 + 90 = 240
            Assert.NotNull(LeaveRules.ValidatePermission(T(10, 0), T(11, 30), "x", 151)); // 241
        }

        [Fact]
        public void Permission_reason_is_required()
            => Assert.NotNull(LeaveRules.ValidatePermission(T(10, 0), T(11, 0), "", 0));

        [Theory]
        [InlineData("09:30", true)]
        [InlineData("9:30", true)]
        [InlineData("09:30:00", true)]
        [InlineData("25:00", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Times_parse_as_HH_mm(string? value, bool ok)
            => Assert.Equal(ok, LeaveRules.TryParseTime(value, out _));
    }
}
