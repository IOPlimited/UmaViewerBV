using System;
using System.Collections.Generic;
using System.Linq;
using BigInteger = System.Numerics.BigInteger;

/// <summary>Exact bipartite filters; kinematic copies share geometry, not contacts.</summary>
internal static class PMXPhysicsCollisionGroups
{
    internal sealed class Copy
    {
        internal PMXSourcePhysicsSnapshot.Collider Source;
        internal int Group;
        internal ushort Mask;
    }

    internal sealed class Result
    {
        internal readonly Dictionary<PMXSourcePhysicsSnapshot.Node, int> Groups = new Dictionary<PMXSourcePhysicsSnapshot.Node, int>();
        internal readonly Dictionary<PMXSourcePhysicsSnapshot.Node, ushort> Masks = new Dictionary<PMXSourcePhysicsSnapshot.Node, ushort>();
        internal readonly List<Copy> Copies = new List<Copy>();
        internal int GroupCount;
    }

    internal static Result Compile(IList<PMXSourcePhysicsSnapshot.Node> nodes)
    {
        var result = new Result();
        if (nodes.Count == 0) return result;
        var colliders = nodes.SelectMany(node => node.Colliders).Distinct().OrderBy(c => c.Index).ToArray();
        var hits = colliders.Select(c => nodes.Where(n => n.Colliders.Contains(c))
            .Aggregate(BigInteger.Zero, (mask, n) => mask | (BigInteger.One << n.Index))).ToArray();
        var initial = nodes.Where(node => node.Colliders.Count != 0)
            .GroupBy(node => string.Join(",", node.Colliders.Select(c => c.Index).OrderBy(i => i)))
            .Select(group => group.Aggregate(BigInteger.Zero, (mask, node) => mask | (BigInteger.One << node.Index))).ToList();
        List<BigInteger> partition = null;
        int slots = 0;
        if (initial.Count == 0) partition = new List<BigInteger> { BigInteger.Zero };
        for (int capacity = 2; partition == null && capacity <= 12; capacity++)
        {
            for (int seed = 0; seed < 16; seed++)
            {
                var groups = new List<BigInteger>(initial);
                var random = new Random(seed);
                while (groups.Count > 1)
                {
                    int best = int.MaxValue, chosenA = -1, chosenB = -1, ties = 0;
                    for (int a = 1; a < groups.Count; a++)
                        for (int b = 0; b < a; b++)
                        {
                            BigInteger merged = groups[a] | groups[b];
                            int cost = hits.Select(hit => hit & merged).Where(hit => !hit.IsZero).Distinct().Count();
                            if (cost > capacity || cost > best) continue;
                            if (cost < best) { best = cost; ties = 0; }
                            if (random.Next(++ties) == 0) { chosenA = a; chosenB = b; }
                        }
                    if (chosenA < 0) break;
                    groups[chosenB] |= groups[chosenA];
                    groups.RemoveAt(chosenA);
                }
                if (groups.Count + capacity <= 16) { partition = groups; slots = capacity; break; }
            }
        }
        if (partition == null)
            throw new InvalidOperationException("Source collision filters could not be encoded exactly in 16 PMX groups. Export a scoped experiment; no collision pairs were widened.");
        for (int group = 0; group < partition.Count; group++)
        {
            var patterns = new Dictionary<BigInteger, int>();
            for (int i = 0; i < colliders.Length; i++)
            {
                BigInteger pattern = hits[i] & partition[group];
                if (pattern.IsZero) continue;
                if (!patterns.TryGetValue(pattern, out int slot)) patterns[pattern] = slot = patterns.Count;
                result.Copies.Add(new Copy { Source = colliders[i], Group = partition.Count + slot, Mask = (ushort)(1 << group) });
            }
            foreach (var node in nodes.Where(n => !(partition[group] & (BigInteger.One << n.Index)).IsZero))
            {
                ushort mask = 0;
                foreach (var pattern in patterns)
                    if (!(pattern.Key & (BigInteger.One << node.Index)).IsZero) mask |= (ushort)(1 << (partition.Count + pattern.Value));
                result.Groups[node] = group;
                result.Masks[node] = mask;
            }
        }
        foreach (var node in nodes.Where(node => node.Colliders.Count == 0))
        {
            result.Groups[node] = 0;
            result.Masks[node] = 0;
        }
        result.GroupCount = partition.Count + slots;
        // Check the actual two-sided Bullet filter, including every excluded pair.
        foreach (var node in nodes)
            foreach (var collider in colliders)
            {
                bool actual = result.Copies.Where(copy => copy.Source == collider).Any(copy =>
                    (result.Masks[node] & (1 << copy.Group)) != 0 && (copy.Mask & (1 << result.Groups[node])) != 0);
                if (actual != node.Colliders.Contains(collider)) throw new InvalidOperationException("Source collision filter mismatch: " + node.Source.name + " / " + collider.Name);
            }
        return result;
    }
}
