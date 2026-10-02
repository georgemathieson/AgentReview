using AgentReview.Core;
using AgentReview.Web.Components;
using AgentReview.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<ReviewBuilder>();
builder.Services.AddSingleton<RecentReposStore>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Review").Get<ReviewOptions>() ?? new ReviewOptions());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
