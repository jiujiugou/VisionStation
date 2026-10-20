using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using VisionStation.App.Services;
using VisionStation.Core;
using VisionStation.Core.Models;
using VisionStation.Core.Tools;
using VisionStation.Devices;
using VisionStation.Service;
using VisionStation.Service.Housekeeping;
using VisionStation.Service.Recipes;
using VisionStation.Storage;

namespace VisionStation.App.Composition;

public sealed class AppHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions RecipeJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IConfiguration? _configuration;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Func<IServiceProvider, IAppWindow> _windowFactory;

    private ServiceProvider? _services;
    private AppPaths? _paths;
    private IAppWindow? _window;
    private int _stopped;

    public AppHost()
        : this(null, null, null)
    {
    }

    internal AppHost(
        IConfiguration? configuration,
        Action<IServiceCollection>? configureServices,
        Func<IServiceProvider, IAppWindow>? windowFactory)
    {
        _configuration = configuration;
        _configureServices = configureServices;
        _windowFactory = windowFactory ?? (sp => sp.GetRequiredService<MainWindow>());
    }

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("AppHost 尚未 Configure");

    public AppPaths Paths => _paths
        ?? throw new InvalidOperationException("AppHost 尚未 Configure");

    public IReadOnlyList<HealthResult> HealthResults { get; private set; } = Array.Empty<HealthResult>();

    public void Configure()
    {
        if (_services != null) return;

        var config = _configuration ?? new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var appPaths = new AppPaths(config);
        _paths = appPaths;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(Path.Combine(appPaths.LogFolder, "visionstation-.log"),
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));

        var recipe = LoadRecipe(appPaths.RecipePath);

        services
            .AddSingleton(appPaths)
            .AddCore(recipe, appPaths)
            .AddDevices(appPaths, config)
            .AddStorage(appPaths, config)
            .AddAppServices(appPaths, config)
            .AddViews();

        _configureServices?.Invoke(services);

        _services = services.BuildServiceProvider();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var services = _services
            ?? throw new InvalidOperationException("AppHost 尚未 Configure");

        var log = services.GetRequiredService<ILogger<App>>();
        var health = services.GetRequiredService<IHealthChecker>();
        var results = health.RunAll();

        foreach (var r in results)
        {
            if (r.OK)
                log.LogInformation("自检 {Item}: OK ({Detail})", r.Item, r.Detail);
            else
                log.LogError("自检 {Item}: FAIL ({Error})", r.Item, r.Error);
        }

        HealthResults = results;

        services.GetRequiredService<IHousekeepingService>();
        await services.GetRequiredService<IInspectionRepository>().EnsureCreatedAsync();
    }

    public void Show()
    {
        if (_window != null) return;

        _window = _windowFactory(Services);
        _window.Show();
    }

    public Task StopAsync(TimeSpan timeout)
    {
        _ = timeout;

        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return Task.CompletedTask;

        (_services as IDisposable)?.Dispose();
        Log.CloseAndFlush();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(5)));

    private static RecipeConfig LoadRecipe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var recipe = JsonSerializer.Deserialize<RecipeConfig>(json, RecipeJsonOptions);
                if (recipe != null && recipe.Tools.Count > 0) return recipe;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配方加载失败 ({Path})", path);
        }

        Log.Warning("配方 {Path} 加载失败（缺失/损坏/为空），回退到内嵌默认配方", path);
        return LoadEmbeddedRecipe();
    }

    private static RecipeConfig LoadEmbeddedRecipe()
    {
        const string resourceName = "VisionStation.App.Config.recipes.default.json";
        using var stream = typeof(AppHost).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内嵌配方资源缺失: {resourceName}（打包配置被改动过？）");
        using var reader = new StreamReader(stream);
        var recipe = JsonSerializer.Deserialize<RecipeConfig>(reader.ReadToEnd(), RecipeJsonOptions);
        if (recipe == null || recipe.Tools.Count == 0)
            throw new InvalidOperationException("内嵌默认配方内容为空或无效");
        return recipe;
    }
}
