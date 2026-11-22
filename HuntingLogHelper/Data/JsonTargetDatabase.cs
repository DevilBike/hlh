using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Data;

/// <summary>Loads targets from Data/class_targets.json (and gc_targets.json later). Filled in during step 2.</summary>
public sealed class JsonTargetDatabase : ITargetDatabase
{
    private readonly Dictionary<LogKind, List<HuntTarget>> targets = new();

    public JsonTargetDatabase(string dataDirectory)
    {
        targets[LogKind.Class] = Load(Path.Combine(dataDirectory, "class_targets.json"));
        targets[LogKind.GrandCompany] = Load(Path.Combine(dataDirectory, "gc_targets.json"));
    }

    public IReadOnlyList<HuntTarget> All(LogKind kind) => targets[kind];

    private static List<HuntTarget> Load(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<HuntTarget>>(File.ReadAllText(path))?.ToList() ?? new();
    }
}
