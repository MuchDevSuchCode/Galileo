using System;
using System.Collections.Generic;
using System.Linq;
using Galileo.Models;

namespace Galileo.Services;

/// <summary>One placed tile in a collage (canvas coordinates).</summary>
public readonly record struct CollageTile(PhotoItem Item, double X, double Y, double Width, double Height);

public enum CollagePreset
{
    Justified,  // aspect-preserving rows fitted to the canvas
    Grid,       // uniform cells (cropped to fill)
    Hero        // one large image + the rest justified beside/below it
}

/// <summary>Collage layout algorithms. Every preset fills the canvas rectangle.</summary>
public static class CollageLayout
{
    public static List<CollageTile> Compute(IReadOnlyList<PhotoItem> items, double width, double height,
        double gutter, CollagePreset preset)
    {
        if (items.Count == 0 || width <= 1 || height <= 1) return new List<CollageTile>();
        return preset switch
        {
            CollagePreset.Grid => GridLayout(items, width, height, gutter),
            CollagePreset.Hero => HeroLayout(items, width, height, gutter),
            _ => JustifiedLayout(items, width, height, gutter),
        };
    }

    private static double Aspect(PhotoItem i) => i.Aspect is > 0 and < 100 ? i.Aspect : 1.0;

    // ---------- Justified: rows that fill the width, row height fit to canvas height ----------

    private static List<CollageTile> JustifiedLayout(IReadOnlyList<PhotoItem> items, double width, double height, double gutter)
    {
        var tiles = new List<CollageTile>();
        var aspects = items.Select(Aspect).ToArray();

        double lo = 8, hi = height * 2;
        for (var iter = 0; iter < 48; iter++)
        {
            var mid = (lo + hi) / 2;
            if (TotalHeight(aspects, width, gutter, mid) > height) hi = mid; else lo = mid;
        }
        var rows = BuildRows(aspects, width, gutter, lo);

        var totalHeight = rows.Sum(r => r.Height) + gutter * Math.Max(0, rows.Count - 1);
        var y = Math.Max(0, (height - totalHeight) / 2);

        foreach (var row in rows)
        {
            var rowWidth = row.Indices.Sum(i => aspects[i] * row.Height) + gutter * (row.Indices.Count - 1);
            var x = Math.Max(0, (width - rowWidth) / 2);
            foreach (var i in row.Indices)
            {
                var w = aspects[i] * row.Height;
                tiles.Add(new CollageTile(items[i], x, y, w, row.Height));
                x += w + gutter;
            }
            y += row.Height + gutter;
        }
        return tiles;
    }

    private static double TotalHeight(double[] aspects, double width, double gutter, double target)
    {
        var rows = BuildRows(aspects, width, gutter, target);
        return rows.Sum(r => r.Height) + gutter * Math.Max(0, rows.Count - 1);
    }

    private static List<(List<int> Indices, double Height)> BuildRows(double[] aspects, double width, double gutter, double target)
    {
        var rows = new List<(List<int>, double)>();
        var current = new List<int>();
        var aspectSum = 0.0;

        for (var i = 0; i < aspects.Length; i++)
        {
            current.Add(i);
            aspectSum += aspects[i];
            if (aspectSum * target + gutter * (current.Count - 1) >= width)
            {
                var h = (width - gutter * (current.Count - 1)) / aspectSum;
                rows.Add((current, h));
                current = new List<int>();
                aspectSum = 0.0;
            }
        }
        if (current.Count > 0)
        {
            var naturalWidth = aspectSum * target + gutter * (current.Count - 1);
            var h = naturalWidth > width ? (width - gutter * (current.Count - 1)) / aspectSum : target;
            rows.Add((current, h));
        }
        return rows;
    }

    // ---------- Grid: uniform cells, cropped to fill ----------

    private static List<CollageTile> GridLayout(IReadOnlyList<PhotoItem> items, double width, double height, double gutter)
    {
        var tiles = new List<CollageTile>();
        var n = items.Count;

        // Columns chosen to roughly match the canvas aspect, so cells are close to square.
        var cols = Math.Clamp((int)Math.Round(Math.Sqrt(n * (width / height))), 1, n);
        var rows = (int)Math.Ceiling((double)n / cols);

        var cellW = (width - gutter * (cols - 1)) / cols;
        var cellH = (height - gutter * (rows - 1)) / rows;

        for (var i = 0; i < n; i++)
        {
            var r = i / cols;
            var c = i % cols;

            // Centre a short final row.
            var inThisRow = Math.Min(cols, n - r * cols);
            var rowW = inThisRow * cellW + (inThisRow - 1) * gutter;
            var xOffset = (width - rowW) / 2;

            var x = xOffset + c * (cellW + gutter);
            var y = r * (cellH + gutter);
            tiles.Add(new CollageTile(items[i], x, y, cellW, cellH));
        }
        return tiles;
    }

    // ---------- Hero: one big image + the rest justified beside/below ----------

    private static List<CollageTile> HeroLayout(IReadOnlyList<PhotoItem> items, double width, double height, double gutter)
    {
        if (items.Count == 1)
            return new List<CollageTile> { new(items[0], 0, 0, width, height) };

        var hero = items[0];
        var rest = items.Skip(1).ToList();
        var tiles = new List<CollageTile>();
        const double heroFraction = 0.62;

        if (width >= height)
        {
            var heroW = width * heroFraction;
            tiles.Add(new CollageTile(hero, 0, 0, heroW, height));

            var restX = heroW + gutter;
            var restW = width - restX;
            foreach (var t in JustifiedLayout(rest, restW, height, gutter))
                tiles.Add(t with { X = t.X + restX });
        }
        else
        {
            var heroH = height * heroFraction;
            tiles.Add(new CollageTile(hero, 0, 0, width, heroH));

            var restY = heroH + gutter;
            var restH = height - restY;
            foreach (var t in JustifiedLayout(rest, width, restH, gutter))
                tiles.Add(t with { Y = t.Y + restY });
        }
        return tiles;
    }
}
