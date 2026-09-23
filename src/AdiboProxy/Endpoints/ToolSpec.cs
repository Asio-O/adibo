// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace AdiboProxy.Endpoints;

/// <summary>工具规格（已归一化，与具体协议无关）。</summary>
public sealed record ToolSpec(string Name, string? Description, JsonElement Parameters);
