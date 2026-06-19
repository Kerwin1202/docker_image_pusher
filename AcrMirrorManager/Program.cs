using AcrMirrorManager.Options;
using AcrMirrorManager.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.Configure<RegistryBackendOptions>(builder.Configuration.GetSection("RegistryBackend"));
builder.Services.Configure<AliyunAcrOptions>(builder.Configuration.GetSection("AliyunAcr"));
builder.Services.Configure<RegistryV2Options>(builder.Configuration.GetSection("RegistryV2"));
builder.Services.Configure<GitHubMirrorOptions>(builder.Configuration.GetSection("GitHubMirror"));
builder.Services.AddSingleton<RegistryV2PersistentCache>();
builder.Services.AddSingleton<AliyunAcrRegistryService>();
builder.Services.AddHttpClient<RegistryV2AcrRegistryService>();
builder.Services.AddTransient<IRegistryV2RefreshService>(services => services.GetRequiredService<RegistryV2AcrRegistryService>());
builder.Services.AddTransient<IAcrRegistryService>(services =>
{
    var mode = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RegistryBackendOptions>>().Value.Mode;
    return mode.Equals("AliyunApi", StringComparison.OrdinalIgnoreCase)
        ? services.GetRequiredService<AliyunAcrRegistryService>()
        : services.GetRequiredService<RegistryV2AcrRegistryService>();
});
builder.Services.AddHttpClient<IGitHubMirrorService, GitHubMirrorService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
});
builder.Services.AddHostedService<RegistryV2RefreshHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
