using SkiaSharp;

namespace Driftcheck.Mcp.Devices;

/// <summary>
/// Stitches multiple vertical scroll captures into a single tall screenshot and merged element/runtime-view list,
/// handling fixed/sticky headers (including pin-on-scroll headers), sticky bottom CTA bars, floating sticky buttons
/// (FABs), and bottom sheets so full-length screens can be compared accurately against tall Figma frames.
/// </summary>
public static class ScrollStitcher
{
    /// <param name="Capture">Captured viewport frame.</param>
    /// <param name="ScrollOffsetPx">Cumulative scroll offset from frame 0, in pixels.</param>
    /// <param name="TopFixedPx">Top fixed/sticky band height (status bar, app bar, sticky header, or bottom-sheet backdrop + header) detected for this frame.</param>
    /// <param name="BottomFixedPx">Bottom fixed/sticky band height (nav bar, sticky CTA bar, or persistent bottom sheet) detected for this frame.</param>
    /// <param name="StickyOverlaysPx">Partial-width sticky overlays (e.g. FABs or floating corner buttons) detected inside the scrollable region, in pixels.</param>
    public sealed record Frame(
        ScreenCapture Capture,
        int ScrollOffsetPx,
        int TopFixedPx = 0,
        int BottomFixedPx = 0,
        IReadOnlyList<Rect>? StickyOverlaysPx = null);

    /// <summary>
    /// Detects unchanged top and bottom bands (status bar, app bar, sticky header, bottom CTA bar, or bottom-sheet
    /// backdrop/header) between two consecutive screenshots so they are not duplicated when stitching.
    /// Supports tall static bands (up to 75% of screen height) for bottom sheets while preserving a minimum scrollable viewport.
    /// </summary>
    public static (int TopFixedPx, int BottomFixedPx) FindFixedBands(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (0, 0);
        int w = a.Width, h = a.Height;
        var pa = a.Pixels;
        var pb = b.Pixels;
        var minScrollable = Math.Min(80, h / 5);
        var maxCombined = Math.Max(0, h - minScrollable);
        var maxSingle = (int)(h * 0.75);

        bool RowsEqual(int y)
        {
            long diff = 0;
            var step = Math.Max(1, w / 64);
            var samples = 0;
            var row = y * w;
            for (var x = 0; x < w; x += step)
            {
                var ca = pa[row + x];
                var cb = pb[row + x];
                diff += Math.Abs(ca.Red - cb.Red) + Math.Abs(ca.Green - cb.Green) + Math.Abs(ca.Blue - cb.Blue);
                samples++;
            }
            return samples > 0 && (double)diff / (samples * 3) < 6.0;
        }

        var top = 0;
        while (top < maxSingle && RowsEqual(top)) top++;

        var bottomLimit = Math.Min(maxSingle, Math.Max(0, maxCombined - top));
        var bottom = 0;
        while (bottom < bottomLimit && RowsEqual(h - 1 - bottom)) bottom++;

        // If the entire screen didn't move, top reached maxSingle: return (0, 0) so EstimateScrollDeltaPx returns 0.
        if (top + bottom >= maxCombined && RowsEqual(top))
            return (0, 0);

        return (top, bottom);
    }

