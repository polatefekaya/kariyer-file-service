using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Amazon.S3;
using Npgsql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using StackExchange.Redis;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;
using Kariyer.FileService.Features.ConfirmUpload;
using Kariyer.FileService.Features.DeleteFile;
using Kariyer.FileService.Features.GetDownloadUrl;
using Kariyer.FileService.Features.GetFileDetails;
using Kariyer.FileService.Features.ListFiles;
using Kariyer.FileService.Features.OverwriteFileContent;
using Kariyer.FileService.Features.PresignedUpload;
using Kariyer.FileService.Features.UpdateFileDetails;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    string garnetConn = builder.Configuration.GetConnectionString("Garnet") ?? string.Empty;
    string dbConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
    string externalProviderUrl = builder.Configuration["ExternalProvider:Url"] ?? string.Empty;
    string otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? string.Empty;
    string serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    string otlpLogsEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT")
        ?? (string.IsNullOrWhiteSpace(otlpEndpoint) ? string.Empty : otlpEndpoint.TrimEnd('/') + "/v1/logs");

    LogEventLevel otlpLogMinLevel = Enum.TryParse(
        Environment.GetEnvironmentVariable("OTEL_LOGS_MINIMUM_LEVEL"),
        ignoreCase: true,
        out LogEventLevel parsedOtlpLevel)
        ? parsedOtlpLevel
        : LogEventLevel.Information;

    string otlpHeadersRaw = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS") ?? string.Empty;
    Dictionary<string, string> otlpHeaders = otlpHeadersRaw
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(h => h.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

    // Explicitly set W3C TraceContext + Baggage propagators
    Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator(new TextMapPropagator[]
    {
        new TraceContextPropagator(),
        new BaggagePropagator()
    }));

    // Setup Serilog
    builder.Services.AddSerilog((services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("deployment.environment", builder.Environment.EnvironmentName)
            .Enrich.WithProperty("service.version", serviceVersion)
            .Enrich.WithProperty("host.name", Environment.MachineName)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj} {TraceId}{NewLine}{Exception}",
                restrictedToMinimumLevel: LogEventLevel.Information);

        if (!string.IsNullOrWhiteSpace(otlpLogsEndpoint))
        {
            lc.WriteTo.OpenTelemetry(
                endpoint: otlpLogsEndpoint,
                protocol: OtlpProtocol.HttpProtobuf,
                headers: otlpHeaders,
                resourceAttributes: new Dictionary<string, object>
                {
                    ["service.name"] = FileServiceDiagnostics.ServiceName,
                    ["service.version"] = serviceVersion,
                    ["deployment.environment"] = builder.Environment.EnvironmentName,
                    ["host.name"] = Environment.MachineName,
                },
                includedData:
                    IncludedData.TraceIdField |
                    IncludedData.SpanIdField |
                    IncludedData.MessageTemplateRenderingsAttribute |
                    IncludedData.SpecRequiredResourceAttributes |
                    IncludedData.SourceContextAttribute,
                restrictedToMinimumLevel: otlpLogMinLevel);
        }
        else
        {
            Log.Warning("OTEL_EXPORTER_OTLP_ENDPOINT is not set — logs will not be exported to SigNoz.");
        }
    });

    // Setup OpenTelemetry Tracing and Metrics for SigNoz
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(
                serviceName: FileServiceDiagnostics.ServiceName,
                serviceVersion: serviceVersion,
                autoGenerateServiceInstanceId: true)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = builder.Environment.EnvironmentName,
                ["host.name"] = Environment.MachineName,
            }))
        .WithTracing(tracing => tracing
            .AddSource(FileServiceDiagnostics.ServiceName)
            .AddAspNetCoreInstrumentation(opts =>
            {
                opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                opts.RecordException = true;
                opts.EnrichWithHttpRequest = (activity, request) =>
                {
                    if (request.HttpContext.Connection.RemoteIpAddress is { } ip)
                        activity.SetTag("http.client_ip", ip.ToString());
                    string ua = request.Headers.UserAgent.ToString();
                    if (!string.IsNullOrEmpty(ua))
                        activity.SetTag("http.user_agent", ua);
                };
            })
            .AddHttpClientInstrumentation(opts =>
            {
                opts.RecordException = true;
            })
            .AddNpgsql()
            .AddOtlpExporter(opts =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    opts.Endpoint = new Uri(otlpEndpoint.TrimEnd('/') + "/v1/traces");
                opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                if (!string.IsNullOrWhiteSpace(otlpHeadersRaw))
                    opts.Headers = otlpHeadersRaw;
            }))
        .WithMetrics(metrics => metrics
            .AddMeter(FileServiceDiagnostics.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(opts =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    opts.Endpoint = new Uri(otlpEndpoint.TrimEnd('/') + "/v1/metrics");
                opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                if (!string.IsNullOrWhiteSpace(otlpHeadersRaw))
                    opts.Headers = otlpHeadersRaw;
            }));

    // Setup Database
    builder.Services.AddDbContext<FileDbContext>(options =>
    {
        if (string.IsNullOrWhiteSpace(dbConnectionString)) return;

        options.UseNpgsql(dbConnectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(2),
                errorCodesToAdd: null);
            npgsqlOptions.MigrationsAssembly(typeof(FileDbContext).Assembly.FullName);
        });
    });

    // Setup Garnet Connection
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(garnetConn, nameof(garnetConn));
        ConfigurationOptions options = ConfigurationOptions.Parse(garnetConn);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    });

    // Setup Amazon S3 Client for Cloudflare R2
    builder.Services.AddSingleton<IAmazonS3>(sp =>
    {
        var config = builder.Configuration.GetSection("R2");
        string accessKey = config["AccessKey"] ?? throw new ArgumentNullException("R2:AccessKey");
        string secretKey = config["SecretKey"] ?? throw new ArgumentNullException("R2:SecretKey");
        string serviceUrl = config["ServiceUrl"] ?? throw new ArgumentNullException("R2:ServiceUrl");

        var credentials = new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey);

        // Cloudflare R2 ONLY accepts SigV4. Without a region the SDK emits legacy SigV2
        // presigned URLs (AWSAccessKeyId/Expires/Signature) which R2 rejects with 401.
        // Force SigV4 and give it R2's "auto" region.
        Amazon.AWSConfigsS3.UseSignatureVersion4 = true;

        var s3Config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true, // Required for Cloudflare R2 compatibility
            AuthenticationRegion = "auto" // R2's region; required for SigV4 signing
        };

        return new AmazonS3Client(credentials, s3Config);
    });

    // Register Services
    builder.Services.AddScoped<ICacheService, GarnetCacheService>();
    builder.Services.AddScoped<IR2StorageService, R2StorageService>();
    builder.Services.AddHostedService<Kariyer.FileService.Infrastructure.BackgroundServices.OrphanedUploadCleanupService>();

    // Register Feature Handlers (Vertical Slices)
    builder.Services.AddScoped<PresignedUploadHandler>();
    builder.Services.AddScoped<ConfirmUploadHandler>();
    builder.Services.AddScoped<GetDownloadUrlHandler>();
    builder.Services.AddScoped<DeleteFileHandler>();
    builder.Services.AddScoped<ListFilesHandler>();
    builder.Services.AddScoped<GetFileDetailsHandler>();
    builder.Services.AddScoped<UpdateFileDetailsHandler>();
    builder.Services.AddScoped<OverwriteFileContentHandler>();
    builder.Services.AddScoped<Kariyer.FileService.Features.MultipartUpload.MultipartInitiateHandler>();
    builder.Services.AddScoped<Kariyer.FileService.Features.MultipartUpload.MultipartPresignPartsHandler>();
    builder.Services.AddScoped<Kariyer.FileService.Features.MultipartUpload.MultipartCompleteHandler>();
    builder.Services.AddScoped<Kariyer.FileService.Features.MultipartUpload.MultipartAbortHandler>();

    // Setup CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("StrictFrontendPolicy", policy =>
        {
            policy.WithOrigins(
                "https://www.kariyerzamani.com",
                "https://auth.kariyerzamani.com",
                "https://kz-auth.kariyerzamani.com",
                "http://localhost:3000",
                "http://localhost:3001",
                "http://localhost:5173",
                "https://kariyerzamani.com",
                "https://kz-admin.kariyerzamani.com",
                "https://admin.kariyerzamani.com",
                "https://tst.kariyerzamani.com"
            )
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
            .AllowAnyHeader()
            .AllowCredentials();
        });
    });

    // Setup Supabase JWT Authentication
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(externalProviderUrl, nameof(externalProviderUrl));
            options.MapInboundClaims = false;
            options.Authority = $"{externalProviderUrl}/auth/v1";

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = $"{externalProviderUrl}/auth/v1",
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = "role"
            };

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    Log.Warning("JWT Validation Failed: {Message}", context.Exception.Message);
                    FileServiceDiagnostics.AuthAttemptsCounter.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                    {
                        Claim? userMetaDataClaim = identity.FindFirst("user_metadata");
                        if (userMetaDataClaim != null)
                        {
                            try
                            {
                                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(userMetaDataClaim.Value);
                                System.Text.Json.JsonElement root = document.RootElement;
                                if (root.TryGetProperty("account_type", out System.Text.Json.JsonElement accountTypeElement))
                                {
                                    string? role = accountTypeElement.GetString();
                                    if (!string.IsNullOrWhiteSpace(role))
                                    {
                                        Claim? existingRole = identity.FindFirst("role");
                                        if (existingRole is not null)
                                            identity.RemoveClaim(existingRole);
                                        identity.AddClaim(new Claim("role", role));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Failed to parse user_metadata JSON claim during token validation.");
                            }
                        }
                    }
                    FileServiceDiagnostics.AuthAttemptsCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();
    builder.Services.AddControllers();
    builder.Services.AddOpenApi();

    // Setup Token Bucket Rate Limiting partitioned by Sub/IP
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("UploadPolicy", httpContext =>
        {
            string? userId = httpContext.User.FindFirst("sub")?.Value;
            string partitionKey = !string.IsNullOrWhiteSpace(userId) 
                ? userId 
                : (httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous");

            return System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                QueueLimit = 2,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = 20,
                AutoReplenishment = true
            });
        });
    });

    // Setup Health Checks
    builder.Services.AddHealthChecks()
        .AddNpgSql(dbConnectionString, name: "postgres")
        .AddRedis(garnetConn, name: "garnet");

    WebApplication app = builder.Build();

    // Run EF migrations automatically on startup to deploy the 'storage' schema and StoredFiles table
    using (IServiceScope scope = app.Services.CreateScope())
    {
        FileDbContext db = scope.ServiceProvider.GetRequiredService<FileDbContext>();
        db.Database.Migrate();
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseSerilogRequestLogging();
    app.UseCors("StrictFrontendPolicy");

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();
    
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        }
    });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Microservice terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
