using TeamTrack;
using Xunit;

namespace TeamTrack.Tests
{
    public class DatabaseTargetTests
    {
        [Theory]
        [InlineData("Host=localhost;Port=5432;Database=teamtrack_dev;Username=teamtrack_dev;Password=Sup3rSecret!", "localhost/teamtrack_dev")]
        [InlineData("Server=db.example.com;Port=5432;User ID=postgres;Password=p@ss;Database=TicketingDB;Pooling=true", "db.example.com/TicketingDB")]
        public void The_log_line_shows_host_and_database_but_never_the_user_or_password(string connectionString, string expected)
        {
            var text = DatabaseTarget.Describe(connectionString);
            Assert.Equal(expected, text);
            Assert.DoesNotContain("Sup3rSecret", text);
            Assert.DoesNotContain("p@ss", text);
            Assert.DoesNotContain("postgres", text);
        }

        [Theory]
        [InlineData(null, "no connection string")]
        [InlineData("", "no connection string")]
        [InlineData("this is not a connection string", "could not be read")]
        public void Missing_or_broken_connection_strings_do_not_throw_or_leak(string? connectionString, string expectedPart)
            => Assert.Contains(expectedPart, DatabaseTarget.Describe(connectionString));
    }
}