    /// <summary>
    /// Detects partial-width sticky overlays inside the scrollable region (such as a Floating Action Button or
    /// corner sticky button) that stayed at the exact same position between <paramref name="prev"/> and
    /// <paramref name="next"/> while the surrounding content scrolled.
    /// </summary>
    public static IReadOnlyList<Rect> FindStickyOverlaysPx(
        ScreenCapture prev, SKBitmap prevBmp,
        ScreenCapture next, SKBitmap nextBmp,
        int topFixedPx, int bottomFixedPx)
    {
        if (prevBmp.Width != nextBmp.Width || prevBmp.Height != nextBmp.Height) return [];
        int w = prevBmp.Width, h = prevBmp.Height;
        var scrollTop = Math.Clamp(topFixedPx, 0, h - 1);
        var scrollBottom = Math.Clamp(h - bottomFixedPx, scrollTop + 1, h);
        var result = new List<Rect>();

        // 1. Element-based detection: an element inside the scrollable region whose bounds did not move at all
        //    and whose pixels are unchanged between frames.
        var pa = prevBmp.Pixels;
        var pb = nextBmp.Pixels;
        var pxPerUnit = prev.PixelsPerUnit;

        foreach (var pe in prev.Elements)
        {
            var rPx = pe.Bounds.Scale(pxPerUnit);
            if (rPx.Y < scrollTop - 2 || rPx.Y + rPx.Height > scrollBottom + 2) continue;
            // Full-width banners are handled by FindFixedBands; focus on partial-width buttons/overlays or pinned headers.
            if (rPx.Width < 16 || rPx.Height < 16 || rPx.Width > w * 0.92 && rPx.Height > (scrollBottom - scrollTop) * 0.5) continue;

            var samePos = next.Elements.Any(ne =>
                Math.Abs(ne.Bounds.X - pe.Bounds.X) <= 2 &&
                Math.Abs(ne.Bounds.Y - pe.Bounds.Y) <= 2 &&
                Math.Abs(ne.Bounds.Width - pe.Bounds.Width) <= 2 &&
                Math.Abs(ne.Bounds.Height - pe.Bounds.Height) <= 2 &&
                ((pe.Id is not null && ne.Id == pe.Id) ||
                 (pe.Text is not null && ne.Text == pe.Text) ||
                 (pe.ContentDescription is not null && ne.ContentDescription == pe.ContentDescription)));

            if (!samePos) continue;
            // Check the inner 60% core so rounded buttons / circular FABs whose corners reveal scrolling background still match.
            var insetX = rPx.Width * 0.2;
            var insetY = rPx.Height * 0.2;
            var core = new Rect(rPx.X + insetX, rPx.Y + insetY, Math.Max(4, rPx.Width - 2 * insetX), Math.Max(4, rPx.Height - 2 * insetY));
            if (RectMad(pa, pb, w, h, core) < 8.0)
                AddMergedRect(result, rPx);
        }

        // 2. Pixel-tile fallback: catch untagged FABs / sticky floating buttons that have no uiautomator id/text.
        const int Tile = 24;
        var scrollH = scrollBottom - scrollTop;
        if (scrollH >= Tile * 3 && w >= Tile * 3)
        {
            int cols = w / Tile, rows = scrollH / Tile;
            var unchanged = new bool[rows, cols];
            var unchangedCount = 0;
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var tileRect = new Rect(c * Tile, scrollTop + r * Tile, Tile, Tile);
                if (RectMad(pa, pb, w, h, tileRect) < 5.0 && HasNonUniformColor(pa, w, tileRect))
                {
                    unchanged[r, c] = true;
                    unchangedCount++;
                }
            }

            // Only extract localized sticky islands (less than 30% of the scrollable area, so static backgrounds aren't flagged).
            if (unchangedCount > 0 && unchangedCount < rows * cols * 0.30)
            {
                var visited = new bool[rows, cols];
                for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                {
                    if (!unchanged[r, c] || visited[r, c]) continue;
                    int minR = r, maxR = r, minC = c, maxC = c, cells = 0;
                    var q = new Queue<(int R, int C)>();
                    q.Enqueue((r, c));
                    visited[r, c] = true;
                    while (q.Count > 0)
                    {
                        var (cr, cc) = q.Dequeue();
                        cells++;
                        if (cr < minR) minR = cr;
                        if (cr > maxR) maxR = cr;
                        if (cc < minC) minC = cc;
                        if (cc > maxC) maxC = cc;
                        foreach (var (nr, nc) in new[] { (cr - 1, cc), (cr + 1, cc), (cr, cc - 1), (cr, cc + 1) })
                        {
                            if (nr >= 0 && nr < rows && nc >= 0 && nc < cols && unchanged[nr, nc] && !visited[nr, nc])
                            {
                                visited[nr, nc] = true;
                                q.Enqueue((nr, nc));
                            }
                        }
                    }

                    if (cells >= 2)
                    {
                        var box = new Rect(minC * Tile, scrollTop + minR * Tile, (maxC - minC + 1) * Tile, (maxR - minR + 1) * Tile);
                        AddMergedRect(result, box);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Estimates how many pixels the screen scrolled downward between <paramref name="prev"/> and <paramref name="next"/>,
    /// ignoring fixed bands and partial-width sticky overlays (FABs / floating buttons).
    /// Returns 0 when the scrollable content did not move (end of scroll).
    /// </summary>
    public static int EstimateScrollDeltaPx(
        ScreenCapture prev, SKBitmap prevBmp,
        ScreenCapture next, SKBitmap nextBmp,
        int topFixedPx, int bottomFixedPx,
        IReadOnlyList<Rect>? stickyOverlaysPx = null)
    {
        if (prevBmp.Width != nextBmp.Width || prevBmp.Height != nextBmp.Height) return 0;
        int w = prevBmp.Width, h = prevBmp.Height;
        var scrollTop = Math.Clamp(topFixedPx, 0, h - 1);
        var scrollBottom = Math.Clamp(h - bottomFixedPx, scrollTop + 1, h);
        var scrollH = scrollBottom - scrollTop;
        if (scrollH < 20) return 0;

        var pa = prevBmp.Pixels;
        var pb = nextBmp.Pixels;
        var overlays = stickyOverlaysPx ?? [];

        // If the scrollable region barely changed, we are already at the bottom.
        if (RegionMad(pa, pb, w, scrollTop, scrollTop, scrollH, overlays) < 3.0)
            return 0;

        // 1. Try element-based delta first (ignoring elements that stayed stationary/sticky).
        var elementDelta = EstimateFromElements(prev, next, scrollTop, scrollBottom);
        if (elementDelta is > 4 and var ed && ed < scrollH - 8)
        {
            var stripH = Math.Min(Math.Max(24, scrollH / 5), scrollH - ed);
            var prevY = scrollTop + ed;
            if (RegionMad(pa, pb, w, prevY, scrollTop, stripH, overlays) < 15.0)
                return ed;
        }

        // 2. Pixel template matching: take a strip from the lower half of prev's scrollable area and find it in next.
        var templateH = Math.Clamp(scrollH / 5, 16, 120);
        var templateY = scrollBottom - templateH - Math.Min(20, scrollH / 10);
        if (templateY < scrollTop) templateY = scrollTop;

        var maxDelta = templateY - scrollTop;
        if (maxDelta <= 2) return 0;

        var bestDelta = 0;
        var bestMad = double.MaxValue;
        // Coarse pass (step 4 px) then fine pass (±4 px) for speed.
        for (var delta = 4; delta <= maxDelta; delta += 4)
        {
            var mad = RegionMad(pa, pb, w, templateY, templateY - delta, templateH, overlays);
            if (mad < bestMad) { bestMad = mad; bestDelta = delta; }
        }

        var lo = Math.Max(1, bestDelta - 4);
        var hi = Math.Min(maxDelta, bestDelta + 4);
        for (var delta = lo; delta <= hi; delta++)
        {
            var mad = RegionMad(pa, pb, w, templateY, templateY - delta, templateH, overlays);
            if (mad < bestMad) { bestMad = mad; bestDelta = delta; }
        }

        return bestMad < 18.0 && bestDelta > 2 ? bestDelta : 0;
    }

    static int? EstimateFromElements(ScreenCapture prev, ScreenCapture next, int scrollTopPx, int scrollBottomPx)
    {
        var topDp = scrollTopPx * prev.PxToUnit;
        var bottomDp = scrollBottomPx * prev.PxToUnit;
        var deltas = new List<double>();

        foreach (var pe in prev.Elements)
        {
            if (pe.Bounds.Y < topDp || pe.Bounds.Y + pe.Bounds.Height > bottomDp) continue;
            var key = pe.Id ?? pe.Text;
            if (string.IsNullOrWhiteSpace(key)) continue;

            var match = next.Elements.FirstOrDefault(ne =>
                ne.Bounds.Y >= topDp && ne.Bounds.Y + ne.Bounds.Height <= bottomDp &&
                Math.Abs(ne.Bounds.X - pe.Bounds.X) <= 3 &&
                Math.Abs(ne.Bounds.Width - pe.Bounds.Width) <= 3 &&
                ((pe.Id is not null && ne.Id == pe.Id) || (pe.Text is not null && ne.Text == pe.Text)));

            if (match is not null && pe.Bounds.Y - match.Bounds.Y > 4)
                deltas.Add((pe.Bounds.Y - match.Bounds.Y) * prev.PixelsPerUnit);
        }

        if (deltas.Count == 0) return null;
        deltas.Sort();
        return (int)Math.Round(deltas[deltas.Count / 2]);
    }

    static double RegionMad(SKColor[] a, SKColor[] b, int width, int yA, int yB, int height, IReadOnlyList<Rect> excludeOverlays)
    {
        var stepX = Math.Max(1, width / 48);
        var stepY = Math.Max(1, height / 24);
        long diff = 0;
        var count = 0;
        for (var dy = 0; dy < height; dy += stepY)
        {
            var pyA = yA + dy;
            var pyB = yB + dy;
            var rowA = pyA * width;
            var rowB = pyB * width;
            for (var x = 0; x < width; x += stepX)
            {
                if (excludeOverlays.Count > 0 && IsInsideAny(x, pyA, excludeOverlays) ||
                    excludeOverlays.Count > 0 && IsInsideAny(x, pyB, excludeOverlays))
                    continue;
                var ca = a[rowA + x];
                var cb = b[rowB + x];
                diff += Math.Abs(ca.Red - cb.Red) + Math.Abs(ca.Green - cb.Green) + Math.Abs(ca.Blue - cb.Blue);
                count++;
            }
        }
        return count == 0 ? 255 : (double)diff / (count * 3);
    }

    static double RectMad(SKColor[] a, SKColor[] b, int width, int height, Rect r)
    {
        int x1 = Math.Clamp((int)r.X, 0, width - 1);
        int y1 = Math.Clamp((int)r.Y, 0, height - 1);
        int x2 = Math.Clamp((int)(r.X + r.Width), x1 + 1, width);
        int y2 = Math.Clamp((int)(r.Y + r.Height), y1 + 1, height);
        var stepX = Math.Max(1, (x2 - x1) / 16);
        var stepY = Math.Max(1, (y2 - y1) / 16);
        long diff = 0;
        var count = 0;
        for (var y = y1; y < y2; y += stepY)
        {
            var row = y * width;
            for (var x = x1; x < x2; x += stepX)
            {
                var ca = a[row + x];
                var cb = b[row + x];
                diff += Math.Abs(ca.Red - cb.Red) + Math.Abs(ca.Green - cb.Green) + Math.Abs(ca.Blue - cb.Blue);
                count++;
            }
        }
        return count == 0 ? 255 : (double)diff / (count * 3);
    }

    /// <summary>Returns true when a tile has actual visual contrast (not a solid blank background).</summary>
    static bool HasNonUniformColor(SKColor[] pixels, int width, Rect r)
    {
        int x1 = (int)r.X, y1 = (int)r.Y, x2 = (int)(r.X + r.Width), y2 = (int)(r.Y + r.Height);
        var first = pixels[y1 * width + x1];
        for (var y = y1; y < y2; y += 3)
        for (var x = x1; x < x2; x += 3)
        {
            var c = pixels[y * width + x];
            if (Math.Abs(c.Red - first.Red) + Math.Abs(c.Green - first.Green) + Math.Abs(c.Blue - first.Blue) > 24)
                return true;
        }
        return false;
    }

    static bool IsInsideAny(int x, int y, IReadOnlyList<Rect> rects)
    {
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height)
                return true;
        }
        return false;
    }

    static void AddMergedRect(List<Rect> list, Rect rect)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var overlaps = !(rect.X + rect.Width < e.X - 4 || e.X + e.Width < rect.X - 4 ||
                             rect.Y + rect.Height < e.Y - 4 || e.Y + e.Height < rect.Y - 4);
            if (overlaps)
            {
                var minX = Math.Min(e.X, rect.X);
                var minY = Math.Min(e.Y, rect.Y);
                var maxX = Math.Max(e.X + e.Width, rect.X + rect.Width);
                var maxY = Math.Max(e.Y + e.Height, rect.Y + rect.Height);
                list[i] = new Rect(minX, minY, maxX - minX, maxY - minY);
                return;
            }
        }
        list.Add(rect);
    }

