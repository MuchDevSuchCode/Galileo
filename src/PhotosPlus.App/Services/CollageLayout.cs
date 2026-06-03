using System;
using System.Collections.Generic;
using System.Linq;
using PhotosPlus.Models;

namespace PhotosPlus.Services;

/// <summary>One placed tile in a collage (canvas coordinates).</summary>
public readonly record struct CollageTile(PhotoItem Item, double X, double Y, double Width, double Height);

/// <summary>
/// "Justified rows, fitted to a rectangle" collage layout. Images keep their aspect ratio,
/// pack into rows that fill the width, and the row height is binary-searched so the stacked
/// rows fill the canvas height — giving an even, gap-free arrangement at any resolution.
/// </summary>
public static class CollageLayout
{
    public static List<CollageTile> Compute(IReadOnlyList<PhotoItem> items, double width, double height, double gutter)
    {
        var tiles = new List<CollageTile>();
        if (items.Count == 0 || width <= 1 || height <= 1) return tiles;

        var aspects = items.Select(i => i.Aspect is > 0 and < 100 ? i.Aspect : 1.0).ToArray();

        // Row target height that makes the total stacked height best fill the canvas.
        // Total height grows monotonically with the target row height, so binary-search it.
        double lo = 8, hi = height * 2;
        for (var iter = 0; iter < 48; iter++)
        {
            var mid = (lo + hi) / 2;
            if (TotalHeight(aspects, width, gutter, mid) > height) hi = mid; else lo = mid;
        }
        var rows = BuildRows(aspects, width, gutter, lo);

        var totalHeight = rows.Sum(r => r.Height) + gutter * Math.Max(0, rows.Count - 1);
        var y = Math.Max(0, (height - totalHeight) / 2); // centre vertically when there's slack

        foreach (var row in rows)
        {
            var rowWidth = row.Indices.Sum(i => aspects[i] * row.Height) + gutter * (row.Indices.Count - 1);
            var x = Math.Max(0, (width - rowWidth) / 2); // centres a non-full (usually last) row
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

            var naturalWidth = aspectSum * target + gutter * (current.Count - 1);
            if (naturalWidth >= width)
            {
                // Scale the full row so it exactly spans the width.
                var h = (width - gutter * (current.Count - 1)) / aspectSum;
                rows.Add((current, h));
                current = new List<int>();
                aspectSum = 0.0;
            }
        }

        if (current.Count > 0)
        {
            // Last partial row: keep target height, but never let it overflow the width.
            var naturalWidth = aspectSum * target + gutter * (current.Count - 1);
            var h = naturalWidth > width ? (width - gutter * (current.Count - 1)) / aspectSum : target;
            rows.Add((current, h));
        }

        return rows;
    }
}
