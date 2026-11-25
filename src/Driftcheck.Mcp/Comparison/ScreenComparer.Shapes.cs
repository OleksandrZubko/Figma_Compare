using System.Text.RegularExpressions;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Figma;
using SkiaSharp;

namespace Driftcheck.Mcp.Comparison;

public sealed partial class ScreenComparer
{
    /// <summary>
    /// Fracción de la grilla de un ícono o una imagen que puede cambiar sin reportarlo: el antialiasing y el
    /// cambio de densidad (2x en el diseño, 2.625x en un Pixel 7) mueven los bordes de los trazos.
    /// </summary>
    const double ImageDifference = 0.2;

    /// <summary>Franjas del frame donde los diseños dibujan el teléfono: la barra de estado y el indicador de inicio.</summary>
    const double StatusBarHeight = 44, HomeIndicatorHeight = 34;

    /// <summary>Cuánto más allá de donde debería estar se busca una forma, en dp o pt.</summary>
    const double ShapeSearchMargin = 12;

    sealed record ShapeContext(
        PixelImage Design, SKBitmap DesignBitmap, double DesignPxPerUnit,
        PixelImage Screen, SKBitmap ScreenBitmap, double PxToUnit,
        double Scale, double Dx, double Dy, double ExtraWidth, double Tolerance, double ColorTolerance,
        IReadOnlyList<RuntimeView> RuntimeViews);

    /// <param name="DesignBox">Caja medida en el PNG del diseño, en unidades del frame.</param>
    /// <param name="Actual">Caja medida en la captura; null si no está.</param>
    /// <param name="Background">Color alrededor en el diseño, para borrarla del diseño alineado.</param>
    sealed record ShapeCheck(
        DesignElement Design, string Label, Rect DesignBox, Rect Expected, Rect? Actual, Rgb Background,
        List<(string Property, string Expected, string Actual)> Diffs);

    sealed record ShapeComparison(List<ShapeCheck> Checks, int DeviceChrome, int NotMeasurable);

