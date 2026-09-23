// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AdiboProxy.Json;
using AdiboProxy.Rcouyi;

namespace AdiboProxy.Endpoints;

/// <summary>
/// 上游的无状态接口不吃 <c>model</c>，要换模型只能建一个带 <c>params.model</c> 的会话，
/// 再走「先存后流」。这里按模型名缓存会话 id 并落盘，避免每个请求都新建一个会话。
/// </summary>
public sealed class TopicRouter
{
    private readonly ConcurrentDictionary<string, long> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _maxTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<TopicRouter> _log;
    private readonly AdiboOptions _options;
    private readonly string _path;
    private bool _loaded;

    public TopicRouter(IWebHostEnvironment env, IOptions<AdiboOptions> options, ILogger<TopicRouter> log)
    {
        _log = log;
        _options = options.Value;
        _path = Path.Combine(env.ContentRootPath, "data", "model-topics.json");
    }

    /// <summary>丢掉某个模型的缓存会话（上游把会话删了、或缓存过期时用）。</summary>
    public async Task InvalidateAsync(string model, CancellationToken ct)
    {
        if (!_topics.TryRemove(model, out var stale))
        {
            return;
        }

        _log.LogWarning("上游会话 {TopicId}（模型 {Model}）已失效，已从缓存移除", stale, model);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>取该模型对应的会话；没有就建一个。</summary>
    public async Task<long> ResolveAsync(RcouyiClient client, string model, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        if (_topics.TryGetValue(model, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_topics.TryGetValue(model, out cached))
            {
                return cached;
            }

            var created = await client.CreateTopicAsync(new RcouyiCreateTopicRequest
            {
                Title = _options.TopicTitle,
                Model = model,
                // 每个模型的上限不同（从 4k 到 1M+），写死一个值不是被拒就是浪费预算。
                MaxTokens = await ResolveMaxTokensAsync(client, model, ct).ConfigureAwait(false),
            }, ct).ConfigureAwait(false);

            var topicId = RcouyiEndpoints.ExtractTopicId(created);
            _topics[model] = topicId;
            await SaveAsync(ct).ConfigureAwait(false);

            _log.LogInformation("为模型 {Model} 建立了上游会话 {TopicId}", model, topicId);
            return topicId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>取站点配置里该模型标称的 maxTokens；取不到就返回 null 用默认值。</summary>
    private async Task<int?> ResolveMaxTokensAsync(RcouyiClient client, string model, CancellationToken ct)
    {
        if (_maxTokens.TryGetValue(model, out var cached))
        {
            return cached;
        }

        try
        {
            var models = await client.GetSiteModelsAsync(ct).ConfigureAwait(false);
            foreach (var item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || !string.Equals(value.GetString(), model, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("maxTokens", out var max) && max.TryGetInt32(out var parsed) && parsed > 0)
                {
                    _maxTokens[model] = parsed;
                    return parsed;
                }
            }
        }
        catch (Exception ex) when (ex is UpstreamException or HttpRequestException or JsonException)
        {
            _log.LogWarning(ex, "读取模型 {Model} 的标称上限失败，改用默认值", model);
        }

        return null;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded || !File.Exists(_path))
        {
            _loaded = true;
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            if (!File.Exists(_path))
            {
                return;
            }

            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            var map = JsonSerializer.Deserialize(text, AppJsonContext.Default.DictionaryStringInt64);
            if (map is null)
            {
                return;
            }

            foreach (var (model, topicId) in map)
            {
                _topics[model] = topicId;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.LogWarning(ex, "读取 {Path} 失败，将重新建立模型会话", _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var snapshot = _topics.ToDictionary(kv => kv.Key, kv => kv.Value);
        await File.WriteAllTextAsync(
            _path,
            JsonSerializer.Serialize(snapshot, AppJsonContext.Default.DictionaryStringInt64),
            ct).ConfigureAwait(false);
    }
}
