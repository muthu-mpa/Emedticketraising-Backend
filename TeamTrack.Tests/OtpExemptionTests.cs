using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TeamTrack.Services;
using Xunit;

namespace TeamTrack.Tests
{
    public class OtpExemptionTests
    {
        private sealed class Env(string name) : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = name;
            public string ApplicationName { get; set; } = "t";
            public string ContentRootPath { get; set; } = ".";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }

        private static IConfiguration Config(params string[] exempt)
            => new ConfigurationBuilder().AddInMemoryCollection(exempt.Select((e, i) => KeyValuePair.Create($"OtpExemptEmails:{i}", (string?)e))).Build();

        [Theory]
        [InlineData("Production")]
        [InlineData("Staging")]
        [InlineData("")]
        public void The_demo_account_is_never_exempt_outside_development_even_if_configured(string environment)
        {
            var config = Config("someone@emedlogix.com", OtpExemption.DemoEmail);
            Assert.False(OtpExemption.IsExempt(config, new Env(environment), OtpExemption.DemoEmail));
            Assert.False(OtpExemption.IsExempt(config, new Env(environment), OtpExemption.DemoEmail.ToUpperInvariant()));
        }

        [Fact]
        public void The_demo_account_is_exempt_in_development_when_configured()
        {
            Assert.True(OtpExemption.IsExempt(Config(OtpExemption.DemoEmail), new Env("Development"), OtpExemption.DemoEmail));
            Assert.False(OtpExemption.IsExempt(Config(), new Env("Development"), OtpExemption.DemoEmail)); // still needs the setting
        }

        [Fact]
        public void Other_configured_accounts_keep_their_exemption_in_every_environment()
        {
            var config = Config("khajahussain@emedlogix.com");
            Assert.True(OtpExemption.IsExempt(config, new Env("Production"), "KhajaHussain@emedlogix.com"));
            Assert.False(OtpExemption.IsExempt(config, new Env("Production"), "other@emedlogix.com"));
            Assert.False(OtpExemption.IsExempt(config, new Env("Production"), null));
        }

        [Fact]
        public void The_demo_email_in_the_auth_guard_matches_the_seeded_one()
            => Assert.Equal(TeamTrack.Marketing.Data.MarketingSeeder.DemoEmail, OtpExemption.DemoEmail);
    }
}
