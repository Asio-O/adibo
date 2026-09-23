// SPDX-License-Identifier: MIT
using System.Text;
using Microsoft.Extensions.Options;
using AdiboProxy.Endpoints;
using AdiboProxy.Json;
using AdiboProxy.Logging;
using AdiboProxy.Providers;
using AdiboProxy.Rcouyi;

// 控制台按 UTF-8 输出，否则中文日志在 cmd 里是乱码。
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // 没有真实控制台（重定向/服务方式启动）时忽略。
}

try
{
    return await RunAsync(args);
}
catch (Exception ex)
{
    // 启动失败时把原因留在屏幕上，别让窗口一闪而过。
    Console.Error.WriteLine();
    Console.Error.WriteLine("================ 启动失败 ================");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ex.ToString());
    Console.Error.WriteLine("==========================================");
    Console.Error.WriteLine();
    Console.Error.WriteLine("常见原因：端口被占用（默认 5080）。换端口：AdiboProxy.exe --urls http://localhost:5099");
    Console.Error.WriteLine("按任意键退出…");
    try
    {
        Console.ReadKey(intercept: true);
    }
    catch (InvalidOperationException)
    {
        // 非交互式环境直接退出。
    }

    return 1;
}

static async Task<int> RunAsync(string[] args)
{
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AdiboOptions>(builder.Configuration.GetSection(AdiboOptions.SectionName));
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<TopicRouter>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UpstreamTrace>(sp =>
    new UpstreamTrace(sp.GetRequiredService<IOptions<AdiboOptions>>().Value.TraceRawChars));

// 上游供应商：把归一化请求发给 rcouyi 面板，或任意 OpenAI / Anthropic 兼容服务。
// 通用上游是外部服务，超时交给调用方控制。
foreach (var entry in ReadProviderEntries(builder.Configuration))
{
    if (!string.Equals(entry.Type, "rcouyi", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddHttpClient("upstream:" + entry.Name)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}

builder.Services.AddSingleton(provider =>
{
    var factory = provider.GetRequiredService<IHttpClientFactory>();
    var registry = new ProviderRegistry();
    var entries = ReadProviderEntries(provider.GetRequiredService<IConfiguration>());

    // rcouyi 面板始终作为兜底供应商（除非显式配置了同名条目）。
    if (!entries.Any(x => string.Equals(x.Name, "rcouyi", StringComparison.OrdinalIgnoreCase)))
    {
        entries.Add(new ProviderConfigEntry { Name = "rcouyi", Type = "rcouyi" });
    }

    foreach (var entry in entries)
    {
        IUpstreamProvider upstream = entry.Type.ToLowerInvariant() switch
        {
            "openai-chat" => new OpenAiChatUpstream(ToConfig(entry), factory),
            "openai-responses" => new OpenAiResponsesUpstream(ToConfig(entry), factory),
            "anthropic" => new AnthropicUpstream(ToConfig(entry), factory),
            _ => new RcouyiProvider(
                provider.GetRequiredService<RcouyiClient>(),
                provider.GetRequiredService<TopicRouter>(),
                provider.GetRequiredService<IOptions<AdiboOptions>>(),
                provider.GetRequiredService<IHttpContextAccessor>(),
                provider.GetRequiredService<ILogger<RcouyiProvider>>()),
        };

        registry.Add(ToConfig(entry), upstream);
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("Providers")
            .LogInformation("上游供应商 {Name} type={Type} baseUrl={BaseUrl} nativeTools={Native}",
                entry.Name, entry.Type, entry.BaseUrl, entry.NativeTools);
    }

    return registry;
});

static List<ProviderConfigEntry> ReadProviderEntries(IConfiguration configuration)
{
    var result = new List<ProviderConfigEntry>();
    foreach (var child in configuration.GetSection("Adibo:Providers").GetChildren())
    {
        var entry = new ProviderConfigEntry
        {
            Name = child["Name"] ?? "",
            Type = child["Type"] ?? "rcouyi",
            BaseUrl = child["BaseUrl"] ?? "",
            ApiKey = child["ApiKey"],
            NativeTools = bool.TryParse(child["NativeTools"], out var native) && native,
            TargetModel = child["TargetModel"],
        };

        foreach (var model in child.GetSection("Models").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(model.Value))
            {
                entry.Models.Add(model.Value!);
            }
        }

        result.Add(entry);
    }

    return result;
}

static ProviderConfig ToConfig(ProviderConfigEntry entry) => new()
{
    Name = entry.Name,
    Type = entry.Type,
    BaseUrl = entry.BaseUrl,
    ApiKey = entry.ApiKey,
    Models = entry.Models,
    NativeTools = entry.NativeTools,
    TargetModel = entry.TargetModel,
};

// AOT 下请求体绑定必须走源生成的 JsonTypeInfo，不能靠反射。
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

// 上游 API 客户端：流式响应可能持续很久，超时交给调用方的 CancellationToken。
builder.Services.AddHttpClient<RcouyiClient>()
    .ConfigureHttpClient((provider, client) =>
    {
        var options = provider.GetRequiredService<IOptions<AdiboOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.Timeout = Timeout.InfiniteTimeSpan;
    });

// 站点前端静态资源客户端：只用来取 /config/system.json 里的模型清单。
builder.Services.AddHttpClient(RcouyiClient.SiteClientName)
    .ConfigureHttpClient((provider, client) =>
    {
        var options = provider.GetRequiredService<IOptions<AdiboOptions>>().Value;
        client.BaseAddress = new Uri(options.SiteBaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(30);
    });

// 文件日志：JSONL，一行一条，丢给 grep / 脚本都能直接看。
var logSection = builder.Configuration.GetSection(AdiboOptions.SectionName);
var logDirectory = logSection["LogDirectory"] ?? "logs";
if (!string.IsNullOrWhiteSpace(logDirectory))
{
    var logPath = Path.IsPathRooted(logDirectory)
        ? logDirectory
        : Path.Combine(builder.Environment.ContentRootPath, logDirectory);
    var fileLevel = Enum.TryParse<LogLevel>(logSection["LogFileLevel"], ignoreCase: true, out var parsedLevel)
        ? parsedLevel
        : LogLevel.Information;
    var retainDays = int.TryParse(logSection["LogRetainDays"], out var parsedDays) ? parsedDays : 7;

    builder.Logging.AddProvider(new FileLoggerProvider(logPath, fileLevel, retainDays));
}

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<AdiboOptions>>().Value;
var tokens = app.Services.GetRequiredService<TokenStore>();
var loaded = await tokens.LoadAsync();

// 每个请求分配一个 id，和日志里的 req= 对齐，方便事后追踪。
app.Use(async (context, next) =>
{
    var trace = context.RequestServices.GetRequiredService<UpstreamTrace>();
    context.Response.Headers["X-Rcouyi-Request-Id"] = trace.Id;

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Http");
    var started = Environment.TickCount64;
    try
    {
        await next();
    }
    finally
    {
        var elapsed = Environment.TickCount64 - started;
        logger.LogInformation(
            "req={Id} {Method} {Path} -> {Status} {Elapsed}ms",
            trace.Id,
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            elapsed);
    }
});

app.MapGet("/health", (TokenStore store) => J.Send(J.Obj(
    ("status", "ok"),
    ("hasToken", store.HasToken),
    ("account", store.Info?.Account),
    ("expiresAt", store.Info?.ExpiresAt))));

// 可选：给 /v1/* 套一层 Bearer 校验。
if (!string.IsNullOrWhiteSpace(options.ApiKey))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/v1"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var provided = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header[7..].Trim()
                : string.Empty;

            if (!string.Equals(provided, options.ApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(
                    J.Obj(("error", J.Obj(("message", "invalid api key"), ("type", "invalid_request_error"))))
                        .ToJsonString(J.Options));
                return;
            }
        }

        await next();
    });
}

app.MapOpenAiEndpoints();
app.MapAnthropicEndpoints();
app.MapResponsesEndpoints();
app.MapRcouyiEndpoints();

app.Logger.LogInformation(
    "rcouyi proxy ready. upstream={BaseUrl} token={State}",
    options.BaseUrl,
    loaded is null ? "MISSING (login first or set Rcouyi:Token)" : $"OK ({tokens.Info?.Account})");

await app.RunAsync();
return 0;
}
