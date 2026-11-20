using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using TeamTrack.DTOs;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Services;

namespace TeamTrack.Workspace
{
    public static class WorkspaceFeature
    {
        /// <summary>Features:Workspace in config, or the Features__Workspace environment variable.</summary>
        public static bool IsEnabled(IConfiguration config) => config.GetValue<bool>("Features:Workspace");

        public static IServiceCollection AddWorkspace(this IServiceCollection services, IConfiguration config)
        {
            services.AddDbContext<WorkspaceDbContext>(o => o.UseNpgsql(config.GetConnectionString("DefaultConnection")));
            services.AddSingleton<WorkspaceState>();
            services.AddSingleton<IWorkspaceClock, WorkspaceClock>();
            services.AddScoped<INotificationService, NotificationService>();
            services.AddScoped<ICampaignService, CampaignService>();
            services.AddScoped<ITimeOffService, TimeOffService>();
            services.AddScoped<IPeopleService, PeopleService>();
            services.AddScoped<IDocumentFileService, DocumentFileService>();
            services.AddScoped<IWorkspaceProjectService, WorkspaceProjectService>();
            services.AddScoped<IWorkspaceHomeService, WorkspaceHomeService>();
            services.AddSingleton<ISheetFileStore, SheetFileStore>();
            services.AddScoped<ISharedSheetService, SharedSheetService>();
            return services;
        }

        public static async Task InitializeWorkspaceAsync(this WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var state = scope.ServiceProvider.GetRequiredService<WorkspaceState>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Workspace");
            await WorkspaceSchemaInitializer.InitializeAsync(db, state, logger);
        }
    }

    /// <summary>
    /// Gate for every workspace controller. When the feature is off, WorkspaceState is never registered, so the
    /// request gets a 404 before any workspace service is constructed. If the schema failed to initialise it gets
    /// a 503 carrying the reason.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class WorkspaceEnabledAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var state = context.HttpContext.RequestServices.GetService<WorkspaceState>();
            if (state == null)
            {
                context.Result = new ObjectResult(ApiResponse<string>.FailureResponse("Workspace features are disabled."))
                { StatusCode = StatusCodes.Status404NotFound };
            }
            else if (!state.SchemaReady)
            {
                context.Result = new ObjectResult(ApiResponse<string>.FailureResponse(
                    $"Workspace is unavailable: schema initialization failed ({state.SchemaError ?? "not initialised yet"})."))
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
