using Hangfire;
using Hangfire.Dashboard;
using Hangfire.MemoryStorage;
using Hangfire.SqlServer;
using InvoiceManager.Data;
using InvoiceManager.Jobs;
using InvoiceManager.Models.Entities;
using InvoiceManager.Services;
using InvoiceManager.Services.Parsers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình Serilog cho Audit & System logs
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDirectory, "audit-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// 2. Database & Entity Framework Core (Hỗ trợ cả SQL Server và SQLite tự động khi deploy Render)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var isRender = string.Equals(Environment.GetEnvironmentVariable("RENDER"), "true", StringComparison.OrdinalIgnoreCase);

var useSqlite = builder.Configuration.GetValue<bool>("UseSqlite")
    || (isRender && (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
    || string.IsNullOrWhiteSpace(connectionString)
    || connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase)
    || connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase);

if (useSqlite)
{
    var appDataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
    Directory.CreateDirectory(appDataDir);
    var sqliteDbPath = Path.Combine(appDataDir, "InvoiceManager.db");
    var sqliteConn = (string.IsNullOrWhiteSpace(connectionString) || !connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        ? $"Data Source={sqliteDbPath}"
        : connectionString;

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlite(sqliteConn));
}
else
{
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlServer(connectionString));
}

// 3. ASP.NET Core Identity & Phân quyền
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

// 4. Session & Caching
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddHttpContextAccessor();

// 5. Đăng ký Services & DI
builder.Services.AddScoped<ITaxAccountContext, TaxAccountContext>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();

// Đăng ký bộ Parsers (Strategy Pattern)
builder.Services.AddScoped<IInvoiceParser, StandardTctParser>();
builder.Services.AddScoped<IInvoiceParser, MisaInvoiceParser>();
builder.Services.AddScoped<IInvoiceParser, ViettelInvoiceParser>();
builder.Services.AddScoped<IInvoiceParser, VnptInvoiceParser>();
builder.Services.AddScoped<IInvoiceParserFactory, InvoiceParserFactory>();

builder.Services.AddScoped<IInvoiceImportService, InvoiceImportService>();
builder.Services.AddScoped<IExcelImportService, ExcelImportService>();
builder.Services.AddScoped<IExportService, ExportService>();
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddScoped<InvoiceAutoSyncJob>();

// 6. Hangfire Background Jobs
if (useSqlite)
{
    builder.Services.AddHangfire(configuration => configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseMemoryStorage());
}
else
{
    builder.Services.AddHangfire(configuration => configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connectionString!, new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.FromSeconds(15),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));
}

builder.Services.AddHangfireServer();

builder.Services.AddControllersWithViews();

var app = builder.Build();

// 7. Tự động chạy Migration và Seed dữ liệu lúc khởi động
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var userMgr = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = services.GetRequiredService<RoleManager<IdentityRole>>();

        await DbInitializer.InitializeAsync(db, userMgr, roleMgr);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Lỗi trong quá trình khởi tạo dữ liệu ban đầu.");
    }
}

// 8. Pipeline cấu hình HTTP
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

// Hangfire Dashboard (chỉ admin hoặc local)
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter() }
});

// Lên lịch Job đồng bộ định kỳ mỗi giờ
RecurringJob.AddOrUpdate<InvoiceAutoSyncJob>("invoice-auto-sync-job", job => job.ExecuteAsync(), Cron.Hourly);

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Invoice}/{action=Index}/{id?}");

app.Run();

// Bộ lọc bảo mật Hangfire Dashboard
public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true && httpContext.User.IsInRole("Admin");
    }
}
