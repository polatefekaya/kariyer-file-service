using System;
using System.Threading.Tasks;

namespace Kariyer.FileService.Infrastructure.Caching;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value, TimeSpan expiration);
    Task InvalidateAsync(string key);
}
