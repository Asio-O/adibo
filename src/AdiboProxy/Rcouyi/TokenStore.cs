using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using AdiboProxy.Json;

namespace AdiboProxy.Rcouyi;

/// <summary>落盘的 token 记录，附带从 JWT 里解出的账号信息，方便排查。</summary>
public sealed class TokenRecord
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("account")] public string? Account { get; set; }
    [JsonPropertyName("memberId")] public long? MemberId { get; set; }
    [JsonPropertyName("nickName")] public string? NickName { get; set; }
    [JsonPropertyName("savedAt")] public DateTimeOffset? SavedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>token 的唯一持有者：内存 + 磁盘双份，进程内并发安全。</summary>
public sealed class TokenStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private volatile string? _token;

    public TokenStore(IOptions<AdiboOptions> options, IWebHostEnvironment env)
    {
        var configured = options.Value.TokenFile;
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "data", "token.json")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(env.ContentRootPath, configured);

        var inline = options.Value.Token?.Trim();
        if (!string.IsNullOrEmpty(inline))
        {
            _token = inline;
            Info = Describe(inline);
        }
    }

    public string? Token => _token;

    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

    public string FilePath => _path;

    /// <summary>当前 token 的账号 / 有效期信息，未加载时为 null。</summary>
    public TokenRecord? Info { get; private set; }

    /// <summary>首次访问时从磁盘恢复 token。</summary>
    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        if (HasToken)
        {
            return _token;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasToken)
            {
                return _token;
            }

            if (!File.Exists(_path))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            var record = ParseFile(text);
            if (record is null || string.IsNullOrWhiteSpace(record.Token))
            {
                return null;
            }

            _token = record.Token;
            Info = record;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>写入新 token 并落盘。</summary>
    public async Task<TokenRecord> SetAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var record = Describe(token.Trim());

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _token = record.Token;
            Info = record;

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(record, AppJsonContext.Default.TokenRecord);
            await File.WriteAllTextAsync(_path, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return record;
    }

    /// <summary>清掉内存与磁盘上的 token。</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _token = null;
            Info = null;
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>从 JWT 的 payload 段解出账号信息与过期时间。</summary>
    public static TokenRecord Describe(string token)
    {
        var record = new TokenRecord { Token = token, SavedAt = DateTimeOffset.UtcNow };

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return record;
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("Account", out var account) && account.ValueKind == JsonValueKind.String)
            {
                record.Account = account.GetString();
            }

            if (root.TryGetProperty("NickName", out var nick) && nick.ValueKind == JsonValueKind.String)
            {
                record.NickName = nick.GetString();
            }

            if (root.TryGetProperty("MemberId", out var memberId) && memberId.TryGetInt64(out var mid))
            {
                record.MemberId = mid;
            }

            if (root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix))
            {
                record.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }
        catch (Exception)
        {
            // JWT 不是标准格式时只保留原始 token，不影响使用。
        }

        return record;
    }

    private static TokenRecord? ParseFile(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return Describe(trimmed);
        }

        try
        {
            var record = JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.TokenRecord);
            if (record is null || string.IsNullOrWhiteSpace(record.Token))
            {
                return null;
            }

            var described = Describe(record.Token);
            described.SavedAt ??= record.SavedAt;
            return described;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };
        return Convert.FromBase64String(normalized);
    }
}