    /// <summary>
    /// Stitches the captured frames into a single PNG at <paramref name="outputPath"/> and merges their elements and runtime views.
    /// Per-frame <see cref="Frame.TopFixedPx"/>, <see cref="Frame.BottomFixedPx"/> and <see cref="Frame.StickyOverlaysPx"/>
    /// take precedence over the fallback <paramref name="topFixedPx"/> / <paramref name="bottomFixedPx"/> parameters.
    /// </summary>
    public static ScreenCapture Stitch(IReadOnlyList<Frame> frames, string outputPath, int topFixedPx = 0, int bottomFixedPx = 0)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (frames.Count == 1) return frames[0].Capture;

        var first = frames[0].Capture;
        var totalOffsetPx = frames[^1].ScrollOffsetPx;
        var totalHeightPx = first.HeightPx + totalOffsetPx;
        var widthPx = first.WidthPx;

        int TopFixed(int i) => frames[i].TopFixedPx > 0 ? frames[i].TopFixedPx : topFixedPx;
        int BottomFixed(int i) => frames[i].BottomFixedPx > 0 ? frames[i].BottomFixedPx : bottomFixedPx;

        var lastBottomFixed = BottomFixed(frames.Count - 1);

        using var stitched = new SKBitmap(widthPx, totalHeightPx);
        using (var canvas = new SKCanvas(stitched))
        {
            canvas.Clear(SKColors.White);

            // Draw first frame (everything above its bottom fixed band).
            var firstBottom = BottomFixed(0);
            using (var bmp0 = SKBitmap.Decode(first.ScreenshotPath)
                ?? throw new InvalidOperationException($"Could not read screenshot {first.ScreenshotPath}"))
            using (var img0 = SKImage.FromBitmap(bmp0))
            {
                var srcH = first.HeightPx - firstBottom;
                canvas.DrawImage(img0, SKRect.Create(0, 0, widthPx, srcH), SKRect.Create(0, 0, widthPx, srcH), SKSamplingOptions.Default);
            }

            // Draw newly revealed slice from each subsequent frame, respecting per-frame sticky headers/footers
            // and patching over any floating sticky overlays (FABs) from the previous frame once content scrolls clear.
            for (var i = 1; i < frames.Count; i++)
            {
                var deltaPx = frames[i].ScrollOffsetPx - frames[i - 1].ScrollOffsetPx;
                if (deltaPx <= 0) continue;

                var frameTop = TopFixed(i);
                var frameBottom = BottomFixed(i);
                var scrollBottomY = first.HeightPx - frameBottom;

                using var bmp = SKBitmap.Decode(frames[i].Capture.ScreenshotPath)
                    ?? throw new InvalidOperationException($"Could not read screenshot {frames[i].Capture.ScreenshotPath}");
                using var img = SKImage.FromBitmap(bmp);

                // Patch regions that were obscured by floating sticky overlays (FABs) in frame i-1
                // using the now-unobscured content that scrolled upward in frame i.
                var prevOverlays = frames[i].StickyOverlaysPx ?? frames[i - 1].StickyOverlaysPx;
                if (prevOverlays is { Count: > 0 })
                {
                    foreach (var ov in prevOverlays)
                    {
                        var revealedY = ov.Y - deltaPx;
                        if (revealedY >= frameTop && revealedY + ov.Height <= scrollBottomY)
                        {
                            var srcRect = SKRect.Create((float)ov.X, (float)revealedY, (float)ov.Width, (float)ov.Height);
                            var dstRect = SKRect.Create((float)ov.X, (float)(ov.Y + frames[i - 1].ScrollOffsetPx), (float)ov.Width, (float)ov.Height);
                            canvas.DrawImage(img, srcRect, dstRect, SKSamplingOptions.Default);
                        }
                    }
                }

                var srcTop = Math.Max(frameTop, scrollBottomY - deltaPx);
                var sliceH = scrollBottomY - srcTop;
                if (sliceH > 0)
                {
                    var destTop = srcTop + frames[i].ScrollOffsetPx;
                    canvas.DrawImage(img, SKRect.Create(0, srcTop, widthPx, sliceH), SKRect.Create(0, destTop, widthPx, sliceH), SKSamplingOptions.Default);
                }
            }

            // Draw fixed bottom bar / persistent bottom sheet once from the last frame at the very bottom.
            var last = frames[^1].Capture;
            using var bmpLast = SKBitmap.Decode(last.ScreenshotPath)
                ?? throw new InvalidOperationException($"Could not read screenshot {last.ScreenshotPath}");
            using var imgLast = SKImage.FromBitmap(bmpLast);

            if (lastBottomFixed > 0)
            {
                canvas.DrawImage(imgLast,
                    SKRect.Create(0, last.HeightPx - lastBottomFixed, widthPx, lastBottomFixed),
                    SKRect.Create(0, totalHeightPx - lastBottomFixed, widthPx, lastBottomFixed),
                    SKSamplingOptions.Default);
            }

            // Draw floating sticky overlays (FABs / corner buttons) once at their final position in the last frame.
            var finalOverlays = frames[^1].StickyOverlaysPx;
            if (finalOverlays is { Count: > 0 })
            {
                foreach (var ov in finalOverlays)
                {
                    var srcRect = SKRect.Create((float)ov.X, (float)ov.Y, (float)ov.Width, (float)ov.Height);
                    var dstRect = SKRect.Create((float)ov.X, (float)(ov.Y + totalOffsetPx), (float)ov.Width, (float)ov.Height);
                    canvas.DrawImage(imgLast, srcRect, dstRect, SKSamplingOptions.Default);
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var data = stitched.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(outputPath, data.ToArray());

        var pxToDp = first.PxToUnit;
        var mergedElements = MergeElements(frames, pxToDp, TopFixed, BottomFixed);
        var mergedRuntime = MergeRuntimeViews(frames, pxToDp, TopFixed, BottomFixed);

        return first with
        {
            ScreenshotPath = outputPath,
            HeightPx = totalHeightPx,
            Elements = mergedElements,
            RuntimeViews = mergedRuntime.Count > 0 ? mergedRuntime : null,
        };
    }

    static IReadOnlyList<ScreenElement> MergeElements(
        IReadOnlyList<Frame> frames,
        double pxToDp,
        Func<int, int> topFixedPx,
        Func<int, int> bottomFixedPx)
    {
        var merged = new List<ScreenElement>();
        for (var i = 0; i < frames.Count; i++)
        {
            var offsetDp = frames[i].ScrollOffsetPx * pxToDp;
            var isFirst = i == 0;
            var isLast = i == frames.Count - 1;
            var topDp = topFixedPx(i) * pxToDp;
            var bottomStartDp = (frames[i].Capture.HeightPx - bottomFixedPx(i)) * pxToDp;
            var overlaysDp = (frames[i].StickyOverlaysPx ?? []).Select(r => r.Scale(pxToDp)).ToList();

            foreach (var e in frames[i].Capture.Elements)
            {
                // Keep fixed/sticky top elements only once (when they first appeared / in frame 0).
                if (!isFirst && topDp > 0 && e.Bounds.Y + e.Bounds.Height <= topDp + 2) continue;
                // Content only enters a frame from the bottom: what touches the top of the scrolling area was
                // already merged from an earlier frame, and here it's cut by the header (a shorter, shifted copy).
                if (!isFirst && e.Bounds.Y <= topDp + 1) continue;
                // Keep fixed bottom bar / persistent bottom sheet elements only once (from the last frame).
                if (!isLast && bottomFixedPx(i) > 0 && e.Bounds.Y >= bottomStartDp - 2) continue;
                // Keep floating sticky buttons (FABs) only once (from the last frame).
                if (!isLast && overlaysDp.Count > 0 && IsInsideOverlay(e.Bounds, overlaysDp)) continue;

                var shifted = e with { Bounds = e.Bounds.Offset(0, offsetDp) };
                var dupIdx = merged.FindIndex(m => IsDuplicateElement(m, shifted));
                if (dupIdx >= 0)
                {
                    var existing = merged[dupIdx];
                    var existingArea = existing.Bounds.Width * existing.Bounds.Height;
                    var shiftedArea = shifted.Bounds.Width * shifted.Bounds.Height;
                    if ((shifted.Runtime is not null && existing.Runtime is null) || shiftedArea > existingArea * 1.05)
                        merged[dupIdx] = shifted;
                }
                else
                {
                    merged.Add(shifted);
                }
            }
        }
        return merged;
    }

    static IReadOnlyList<RuntimeView> MergeRuntimeViews(
        IReadOnlyList<Frame> frames,
        double pxToDp,
        Func<int, int> topFixedPx,
        Func<int, int> bottomFixedPx)
    {
        var merged = new List<RuntimeView>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Capture.RuntimeViews is not { Count: > 0 } views) continue;
            var offsetDp = frames[i].ScrollOffsetPx * pxToDp;
            var isFirst = i == 0;
            var isLast = i == frames.Count - 1;
            var topDp = topFixedPx(i) * pxToDp;
            var bottomStartDp = (frames[i].Capture.HeightPx - bottomFixedPx(i)) * pxToDp;
            var overlaysDp = (frames[i].StickyOverlaysPx ?? []).Select(r => r.Scale(pxToDp)).ToList();

            foreach (var v in views)
            {
                if (!isFirst && topDp > 0 && v.Bounds.Y + v.Bounds.Height <= topDp + 2) continue;
                if (!isFirst && v.Bounds.Y <= topDp + 1) continue;
                if (!isLast && bottomFixedPx(i) > 0 && v.Bounds.Y >= bottomStartDp - 2) continue;
                if (!isLast && overlaysDp.Count > 0 && IsInsideOverlay(v.Bounds, overlaysDp)) continue;

                var shifted = v with { Bounds = v.Bounds.Offset(0, offsetDp) };
                var dup = merged.Any(m =>
                    m.Props.ClassName == shifted.Props.ClassName &&
                    Math.Abs(m.Bounds.X - shifted.Bounds.X) <= 3 &&
                    Math.Abs(m.Bounds.Y - shifted.Bounds.Y) <= 3 &&
                    Math.Abs(m.Bounds.Width - shifted.Bounds.Width) <= 3 &&
                    Math.Abs(m.Bounds.Height - shifted.Bounds.Height) <= 3);
                if (!dup) merged.Add(shifted);
            }
        }
        return merged;
    }

    static bool IsInsideOverlay(Rect bounds, List<Rect> overlays)
    {
        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        return overlays.Any(o => cx >= o.X - 4 && cx <= o.X + o.Width + 4 && cy >= o.Y - 4 && cy <= o.Y + o.Height + 4);
    }

    static bool IsDuplicateElement(ScreenElement a, ScreenElement b)
    {
        var sameContent = (a.Id is not null && a.Id == b.Id) ||
                          (a.Text is not null && a.Text == b.Text && a.ClassName == b.ClassName);
        if (!sameContent) return false;
        return Math.Abs(a.Bounds.X - b.Bounds.X) <= 4 && Math.Abs(a.Bounds.Y - b.Bounds.Y) <= 6;
    }
}
