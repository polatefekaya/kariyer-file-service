using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Infrastructure.Caching;

public class GarnetCacheService : ICacheService
{
    private readonly IDatabase _database;
    private readonly ILogger<GarnetCacheService> _logger;

    public GarnetCacheService(IConnectionMultiplexer connectionMultiplexer, ILogger<GarnetCacheService> logger)
    {
        _database = connectionMultiplexer.GetDatabase();
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            RedisValue value = await _database.StringGetAsync(key);
            if (value.IsNullOrEmpty)
            {
                FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                    new KeyValuePair<string, object?>("operation", "get"),
                    new KeyValuePair<string, object?>("outcome", "miss"));
                return default;
            }

            FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                    new KeyValuePair<string, object?>("operation", "get"),
                    new KeyValuePair<string, object?>("outcome", "hit"));

            return JsonSerializer.Deserialize<T>((string)value!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving key '{Key}' from Garnet cache", key);
            FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "get"),
                new KeyValuePair<string, object?>("outcome", "failure"));
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration)
    {
        try
        {
            string json = JsonSerializer.Serialize(value);
            bool success = await _database.StringSetAsync(key, json, expiration);
            string outcome = success ? "success" : "failure";

            FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "set"),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving key '{Key}' to Garnet cache", key);
            FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "set"),
                new KeyValuePair<string, object?>("outcome", "failure"));
        }
    }

    public async Task InvalidateAsync(string key)
    {
        try
        {
            bool success = await _database.KeyDeleteAsync(key);
            string outcome = success ? "success" : "failure";

            FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "invalidate"),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting key '{Key}' from Garnet cache", key);
            FileServiceDiagnostics.CacheOperationsCounter.Add(1,
                new KeyValuePair<string, object?>("operation", "invalidate"),
                new KeyValuePair<string, object?>("outcome", "failure"));
        }
    }
}