    /// <summary>
    /// Capas sin texto (botones, campos, líneas, íconos, imágenes): la app no las lista, así que se buscan en
    /// la captura por cómo se ven y se miden con el mismo algoritmo que en el PNG del diseño. Se compara la
    /// caja, el color de relleno, el radio de las esquinas y el borde; en íconos e imágenes, el contenido.
    /// </summary>
    static ShapeComparison CompareShapes(DesignFrame design, ShapeContext c)
    {
        var checks = new List<ShapeCheck>();
        var chrome = 0;
        var notMeasurable = 0;

        foreach (var shape in design.Shapes ?? [])
        {
            if (IsDeviceChrome(shape, design)) { chrome++; continue; }

            var solid = shape.Shape == ShapeKind.Shape;
            var border = shape.Stroke is not null;
            var designSample = ShapeProbe.Measure(c.Design, [shape.Bounds.Scale(c.DesignPxPerUnit)],
                ShapeSearchMargin * c.DesignPxPerUnit, solid, border);

            // Una forma que no se distingue de su fondo en el diseño (blanca sobre blanco, sin borde ni sombra)
            // tampoco se puede buscar en la app.
            if (designSample is null) { notMeasurable++; continue; }

            var designBox = designSample.Bounds.Scale(1 / c.DesignPxPerUnit);

            // Un contenedor y su hijo pueden pintar lo mismo (una instancia con fondo y su frame con el mismo fondo).
            if (checks.Any(k => ShapeProbe.Overlap(k.DesignBox, designBox) > 0.9)) continue;

            var at = designBox.Scale(c.Scale).Offset(c.Dx, c.Dy);
            // Un ícono o una imagen tienen tamaño fijo: no se estiran con el ancho del dispositivo.
            var options = HorizontalOptions(at, c.ExtraWidth, solid ? shape.WidthShare : null)
                .Where(o => solid || o.Width == at.Width).ToList();
            var appSample = ShapeProbe.Measure(c.Screen, options.Select(o => o.Scale(1 / c.PxToUnit)).ToList(),
                ShapeSearchMargin / c.PxToUnit, solid, border);

            // Si falta un ícono, lo más cercano suele ser otra cosa (la primera letra del texto que se corrió a
            // su lugar): con la mitad o el doble del área, no es el ícono.
            if (!solid && appSample is not null)
            {
                var ratio = appSample.Bounds.Width * appSample.Bounds.Height * c.PxToUnit * c.PxToUnit / (at.Width * at.Height);
                if (ratio is < 0.5 or > 2) appSample = null;
            }

            var label = ShapeLabel(shape, design.Elements);
            var diffs = new List<(string, string, string)>();
            if (appSample is null)
            {
                // Sin la app no se sabe cómo se ancla: se ubica como el PNG del diseño, centrado.
                checks.Add(new ShapeCheck(shape, label, designBox, at.Offset(c.ExtraWidth / 2, 0), null, designSample.Background, diffs));
                continue;
            }

            var actual = appSample.Bounds.Scale(c.PxToUnit);
            var expected = Closest(options, actual);
            void Check(string prop, double exp, double act, double allowed)
            {
                if (Math.Abs(exp - act) > allowed) diffs.Add((prop, F(exp), F(act)));
            }

            Check("x", expected.X, actual.X, c.Tolerance);
            Check("y", expected.Y, actual.Y, c.Tolerance);
            Check("width", expected.Width, actual.Width, c.Tolerance);
            Check("height", expected.Height, actual.Height, c.Tolerance);

            if (solid && shape.Fill is not null && appSample.Fill.DistanceTo(designSample.Fill) > c.ColorTolerance)
                diffs.Add(("background", designSample.Fill.Hex, appSample.Fill.Hex));

            if (designSample.CornerRadius is { } designRadius && appSample.CornerRadius is { } appRadius)
            {
                var exp = designRadius / c.DesignPxPerUnit * c.Scale;
                Check("corner radius", exp, appRadius * c.PxToUnit, Math.Max(c.Tolerance, exp * 0.2));
            }

            if (designSample.Border is { } designBorder)
            {
                if (appSample.Border is not { } appBorder) diffs.Add(("border", designBorder.Hex, "no border"));
                else if (appBorder.DistanceTo(designBorder) > c.ColorTolerance) diffs.Add(("border", designBorder.Hex, appBorder.Hex));
            }

            // Match Frida runtime view for shapes to check stroke width, shadow, opacity.
            if (solid)
            {
                var runtimeBox = FindRuntimeBox(c.RuntimeViews, actual, c.Tolerance);
                if (runtimeBox is { Props: var rp })
                {
                    if (Math.Abs(shape.Opacity - rp.Alpha) > 0.05)
                        diffs.Add(("opacity", F2(shape.Opacity), F2(rp.Alpha)));
                    if (shape.Shadow != rp.Shadow)
                        diffs.Add(("shadow", shape.Shadow ? "drop shadow" : "none",
                            rp.Shadow ? (rp.Elevation > 0.5 ? $"elevation {F(rp.Elevation)} dp" : "shadow") : "none"));
                    if (shape.StrokeWeight is { } sw && sw > 0 && rp.Box?.StrokeWidth is { } appSw &&
                        Math.Abs(sw * c.Scale - appSw) > Math.Max(0.5, c.Tolerance / 2))
                        diffs.Add(("border width", F(sw * c.Scale), F(appSw)));
                }
            }

            // With the icon read from the app (Frida), its color(s) and size are compared as values: the colors it
            // paints and the box it's drawn in, which is what the Figma layer measures (not the visible strokes).
            var runtimeIcon = shape.Shape == ShapeKind.Icon ? FindRuntimeIcon(c.RuntimeViews, actual) : null;
            if (runtimeIcon is { Props.Icon: { } icon })
            {
                if (Math.Abs(shape.Opacity - runtimeIcon.Props.Alpha) > 0.05)
                    diffs.Add(("opacity", F2(shape.Opacity), F2(runtimeIcon.Props.Alpha)));

                var figmaColors = shape.IconColors ?? (shape.Fill is { } single ? [single] : null);
                if (!icon.Gradient && figmaColors is { Count: > 0 } && icon.Colors.Count > 0)
                {
                    if (figmaColors.Count == 1)
                    {
                        if (icon.Colors.All(col => col.DistanceTo(figmaColors[0]) > c.ColorTolerance))
                            diffs.Add(("icon color", figmaColors[0].Hex, string.Join(", ", icon.Colors.Select(col => col.Hex))));
                    }
                    else
                    {
                        var allFigmaMatched = figmaColors.All(fc => icon.Colors.Any(ac => ac.DistanceTo(fc) <= c.ColorTolerance));
                        var allAppMatched = icon.Colors.All(ac => figmaColors.Any(fc => fc.DistanceTo(ac) <= c.ColorTolerance));
                        if (!allFigmaMatched || !allAppMatched)
                            diffs.Add(("icon color",
                                string.Join(", ", figmaColors.Select(col => col.Hex)),
                                string.Join(", ", icon.Colors.Select(col => col.Hex))));
                    }
                }

                var layer = shape.Bounds.Scale(c.Scale);
                if (Math.Abs(layer.Width - runtimeIcon.Bounds.Width) > c.Tolerance || Math.Abs(layer.Height - runtimeIcon.Bounds.Height) > c.Tolerance)
                    diffs.Add(("icon size", $"{F(layer.Width)}×{F(layer.Height)}", $"{F(runtimeIcon.Bounds.Width)}×{F(runtimeIcon.Bounds.Height)}"));
            }
            else if (shape.Shape == ShapeKind.Icon && !shape.MultiColor && appSample.Fill.DistanceTo(designSample.Fill) > c.ColorTolerance)
                diffs.Add(("icon color", designSample.Fill.Hex, appSample.Fill.Hex));

            // El contenido solo se compara si la caja coincide: si no, la diferencia ya está reportada y el
            // recorte deformado siempre daría distinto.
            if (!solid && diffs.Count == 0)
            {
                var difference = ShapeProbe.Difference(c.DesignBitmap, designSample.Bounds, c.ScreenBitmap, appSample.Bounds);
                if (difference > ImageDifference) diffs.Add(("image", "as in Figma", $"{difference:P0} different"));
            }

            checks.Add(new ShapeCheck(shape, label, designBox, expected, actual, designSample.Background, diffs));
        }

        // Nombres repetidos ("Línea" ×2): se les agrega la posición para distinguirlos en el reporte.
        foreach (var group in checks.GroupBy(k => k.Label).Where(g => g.Count() > 1))
            foreach (var k in group.ToList())
                checks[checks.IndexOf(k)] = k with { Label = $"{k.Label} (x={F(k.DesignBox.X)}, y={F(k.DesignBox.Y)})" };

        return new ShapeComparison(checks, chrome, notMeasurable);
    }

