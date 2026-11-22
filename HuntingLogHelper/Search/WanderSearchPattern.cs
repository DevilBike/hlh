using System;
using System.Collections.Generic;
using System.Numerics;
using HuntingLogHelper.Core;

namespace HuntingLogHelper.Search;

/// <summary>Randomized wander inside the area: no grid, varied spacing, revisits allowed. Tuned in step 2.</summary>
public sealed class WanderSearchPattern : ISearchPattern
{
    private readonly Random rng = new();

    public IEnumerable<Vector3> Waypoints(Vector3 center, float radius)
    {
        var count = rng.Next(5, 10);
        for (var i = 0; i < count; i++)
        {
            // Uniform point in a disc, biased slightly toward the middle where spawns cluster.
            var r = radius * MathF.Pow((float)rng.NextDouble(), 0.6f);
            var a = (float)(rng.NextDouble() * Math.PI * 2);
            yield return center + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
        }
    }
}
