using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using TableOrderWeb.Services;

var builder = WebApplication.CreateBuilder(args);
const string AdminAuthScheme = "TableOrderWeb.AdminAuth";
const string StaffAuthScheme = "TableOrderWeb.StaffAuth";
const string MixedAuthScheme = "TableOrderWeb.MixedAuth";

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<IMenuRepository, SqlCmdMenuRepository>();
builder.Services.AddScoped<IMenuAdminService, SqlCmdMenuAdminService>();
builder.Services.AddSingleton<IUserAccountService, FileUserAccountService>();
builder.Services.AddSingleton<ITableOrderService, FileTableOrderService>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys")))
    .SetApplicationName("TableOrderWeb");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = MixedAuthScheme;
        options.DefaultChallengeScheme = MixedAuthScheme;
    })
    .AddPolicyScheme(MixedAuthScheme, null, options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var portal = context.Request.Query["portal"].FirstOrDefault();
            if (string.Equals(portal, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return AdminAuthScheme;
            }

            if (string.Equals(portal, "Staff", StringComparison.OrdinalIgnoreCase))
            {
                return StaffAuthScheme;
            }

            return context.Request.Path.StartsWithSegments("/Home/Admin", StringComparison.OrdinalIgnoreCase)
                ? AdminAuthScheme
                : StaffAuthScheme;
        };
    })
    .AddCookie(AdminAuthScheme, options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Cookie.Name = "TableOrderWeb.AdminAuth";
        options.SlidingExpiration = true;
    })
    .AddCookie(StaffAuthScheme, options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.Cookie.Name = "TableOrderWeb.StaffAuth";
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

try
{
    var tableOrderService = app.Services.GetRequiredService<ITableOrderService>();
    if (tableOrderService is FileTableOrderService fileTableOrderService)
    {
        await fileTableOrderService.SyncSqlHistoryAsync();
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Khong the dong bo lich su order/bill sang SQL luc khoi dong.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "table-menu",
    pattern: "ban/{tableCode}",
    defaults: new { controller = "Home", action = "Customer" });

app.MapControllerRoute(
    name: "table-short",
    pattern: "t/{tableCode}",
    defaults: new { controller = "Home", action = "Customer" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();



