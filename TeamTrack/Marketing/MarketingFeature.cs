using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.DTOs;
using TeamTrack.Marketing.Data;
using TeamTrack.Marketing.Services;

namespace TeamTrack.Marketing
{
    public static class MarketingFeature
    {
        /// <summary>Features:Marketing in config, or the Features__Marketing environment variable.</summary>
        public static bool IsEnabled(IConfiguration config) => config.GetValue<bool>("Features:Marketing");

        public static IServiceCollection AddMarketing(this IServiceCollection services, IConfiguration config)
        {
            services.AddDbContext<MarketingDbContext>(o => o.UseNpgsql(config.GetConnectionString("DefaultConnection")));
            services.AddSingleton<MarketingState>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IMarketingFileStore, MarketingFileStore>();
            services.AddScoped<IMarketingService, MarketingService>();
            return services;
        }

        /// <summary>Creates the mk_ tables and, in Development only, seeds the demo user and the one demo lead.</summary>
        public static async Task InitializeMarketingAsync(this WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketingDbContext>();
            var state = scope.ServiceProvider.GetRequiredService<MarketingState>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Marketing");

            var ready = await MarketingSchemaInitializer.InitializeAsync(db, state, logger);
            if (!ready || !app.Environment.IsDevelopment()) return;

            try
            {
                var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await MarketingSeeder.SeedAsync(db, appDb, app.Configuration, logger, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // a failed demo seed must never stop the app
                logger.LogError(ex, "Seeding the Marketing demo data failed. The app keeps running without it.");
            }
        }
    }

    /// <summary>Gate for the marketing controller: 404 when the feature is off, 503 (with the reason) when its schema failed.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class MarketingEnabledAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var state = context.HttpContext.RequestServices.GetService<MarketingState>();
            if (state == null)
            {
                context.Result = new ObjectResult(ApiResponse<string>.FailureResponse("Marketing features are disabled."))
                { StatusCode = StatusCodes.Status404NotFound };
            }
            else if (!state.SchemaReady)
            {
                context.Result = new ObjectResult(ApiResponse<string>.FailureResponse(
                    $"Marketing is unavailable: schema initialization failed ({state.SchemaError ?? "not initialised yet"})."))
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
