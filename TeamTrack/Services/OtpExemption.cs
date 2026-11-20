namespace TeamTrack.Services
{
    /// <summary>
    /// Decides whether an account skips the emailed login OTP. The list comes from the OtpExemptEmails setting, except that the
    /// Marketing demo account is exempt in the Development environment only, whatever the configuration says.
    /// </summary>
    public static class OtpExemption
    {
        /// <summary>Must match MarketingSeeder.DemoEmail; kept here so Auth does not depend on the Marketing feature.</summary>
        public const string DemoEmail = "marketing.demo@example.com";

        public static bool IsExempt(IConfiguration config, IHostEnvironment env, string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            if (string.Equals(email, DemoEmail, StringComparison.OrdinalIgnoreCase) && !env.IsDevelopment()) return false;

            var exempt = config.GetSection("OtpExemptEmails").Get<string[]>() ?? Array.Empty<string>();
            return exempt.Any(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase));
        }
    }
}