    /// <summary>
    /// The icon view (an &lt;Svg&gt;, a tinted ImageView) that draws what was found at <paramref name="visible"/>:
    /// the smallest one that contains its center and isn't much bigger than it (not a full-screen illustration).
    /// </summary>
    static RuntimeView? FindRuntimeIcon(IReadOnlyList<RuntimeView> views, Rect visible)
    {
        var (cx, cy) = (visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
        var area = Math.Max(visible.Width * visible.Height, 1);
        return views
            .Where(v => v.Props.Icon is not null &&
                        v.Bounds.X <= cx && cx <= v.Bounds.X + v.Bounds.Width &&
                        v.Bounds.Y <= cy && cy <= v.Bounds.Y + v.Bounds.Height &&
                        v.Bounds.Width * v.Bounds.Height <= area * 4)
            .MinBy(v => v.Bounds.Width * v.Bounds.Height);
    }

    static RuntimeView? FindRuntimeBox(IReadOnlyList<RuntimeView> views, Rect visible, double tolerance)
    {
        var tol = Math.Max(tolerance * 2, 4);
        var exact = views
            .Where(v => v.Props.Icon is null && v.Props.Text is null &&
                        Math.Abs(v.Bounds.X - visible.X) <= tol &&
                        Math.Abs(v.Bounds.Y - visible.Y) <= tol &&
                        Math.Abs(v.Bounds.Width - visible.Width) <= tol &&
                        Math.Abs(v.Bounds.Height - visible.Height) <= tol)
            .LastOrDefault(v => v.Props.Box is not null || v.Props.Shadow || v.Props.Alpha < 0.99);
        if (exact is not null) return exact;

        // Fallback by IoU when a shape stretched slightly differently than the design width.
        return views
            .Where(v => v.Props.Icon is null && v.Props.Text is null &&
                        (v.Props.Box is not null || v.Props.Shadow || v.Props.Alpha < 0.99) &&
                        ShapeProbe.Overlap(v.Bounds, visible) >= 0.7)
            .OrderByDescending(v => ShapeProbe.Overlap(v.Bounds, visible))
            .FirstOrDefault();
    }

    sealed record LayoutStats(int GapsChecked, int PaddingsChecked);

    static LayoutStats CompareLayouts(
        DesignFrame design,
        List<Measured> measured,
        ShapeComparison shapes,
        ScreenCapture capture,
        double scale,
        double dx,
        double dy,
        double extraWidth,
        double tolerance,
        UiFramework framework,
        List<ElementDiff> diffs,
        List<ElementResult> elements)
    {
        if (design.Layouts is not { Count: > 0 } layouts) return new LayoutStats(0, 0);

        // Map Figma NodeIds to their actual screen bounds (view bounds, not glyph ink).
        var matchedById = new Dictionary<string, Rect>();
        var designById = new Dictionary<string, Rect>();
        foreach (var m in measured)
            if (m.Design.NodeId is { } id) { matchedById[id] = m.Screen.Bounds; designById[id] = m.Design.Bounds; }
        foreach (var s in shapes.Checks)
            if (s.Design.NodeId is { } id && s.Actual is { } act && matchedById.TryAdd(id, act)) designById[id] = s.Design.Bounds;

        var runtimeViews = capture.RuntimeViews ?? [];
        var dummyElement = new DesignElement("", "FRAME", default, null, null, null, null, null, null);
        int gapsChecked = 0, paddingsChecked = 0;

        // The child's box in the app. A wrapper without its own match is measured by its matched descendants, but
        // only if they reach the edge the gap is measured from (the trailing edge of the first child, the leading
        // edge of the next): a matched title alone inside a card would put the card's edge in the wrong place.
        Rect? ResolveChildBox(DesignLayout layout, int index, bool leading)
        {
            if (layout.ChildNodeIds[index] is { } directId && matchedById.TryGetValue(directId, out var direct))
                return direct;

            if (layout.ChildDescendantIds is { Count: > 0 } descList && index < descList.Count)
            {
                Rect? union = null, designUnion = null;
                foreach (var descId in descList[index])
                {
                    if (!matchedById.TryGetValue(descId, out var b)) continue;
                    union = union is { } u ? Union(u, b) : b;
                    var db = designById[descId];
                    designUnion = designUnion is { } du ? Union(du, db) : db;
                }
                if (union is null || designUnion is not { } dUnion) return null;

                var child = layout.ChildBounds[index];
                var reaches = (layout.IsHorizontal, leading) switch
                {
                    (true, true) => Math.Abs(dUnion.X - child.X),
                    (true, false) => Math.Abs(dUnion.X + dUnion.Width - (child.X + child.Width)),
                    (false, true) => Math.Abs(dUnion.Y - child.Y),
                    (false, false) => Math.Abs(dUnion.Y + dUnion.Height - (child.Y + child.Height)),
                };
                return reaches <= 1 ? union : null;
            }
            return null;
        }

        foreach (var layout in layouts)
        {
            // Skip status bar / home indicator mockups drawn in the Figma frame.
            if (design.Bounds.Height > design.Bounds.Width &&
                (layout.Bounds.Y + layout.Bounds.Height <= StatusBarHeight || layout.Bounds.Y >= design.Bounds.Height - HomeIndicatorHeight))
                continue;

            var layoutLabel = string.IsNullOrWhiteSpace(layout.Name) ? "Auto Layout" : layout.Name;
            var expectedBox = layout.Bounds.Scale(scale).Offset(dx, dy);
            var layoutDiffs = new List<ElementDiff>();

            // 1. Container padding: use Frida View.getPadding*() when present, otherwise measure visual insets
            //    from the matched container view to its inner children (React Native / Yoga / Compose / iOS).
            if (!layout.Padding.IsZero && runtimeViews.Count > 0)
            {
                var containerRv = FindLayoutContainerView(runtimeViews, layout, expectedBox, extraWidth, tolerance, matchedById);
                if (containerRv is not null)
                {
                    var expPad = layout.Padding.Scale(scale);
                    // What the user sees comes first: Android code often spaces the content with the children's
                    // margins instead of the container's padding, and View.getPadding*() would then report 0.
                    var visualPadding = MeasureVisualPadding(containerRv, runtimeViews);
                    if (visualPadding is null && containerRv.Props.Padding is { } appPad)
                    {
                        paddingsChecked++;
                        if (Math.Abs(expPad.Left - appPad.Left) > tolerance ||
                            Math.Abs(expPad.Top - appPad.Top) > tolerance ||
                            Math.Abs(expPad.Right - appPad.Right) > tolerance ||
                            Math.Abs(expPad.Bottom - appPad.Bottom) > tolerance)
                        {
                            var diff = new ElementDiff(layoutLabel, "padding", FormatInsets(expPad), FormatInsets(appPad));
                            diff = diff with { Fix = FixSuggester.Suggest(framework, capture.Unit, dummyElement, diff) };
                            layoutDiffs.Add(diff);
                        }
                    }
                    else if (visualPadding is { } visualPad)
                    {
                        // Only compare sides where Figma declares explicit padding and layout alignment doesn't add centering slack.
                        var checkLeft = expPad.Left > 0 && !(layout.IsHorizontal && layout.PrimaryCenter) && !(!layout.IsHorizontal && layout.CounterCenter);
                        var checkRight = expPad.Right > 0 && !(layout.IsHorizontal && layout.PrimaryCenter) && !(!layout.IsHorizontal && layout.CounterCenter);
                        var checkTop = expPad.Top > 0 && !(!layout.IsHorizontal && layout.PrimaryCenter) && !(layout.IsHorizontal && layout.CounterCenter);
                        var checkBottom = expPad.Bottom > 0 && !(!layout.IsHorizontal && layout.PrimaryCenter) && !(layout.IsHorizontal && layout.CounterCenter);
                        // For single-child centered icon buttons (e.g. 48x48 with 20x20 icon), all 4 sides are meaningful!
                        if (layout.ChildBounds.Count == 1 && Math.Abs(expPad.Left - expPad.Right) <= 0.5 && Math.Abs(expPad.Top - expPad.Bottom) <= 0.5)
                            checkLeft = checkRight = checkTop = checkBottom = true;

                        if (checkLeft || checkRight || checkTop || checkBottom)
                        {
                            paddingsChecked++;
                            var differs =
                                (checkLeft && Math.Abs(expPad.Left - visualPad.Left) > tolerance) ||
                                (checkTop && Math.Abs(expPad.Top - visualPad.Top) > tolerance) ||
                                (checkRight && Math.Abs(expPad.Right - visualPad.Right) > tolerance) ||
                                (checkBottom && Math.Abs(expPad.Bottom - visualPad.Bottom) > tolerance);
                            if (differs)
                            {
                                var diff = new ElementDiff(layoutLabel, "padding", FormatInsets(expPad), FormatInsets(visualPad));
                                diff = diff with { Fix = FixSuggester.Suggest(framework, capture.Unit, dummyElement, diff) };
                                layoutDiffs.Add(diff);
                            }
                        }
                    }
                }
            }

            // 2. Item spacing (gap) between consecutive flow children (resolving nested Auto Layout wrappers).
            if (!layout.SpaceBetween && layout.ItemSpacing > 0 && layout.ChildNodeIds.Count >= 2)
            {
                var expGap = layout.ItemSpacing * scale;
                var reportedGaps = new List<double>();
                for (var i = 0; i < layout.ChildNodeIds.Count - 1; i++)
                {
                    if (ResolveChildBox(layout, i, leading: false) is not { } boxA || ResolveChildBox(layout, i + 1, leading: true) is not { } boxB)
                        continue;

                    var actualGap = layout.IsHorizontal
                        ? boxB.X - (boxA.X + boxA.Width)
                        : boxB.Y - (boxA.Y + boxA.Height);

                    // Ignore negative/nonsensical gaps caused by mismatched unrelated boxes.
                    if (actualGap < -tolerance * 4) continue;

                    gapsChecked++;
                    if (Math.Abs(expGap - actualGap) > tolerance)
                        reportedGaps.Add(actualGap);
                }

                if (reportedGaps.Count > 0)
                {
                    var actualGap = ElementMatcher.Median(reportedGaps);
                    var diff = new ElementDiff(layoutLabel, "gap", F(expGap), F(actualGap));
                    diff = diff with { Fix = FixSuggester.Suggest(framework, capture.Unit, dummyElement, diff) };
                    layoutDiffs.Add(diff);
                }
            }

            if (layoutDiffs.Count > 0)
            {
                diffs.AddRange(layoutDiffs);
                elements.Add(new ElementResult(layoutLabel, null, ElementStatus.Differs, expectedBox, expectedBox, layoutDiffs));
            }
        }

        return new LayoutStats(gapsChecked, paddingsChecked);
    }

    static RuntimeView? FindLayoutContainerView(
        IReadOnlyList<RuntimeView> runtimeViews,
        DesignLayout layout,
        Rect expectedBox,
        double extraWidth,
        double tolerance,
        Dictionary<string, Rect> matchedById)
    {
        // If this container itself was matched as a shape/element, use its actual bounds!
        if (layout.NodeId is { } nid && matchedById.TryGetValue(nid, out var matchedBox))
        {
            var byMatched = runtimeViews
                .Where(v => v.Props.Text is null && v.Props.Icon is null && ShapeProbe.Overlap(v.Bounds, matchedBox) >= 0.7)
                .OrderByDescending(v => v.Props.Padding is not null ? 1 : 0)
                .ThenByDescending(v => ShapeProbe.Overlap(v.Bounds, matchedBox))
                .FirstOrDefault();
            if (byMatched is not null) return byMatched;
        }

        var tol = Math.Max(tolerance * 2, 4);
        var candidates = HorizontalOptions(expectedBox, extraWidth);
        return runtimeViews
            .Where(v => v.Props.Text is null && v.Props.Icon is null && candidates.Any(c =>
                (Math.Abs(v.Bounds.X - c.X) <= tol && Math.Abs(v.Bounds.Y - c.Y) <= tol &&
                 Math.Abs(v.Bounds.Width - c.Width) <= tol && Math.Abs(v.Bounds.Height - c.Height) <= tol) ||
                ShapeProbe.Overlap(v.Bounds, c) >= 0.75))
            .OrderByDescending(v => v.Props.Padding is not null ? 1 : 0)
            .LastOrDefault();
    }

    static Insets? MeasureVisualPadding(RuntimeView container, IReadOnlyList<RuntimeView> allViews)
    {
        var cb = container.Bounds;
        var inner = allViews.Where(v =>
            !ReferenceEquals(v, container) &&
            (v.Props.Text is not null || v.Props.Icon is not null || v.Props.Box is not null) &&
            v.Bounds.Width < cb.Width - 0.5 && v.Bounds.Height <= cb.Height + 0.5 &&
            v.Bounds.X >= cb.X - 1 && v.Bounds.Y >= cb.Y - 1 &&
            v.Bounds.X + v.Bounds.Width <= cb.X + cb.Width + 1 &&
            v.Bounds.Y + v.Bounds.Height <= cb.Y + cb.Height + 1).ToList();
        if (inner.Count == 0) return null;

        var left = Math.Max(0, Math.Round(inner.Min(v => v.Bounds.X) - cb.X, 1));
        var top = Math.Max(0, Math.Round(inner.Min(v => v.Bounds.Y) - cb.Y, 1));
        var right = Math.Max(0, Math.Round(cb.X + cb.Width - inner.Max(v => v.Bounds.X + v.Bounds.Width), 1));
        var bottom = Math.Max(0, Math.Round(cb.Y + cb.Height - inner.Max(v => v.Bounds.Y + v.Bounds.Height), 1));
        return new Insets(left, top, right, bottom);
    }

    static Rect Union(Rect a, Rect b)
    {
        var x1 = Math.Min(a.X, b.X);
        var y1 = Math.Min(a.Y, b.Y);
        var x2 = Math.Max(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    static string FormatInsets(Insets i) =>
        i.Left == i.Top && i.Top == i.Right && i.Right == i.Bottom ? F(i.Left) : $"{F(i.Left)}, {F(i.Top)}, {F(i.Right)}, {F(i.Bottom)}";

    static string F2(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Fondo de la pantalla: el color más frecuente en el PNG del diseño y en la captura.</summary>
    static (Rgb Design, Rgb App)? ScreenBackground(DesignFrame design, ShapeContext c)
    {
        if (design.Background is null) return null;
        var designColor = SampleDominantColor(c.Design.Pixels, c.Design.Width, c.Design.Height,
            new Rect(0, 0, c.Design.Width, c.Design.Height), inset: 0);
        var appColor = SampleDominantColor(c.Screen.Pixels, c.Screen.Width, c.Screen.Height,
            new Rect(0, 0, c.Screen.Width, c.Screen.Height), inset: 0);
        return designColor is { } d && appColor is { } a ? (d, a) : null;
    }

    /// <summary>
    /// Una capa entera dentro de la barra de estado o del indicador de inicio que el diseño dibuja como
    /// maqueta (la batería, la señal, la barrita de abajo): en la app los pone el sistema.
    /// </summary>
    static bool IsDeviceChrome(DesignElement shape, DesignFrame design) =>
        design.Bounds.Height > design.Bounds.Width &&
        (shape.Bounds.Y + shape.Bounds.Height <= StatusBarHeight || shape.Bounds.Y >= design.Bounds.Height - HomeIndicatorHeight);

    /// <summary>
    /// Nombre para el reporte. Los nombres automáticos ("Frame", "Auto Layout Horizontal") y los de variantes
    /// ("Type=Primary, State=Disabled") no dicen qué es, así que se usa el tipo; si tiene un texto adentro
    /// (el de un botón o un campo), se agrega.
    /// </summary>
    static string ShapeLabel(DesignElement shape, IReadOnlyList<DesignElement> elements)
    {
        var b = shape.Bounds;
        var generic = shape.Name.Contains('=') || GenericShapeNameRegex().IsMatch(shape.Name.Trim());
        var name = !generic ? shape.Name : shape.Shape switch
        {
            ShapeKind.Icon => "Icon",
            ShapeKind.Image => "Image",
            _ when Math.Min(b.Width, b.Height) <= 2 => "Line",
            _ => "Shape",
        };

        var text = elements
            .Where(e => e.Text is not null && e.Bounds.X >= b.X && e.Bounds.Y >= b.Y &&
                        e.Bounds.X + e.Bounds.Width <= b.X + b.Width && e.Bounds.Y + e.Bounds.Height <= b.Y + b.Height)
            .OrderBy(e => e.Bounds.Y).ThenBy(e => e.Bounds.X)
            .Select(e => e.Text!.ReplaceLineEndings(" ").Trim())
            .FirstOrDefault(t => t.Length > 0);
        if (text is null) return name;
        if (text.Length > 40) text = text[..40] + "…";
        return $"{name} \"{text}\"";
    }

    [GeneratedRegex(@"^(Frame|Group|Rectangle|Ellipse|Vector|Line|Polygon|Star|Union|Subtract|Intersect|Exclude|Image|Component|Instance|Auto layout( (horizontal|vertical))?|Content|Container)( \d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericShapeNameRegex();
}
