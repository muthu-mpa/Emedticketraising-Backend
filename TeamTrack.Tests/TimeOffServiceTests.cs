using TeamTrack.Workspace.Dtos;
using Xunit;
using static TeamTrack.Tests.Check;

namespace TeamTrack.Tests
{
    public class TimeOffServiceTests
    {
        private static DateOnly D(int m, int d) => new(2026, m, d);

        private static LeaveRequestInput Leave(string type, DateOnly from, DateOnly to, string reason = "personal")
            => new() { LeaveType = type, FromDate = from, ToDate = to, Reason = reason };

        private static PermissionRequestInput Perm(DateOnly date, string start, string end, string reason = "errand")
            => new() { Date = date, StartTime = start, EndTime = end, Reason = reason };

        private static int Remaining(BalancesDto b, string type) => b.Leave.First(l => l.LeaveType == type).Remaining;

        // ---------- balance ----------

        [Fact]
        public async Task Balance_is_deducted_only_when_the_request_is_approved()
        {
            var k = new Kit();
            var req = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 4)))); // 3 days

            Assert.Equal(3, req.Days);
            Assert.Equal(12, Remaining(await k.TimeOff.BalancesAsync(k.Asha.Id), "casual")); // pending: untouched

            Ok(await k.TimeOff.ApproveAsync(k.Priya, req.Id));
            Assert.Equal(9, Remaining(await k.TimeOff.BalancesAsync(k.Asha.Id), "casual"));
        }

        [Fact]
        public async Task Rejected_and_cancelled_requests_never_touch_the_balance()
        {
            var k = new Kit();
            var a = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 4))));
            var b = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(4, 6), D(4, 7))));
            Ok(await k.TimeOff.RejectAsync(k.Priya, a.Id, "busy week"));
            Ok(await k.TimeOff.CancelAsync(k.Asha, b.Id));
            Assert.Equal(12, Remaining(await k.TimeOff.BalancesAsync(k.Asha.Id), "casual"));
        }

        [Fact]
        public async Task Request_is_blocked_when_days_exceed_the_remaining_balance()
        {
            var k = new Kit();
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", D(3, 2), D(3, 8))), 400, "not enough"); // 7 > 6

            var big = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", D(3, 2), D(3, 5)))); // 4
            Ok(await k.TimeOff.ApproveAsync(k.Priya, big.Id)); // 2 left
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", D(5, 4), D(5, 6))), 400, "not enough"); // 3 > 2
            Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", D(5, 4), D(5, 5)))); // 2 == 2
        }

        [Fact]
        public async Task Balance_is_rechecked_at_approval_so_two_pending_requests_cannot_overdraw()
        {
            var k = new Kit();
            var first = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", D(3, 2), D(3, 5)))); // 4
            var second = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", D(6, 1), D(6, 3)))); // 3 (each fits alone: 6)

            Ok(await k.TimeOff.ApproveAsync(k.Priya, first.Id));
            Fails(await k.TimeOff.ApproveAsync(k.Priya, second.Id), 409, "not enough");
            Assert.Equal(2, Remaining(await k.TimeOff.BalancesAsync(k.Asha.Id), "sick"));
        }

        [Theory]
        [InlineData("2026-03-10", "2026-03-12", false)] // identical
        [InlineData("2026-03-12", "2026-03-14", false)] // shares an end day
        [InlineData("2026-03-13", "2026-03-14", true)]
        public async Task Overlap_with_own_pending_leave_is_blocked(string from, string to, bool allowed)
        {
            var k = new Kit();
            Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 10), D(3, 12))));
            var result = await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("sick", DateOnly.Parse(from), DateOnly.Parse(to)));
            Assert.Equal(allowed, result.Success);
            if (!allowed) Fails(result, 400, "overlap");
        }

        [Fact]
        public async Task Overlap_with_own_approved_leave_is_blocked_across_leave_types()
        {
            var k = new Kit();
            var approved = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("earned", D(3, 10), D(3, 12))));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, approved.Id));
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 12), D(3, 13))), 400, "overlap");
        }

        [Fact]
        public async Task Rejected_or_cancelled_leave_does_not_block_new_requests_on_the_same_days()
        {
            var k = new Kit();
            var a = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 10), D(3, 12))));
            Ok(await k.TimeOff.CancelAsync(k.Asha, a.Id));
            var b = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 10), D(3, 12))));
            Ok(await k.TimeOff.RejectAsync(k.Priya, b.Id, "no cover"));
            Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 10), D(3, 12))));
        }

        [Fact]
        public async Task Other_peoples_leave_is_info_not_a_block()
        {
            var k = new Kit();
            var ben = Ok(await k.TimeOff.RequestLeaveAsync(k.Ben, Leave("casual", D(3, 10), D(3, 12))));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, ben.Id));

            Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 11), D(3, 11)))); // not blocked
            var info = Ok(await k.TimeOff.OverlapsAsync(k.Asha, D(3, 11), D(3, 11)));
            Assert.Equal("Ben", Assert.Single(info).UserName);
        }

        [Fact]
        public async Task Leave_validation_errors()
        {
            var k = new Kit();
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 5), D(3, 4))), 400);
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 4), D(3, 5), " ")), 400, "reason");
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("holiday", D(3, 4), D(3, 5))), 400);
            Fails(await k.TimeOff.RequestLeaveAsync(k.Asha, new LeaveRequestInput { LeaveType = "casual", Reason = "x" }), 400, "dates");
        }

        // ---------- permission ----------

        [Fact]
        public async Task Permission_is_capped_at_two_hours_per_request()
        {
            var k = new Kit();
            Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 2), "10:00", "12:00")));
            Fails(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 3), "10:00", "12:15")), 400, "2 hours");
            Fails(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 3), "12:00", "10:00")), 400, "after");
        }

        [Fact]
        public async Task Permission_is_capped_at_four_hours_per_month_counting_pending_and_approved()
        {
            var k = new Kit();
            var first = Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 2), "10:00", "12:00"))); // pending 2h
            Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 9), "10:00", "11:30")));           // pending 3.5h total
            Fails(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 16), "10:00", "11:00")), 400, "4 hours");
            Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 16), "10:00", "10:30")));          // exactly 4h
            Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(4, 2), "10:00", "12:00")));           // other month is fine

            // cancelling frees the hours again
            Ok(await k.TimeOff.CancelAsync(k.Asha, first.Id));
            Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 20), "10:00", "11:30")));
        }

        [Fact]
        public async Task Permission_requires_a_reason_and_valid_times()
        {
            var k = new Kit();
            Fails(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 2), "10:00", "11:00", "")), 400, "reason");
            Fails(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 2), "10:00", "nonsense")), 400);
            Fails(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(3, 2), "10:10", "11:00")), 400, "15-minute");
        }

        [Fact]
        public async Task Permission_does_not_consume_leave_balance_and_reports_hours_left()
        {
            var k = new Kit();
            var p = Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, Perm(D(11, 2), "10:00", "11:30")));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, p.Id));
            var b = await k.TimeOff.BalancesAsync(k.Asha.Id); // clock is in November 2026
            Assert.Equal(12, Remaining(b, "casual"));
            Assert.Equal(2.5, b.PermissionHoursLeftThisMonth);
        }

        // ---------- decisions ----------

        [Fact]
        public async Task Rejecting_needs_a_reason_which_the_requester_can_see()
        {
            var k = new Kit();
            var r = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 3))));
            Fails(await k.TimeOff.RejectAsync(k.Priya, r.Id, "  "), 400, "reason");
            Ok(await k.TimeOff.RejectAsync(k.Priya, r.Id, "release week"));

            var mine = Ok(await k.TimeOff.MineAsync(k.Asha));
            Assert.Equal("rejected", mine[0].Status);
            Assert.Equal("release week", mine[0].DecisionReason);
        }

        [Fact]
        public async Task Cancel_is_only_for_your_own_pending_requests()
        {
            var k = new Kit();
            var r = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 3))));
            Fails(await k.TimeOff.CancelAsync(k.Ben, r.Id), 403);
            Fails(await k.TimeOff.CancelAsync(k.Priya, r.Id), 403); // even a PM cannot cancel someone else's

            Ok(await k.TimeOff.ApproveAsync(k.Priya, r.Id));
            Fails(await k.TimeOff.CancelAsync(k.Asha, r.Id), 409); // no longer pending
            Fails(await k.TimeOff.CancelAsync(k.Asha, 999), 404);
        }

        [Fact]
        public async Task Only_a_product_manager_can_decide_and_never_on_their_own_request()
        {
            var k = new Kit();
            var emp = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 3))));
            Fails(await k.TimeOff.ApproveAsync(k.Ben, emp.Id), 403);
            Fails(await k.TimeOff.RejectAsync(k.Ben, emp.Id, "no"), 403);
            Fails(await k.TimeOff.ApproveAsync(k.MeenaAsEmployee, emp.Id), 403); // "Both" user acting as Employee

            var pm = Ok(await k.TimeOff.RequestLeaveAsync(k.Priya, Leave("casual", D(3, 9), D(3, 9))));
            Fails(await k.TimeOff.ApproveAsync(k.Priya, pm.Id), 403, "own");
            Fails(await k.TimeOff.RejectAsync(k.Priya, pm.Id, "no"), 403, "own");
            Ok(await k.TimeOff.ApproveAsync(k.Ravi, pm.Id));       // the other PM can
        }

        [Fact]
        public async Task A_both_user_acting_as_pm_cannot_approve_their_own_leave_but_can_approve_others()
        {
            var k = new Kit();
            var own = Ok(await k.TimeOff.RequestLeaveAsync(k.MeenaAsPm, Leave("casual", D(3, 2), D(3, 3))));
            Fails(await k.TimeOff.ApproveAsync(k.MeenaAsPm, own.Id), 403);
            var other = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 9), D(3, 9))));
            Ok(await k.TimeOff.ApproveAsync(k.MeenaAsPm, other.Id));
        }

        [Fact]
        public async Task A_decided_request_cannot_be_decided_again()
        {
            var k = new Kit();
            var r = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 3))));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, r.Id));
            Fails(await k.TimeOff.ApproveAsync(k.Ravi, r.Id), 409);
            Fails(await k.TimeOff.RejectAsync(k.Ravi, r.Id, "late"), 409);
        }

        // ---------- views and notifications ----------

        [Fact]
        public async Task A_pms_own_leave_goes_to_the_other_pms_only()
        {
            var k = new Kit();
            var r = Ok(await k.TimeOff.RequestLeaveAsync(k.Priya, Leave("casual", D(3, 2), D(3, 3))));

            var priyaView = Ok(await k.TimeOff.TeamAsync(k.Priya));
            Assert.Empty(priyaView.Waiting);
            var raviView = Ok(await k.TimeOff.TeamAsync(k.Ravi));
            Assert.Equal(r.Id, Assert.Single(raviView.Waiting).Id);
            Assert.True(raviView.Waiting[0].CanDecide);

            Assert.Empty(await k.NotificationMessagesAsync(k.Priya.Id));
            Assert.Contains("time_off_requested", await k.NotificationMessagesAsync(k.Ravi.Id));
        }

        [Fact]
        public async Task Team_view_is_pm_only_and_lists_the_last_six_decisions()
        {
            var k = new Kit();
            Fails(await k.TimeOff.TeamAsync(k.Asha), 403);

            for (var i = 0; i < 8; i++)
            {
                var r = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2 + i * 2), D(3, 2 + i * 2))));
                Ok(await k.TimeOff.ApproveAsync(k.Priya, r.Id));
            }
            var open = Ok(await k.TimeOff.RequestLeaveAsync(k.Ben, Leave("casual", D(6, 1), D(6, 1))));

            var team = Ok(await k.TimeOff.TeamAsync(k.Priya));
            Assert.Equal(6, team.RecentDecisions.Count);
            Assert.Equal(open.Id, Assert.Single(team.Waiting).Id);
        }

        [Fact]
        public async Task Requests_notify_other_pms_and_decisions_notify_the_requester()
        {
            var k = new Kit();
            var r = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 3))));
            Assert.Contains("time_off_requested", await k.NotificationMessagesAsync(k.Priya.Id));
            Assert.Contains("time_off_requested", await k.NotificationMessagesAsync(k.Ravi.Id));
            Assert.Contains("time_off_requested", await k.NotificationMessagesAsync(k.MeenaAsPm.Id)); // "Both" counts as a PM
            Assert.Empty(await k.NotificationMessagesAsync(k.Asha.Id));

            Ok(await k.TimeOff.ApproveAsync(k.Priya, r.Id));
            Assert.Equal(["time_off_approved"], await k.NotificationMessagesAsync(k.Asha.Id));

            var r2 = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(4, 2), D(4, 2))));
            Ok(await k.TimeOff.RejectAsync(k.Ravi, r2.Id, "no cover"));
            Assert.Equal(["time_off_approved", "time_off_rejected"], await k.NotificationMessagesAsync(k.Asha.Id));
        }

        [Fact]
        public async Task My_requests_are_newest_first_and_cancel_flag_only_on_pending()
        {
            var k = new Kit();
            var a = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(3, 2), D(3, 2))));
            await Task.Delay(5);
            var b = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(4, 2), D(4, 2))));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, a.Id));

            var mine = Ok(await k.TimeOff.MineAsync(k.Asha));
            Assert.Equal([b.Id, a.Id], mine.Select(m => m.Id));
            Assert.True(mine[0].CanCancel);
            Assert.False(mine[1].CanCancel);
        }

        [Fact]
        public async Task Away_today_shows_leave_and_permission_labels_and_calendar_lists_pending_and_approved()
        {
            var k = new Kit(); // today = 2026-11-04
            var leave = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, Leave("casual", D(11, 3), D(11, 5))));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, leave.Id));
            var perm = Ok(await k.TimeOff.RequestPermissionAsync(k.Ben, Perm(D(11, 4), "10:00", "11:30")));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, perm.Id));
            var pendingOnly = Ok(await k.TimeOff.RequestLeaveAsync(k.Ravi, Leave("sick", D(11, 4), D(11, 4))));

            var away = await k.TimeOff.AwayTodayAsync();
            Assert.Equal(["Asha", "Ben"], away.Select(a => a.UserName)); // pending is not "away"
            Assert.Equal("On leave", away[0].Label);
            Assert.Equal("Away 10:00–11:30", away[1].Label);

            var cal = Ok(await k.TimeOff.CalendarAsync(2026, 11));
            Assert.Equal(3, cal.Count);
            Assert.Contains(cal, c => c.RequestId == pendingOnly.Id && c.Status == "pending");
            Assert.Empty(Ok(await k.TimeOff.CalendarAsync(2026, 12)));
            Fails(await k.TimeOff.CalendarAsync(2026, 13), 400);
        }
    }
}
