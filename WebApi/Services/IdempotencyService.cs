using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebApi.Services;

public interface IIdempotencyService
{
    string ComputeBodyHash(string bodyJson);

    string ComputeScopedKeyHash(string scope, string key);
}

public sealed class IdempotencyService : IIdempotencyService
{
    public string ComputeBodyHash(string bodyJson)
    {
        var bytes = Encoding.UTF8.GetBytes(bodyJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public string ComputeScopedKeyHash(string scope, string key)
    {
        var bytes = Encoding.UTF8.GetBytes($"{scope}:{key}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
