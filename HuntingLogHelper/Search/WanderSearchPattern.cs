using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Search;

/// <summary>
/// Natural-looking search. With observed spawn points it drifts between nearby ones in a
/// loose, varied order. Without them it does a meandering random walk inside the area.
/// Every point gets positional jitter and some points get a short idle pause.
/// </summary>
public sealed class WanderSearchPattern : ISearchPattern
{
    private readonly Random rng = new();

    public IEnumerable<Waypoint> Waypoints(HuntArea area, Vector3 playerPosition)
    {
        var points = area.SpawnPoints.Count > 0
            ? FromSpawnPoints(area, playerPosition)
            : RandomWalk(area, playerPosition);

        foreach (var p in points)
            yield return new Waypoint(new Vector3(p.X, playerPosition.Y, p.Y), Pause());
    }

    private IEnumerable<Vector2> FromSpawnPoints(HuntArea area, Vector3 player)
    {
        var remaining = area.SpawnPoints.Select(s => new Vector2(s[0], s[1])).ToList();
        var here = new Vector2(player.X, player.Z);
        var count = Math.Min(remaining.Count, rng.Next(6, 12));

        for (var i = 0; i < count && remaining.Count > 0; i++)
        {
            // Pick among the 3 closest unvisited points, favouring the closest: looks like browsing, not sweeping.
            var options = remaining.OrderBy(p => Vector2.Distance(p, here)).Take(3).ToList();
            var pick = options[WeightedIndex(options.Count)];
            remaining.Remove(pick);
            here = Jitter(pick, 3f, 7f);
            yield return here;
        }
    }

    private IEnumerable<Vector2> RandomWalk(HuntArea area, Vector3 player)
    {
        var center = area.CenterXZ;
        var here = new Vector2(player.X, player.Z);
        if (Vector2.Distance(here, center) > area.Radius) here = Jitter(center, 0f, area.Radius * 0.3f);
        var heading = (float)(rng.NextDouble() * Math.PI * 2);
        var count = rng.Next(6, 11);

        for (var i = 0; i < count; i++)
        {
            heading += (float)((rng.NextDouble() - 0.5) * Math.PI * 0.9); // turn up to about ±80°
            var step = 12f + (float)rng.NextDouble() * 22f;
            var next = here + new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * step;

            // Stay inside the area: steer back toward the middle when drifting out.
            if (Vector2.Distance(next, center) > area.Radius)
            {
                var toCenter = center - here;
                heading = MathF.Atan2(toCenter.Y, toCenter.X) + (float)((rng.NextDouble() - 0.5) * 0.8);
                next = here + new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * step;
            }
            here = next;
            yield return here;
        }
    }

    private Vector2 Jitter(Vector2 p, float min, float max)
    {
        var a = (float)(rng.NextDouble() * Math.PI * 2);
        var r = min + (float)rng.NextDouble() * (max - min);
        return p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
    }

    /// <summary>Most points: no pause. Some: a short look-around.</summary>
    private float Pause() => rng.NextDouble() < 0.35 ? 0.6f + (float)rng.NextDouble() * 2.4f : 0f;

    private int WeightedIndex(int n)
    {
        var roll = rng.NextDouble();
        return n switch { 1 => 0, 2 => roll < 0.7 ? 0 : 1, _ => roll < 0.6 ? 0 : roll < 0.88 ? 1 : 2 };
    }
}
