// Frida agent for Driftcheck: walks the View tree of every window of the app and returns the properties
// that uiautomator doesn't expose (text size, typeface, colors, corner radius, stroke, padding, alpha…).
// Built with `npm run build` into _agent.js, which probe.py loads. Android Views and React Native (not Compose).
import Java from "frida-java-bridge";

const MAX_TEXT = 200;
const MASK_ID = 0x0102002e; // android.R.id.mask: the ripple's mask layer, never drawn.

function tryUse(name) {
  try {
    return Java.use(name);
  } catch (e) {
    return null;
  }
}

function collect() {
  const C = {
    WindowManagerGlobal: Java.use("android.view.WindowManagerGlobal"),
    ViewGroup: Java.use("android.view.ViewGroup"),
    TextView: Java.use("android.widget.TextView"),
    ImageView: Java.use("android.widget.ImageView"),
    Rect: Java.use("android.graphics.Rect"),
    ColorDrawable: Java.use("android.graphics.drawable.ColorDrawable"),
    GradientDrawable: Java.use("android.graphics.drawable.GradientDrawable"),
    LayerDrawable: Java.use("android.graphics.drawable.LayerDrawable"),
    DrawableContainer: Java.use("android.graphics.drawable.DrawableContainer"),
    DrawableWrapper: tryUse("android.graphics.drawable.DrawableWrapper"),
    InsetDrawable: Java.use("android.graphics.drawable.InsetDrawable"),
    ShapeDrawable: Java.use("android.graphics.drawable.ShapeDrawable"),
    BitmapDrawable: Java.use("android.graphics.drawable.BitmapDrawable"),
    VectorDrawable: tryUse("android.graphics.drawable.VectorDrawable"),
    MaterialShapeDrawable: tryUse("com.google.android.material.shape.MaterialShapeDrawable"),
    AppCompatWrapper: tryUse("androidx.appcompat.graphics.drawable.DrawableWrapperCompat"),
    CardView: tryUse("androidx.cardview.widget.CardView"),
    MaterialCardView: tryUse("com.google.android.material.card.MaterialCardView"),
    MaterialButton: tryUse("com.google.android.material.button.MaterialButton"),
    Spanned: Java.use("android.text.Spanned"),
    JObject: Java.use("java.lang.Object"),
    AbsoluteSizeSpan: Java.use("android.text.style.AbsoluteSizeSpan"),
    ForegroundColorSpan: Java.use("android.text.style.ForegroundColorSpan"),
    StyleSpan: Java.use("android.text.style.StyleSpan"),
    TypefaceSpan: Java.use("android.text.style.TypefaceSpan"),
    // React Native: text styles are spans on the text, backgrounds are its own drawables.
    RnStyleSpan: tryUse("com.facebook.react.views.text.internal.span.CustomStyleSpan"),
    RnLetterSpacingSpan: tryUse("com.facebook.react.views.text.internal.span.CustomLetterSpacingSpan"),
    RnLineHeightSpan: tryUse("com.facebook.react.views.text.internal.span.CustomLineHeightSpan"),
    RnComposite: tryUse("com.facebook.react.uimanager.drawable.CompositeBackgroundDrawable"),
    RnBackground: tryUse("com.facebook.react.uimanager.drawable.BackgroundDrawable"),
    RnBorder: tryUse("com.facebook.react.uimanager.drawable.BorderDrawable"),
    // react-native-svg: an <Svg> is a view whose children (groups, paths) hold fill and stroke.
    SvgView: tryUse("com.horcrux.svg.SvgView"),
    SvgRenderable: tryUse("com.horcrux.svg.RenderableView"),
    SvgGroup: tryUse("com.horcrux.svg.GroupView"),
    ReadableArray: tryUse("com.facebook.react.bridge.ReadableArray"),
  };

  const is = (cls, obj) => cls !== null && obj !== null && cls.class.isInstance(obj);
  const hex = (argb) => {
    const v = argb >>> 0;
    return "#" + ("0000000" + v.toString(16)).slice(-8).toUpperCase(); // #AARRGGBB
  };
  const stateColor = (csl, state) => {
    if (csl === null) return null;
    try {
      return hex(csl.getColorForState(state, csl.getDefaultColor()));
    } catch (e) {
      return null;
    }
  };

  // Kotlin data classes print as "ComputedBorderRadius(topLeft=CornerRadii(horizontal=24.0, vertical=24.0), …)":
  // the first number after the key is the value.
  const firstNumberAfter = (text, key) => {
    const m = new RegExp(key + "[^0-9-]*(-?[0-9.]+)").exec(text);
    return m === null ? null : parseFloat(m[1]);
  };
  // React Native keeps the computed corner radii in dp; everything else in this dump is px.
  const rnRadius = (r) => {
    const dp = firstNumberAfter(r.toString(), "topLeft");
    return dp === null ? null : dp * metrics.density;
  };

  function rnBackgroundInfo(d) {
    const b = Java.cast(d, C.RnBackground);
    const info = { kind: "rn", color: hex(b.getBackgroundColor()) };
    try {
      const r = b.computedBorderRadius.value;
      if (r !== null) info.radius = rnRadius(r);
    } catch (e) {}
    return info;
  }

  function rnBorderInfo(d, info) {
    const b = Java.cast(d, C.RnBorder);
    try {
      const w = b.getFullBorderWidth();
      if (w > 0) {
        info.strokeWidth = w;
        const colors = b.computedBorderColors.value;
        if (colors !== null) {
          const c = firstNumberAfter(colors.toString(), "left");
          if (c !== null) info.strokeColor = hex(c);
        }
      }
    } catch (e) {}
    try {
      const r = b.computedBorderRadius.value;
      if (r !== null && info.radius === undefined) info.radius = rnRadius(r);
    } catch (e) {}
    return info;
  }

  function drawableInfo(d, state, depth) {
    if (d === null || depth > 6) return null;
    try {
      if (is(C.RnComposite, d)) {
        const comp = Java.cast(d, C.RnComposite);
        const bg = comp.getBackground();
        const info = bg !== null ? rnBackgroundInfo(bg) : { kind: "rn" };
        try {
          const shadows = comp.getOuterShadows();
          if (shadows !== null && shadows.size() > 0) info.shadow = true; // boxShadow
        } catch (e) {}
        const border = comp.getBorder();
        return border !== null ? rnBorderInfo(border, info) : info;
      }
      if (is(C.RnBackground, d)) return rnBackgroundInfo(d);
      if (is(C.RnBorder, d)) return rnBorderInfo(d, { kind: "rn" });
      if (is(C.ColorDrawable, d)) {
        return { kind: "color", color: hex(Java.cast(d, C.ColorDrawable).getColor()) };
      }
      if (is(C.MaterialShapeDrawable, d)) {
        const m = Java.cast(d, C.MaterialShapeDrawable);
        const info = { kind: "material", color: stateColor(m.getFillColor(), state) };
        try { info.radius = m.getTopLeftCornerResolvedSize(); } catch (e) {}
        try {
          const sw = m.getStrokeWidth();
          if (sw > 0) { info.strokeWidth = sw; info.strokeColor = stateColor(m.getStrokeColor(), state); }
        } catch (e) {}
        return info;
      }
      if (is(C.GradientDrawable, d)) {
        const g = Java.cast(d, C.GradientDrawable);
        const info = { kind: "gradient" };
        try { info.color = stateColor(g.getColor(), state); } catch (e) {}
        try {
          const radii = g.getCornerRadii();
          if (radii !== null) info.radii = Array.from(radii);
        } catch (e) {}
        try { info.radius = g.getCornerRadius(); } catch (e) {}
        try {
          const colors = g.getColors();
          if (colors !== null) info.gradient = Array.from(colors).map(hex);
        } catch (e) {}
        try {
          // No public getter for the stroke: it lives in the drawable's stroke paint.
          const paint = g.mStrokePaint.value;
          if (paint !== null && paint.getStrokeWidth() > 0) {
            info.strokeWidth = paint.getStrokeWidth();
            info.strokeColor = hex(paint.getColor());
          }
        } catch (e) {}
        return info;
      }
      if (is(C.LayerDrawable, d)) {
        // RippleDrawable is a LayerDrawable: the content is the first layer that isn't the mask.
        const l = Java.cast(d, C.LayerDrawable);
        for (let i = 0; i < l.getNumberOfLayers(); i++) {
          if (l.getId(i) === MASK_ID) continue;
          const info = drawableInfo(l.getDrawable(i), state, depth + 1);
          if (info !== null) return info;
        }
        return null;
      }
      if (is(C.DrawableContainer, d)) {
        return drawableInfo(Java.cast(d, C.DrawableContainer).getCurrent(), state, depth + 1);
      }
      if (is(C.InsetDrawable, d) || is(C.DrawableWrapper, d)) {
        const w = Java.cast(d, C.DrawableWrapper !== null ? C.DrawableWrapper : C.InsetDrawable);
        return drawableInfo(w.getDrawable(), state, depth + 1);
      }
      if (is(C.AppCompatWrapper, d)) {
        return drawableInfo(Java.cast(d, C.AppCompatWrapper).getDrawable(), state, depth + 1);
      }
      if (is(C.BitmapDrawable, d)) return { kind: "bitmap" };
      if (is(C.VectorDrawable, d)) return { kind: "vector" };
      return { kind: d.$className };
    } catch (e) {
      return { kind: "error", error: String(e) };
    }
  }

  function textInfo(v) {
    const t = Java.cast(v, C.TextView);
    const info = {
      density: metrics.density,
      text: String(t.getText()).slice(0, MAX_TEXT),
      size: t.getTextSize(),
      color: hex(t.getCurrentTextColor()),
      letterSpacing: t.getLetterSpacing(),
      lineHeight: t.getLineHeight(),
      gravity: t.getGravity(),
      maxLines: t.getMaxLines(),
    };
    try {
      const layout = t.getLayout();
      if (layout !== null) {
        const lineCount = layout.getLineCount();
        info.lineCount = lineCount;
        let ellipsized = false;
        let maxLineW = 0;
        for (let i = 0; i < lineCount; i++) {
          if (layout.getEllipsisCount(i) > 0) ellipsized = true;
          const lw = layout.getLineWidth(i);
          if (lw > maxLineW) maxLineW = lw;
        }
        info.ellipsized = ellipsized;
        const availW = t.getWidth() - t.getTotalPaddingLeft() - t.getTotalPaddingRight();
        const availH = t.getHeight() - t.getTotalPaddingTop() - t.getTotalPaddingBottom();
        if ((availW > 0 && maxLineW > availW + 2) || (availH > 0 && layout.getHeight() > availH + 2)) {
          info.clipped = true;
        }
      }
    } catch (e) {}
    const tf = t.getTypeface();
    if (tf !== null) {
      try { info.weight = tf.getWeight(); } catch (e) {}
      info.italic = tf.isItalic();
      try { info.family = tf.getSystemFontFamilyName(); } catch (e) {}
      if (info.family === null || info.family === undefined) {
        try { info.family = tf.mSystemFontFamilyName.value; } catch (e) {}
      }
    }
    applySpans(t.getText(), info);
    delete info.density;
    return info;
  }

  // Spans that cover the start of the text override the TextView's own style: that's how React Native (and
  // any Views app using SpannableString) sets size, color, weight and font.
  function applySpans(text, info) {
    if (!is(C.Spanned, text)) return;
    const s = Java.cast(text, C.Spanned);
    const spans = s.getSpans(0, String(text).length, C.JObject.class);
    if (spans === null) return;
    for (let i = 0; i < spans.length; i++) {
      const span = spans[i];
      if (s.getSpanStart(span) !== 0) continue;
      try {
        if (is(C.AbsoluteSizeSpan, span)) {
          const a = Java.cast(span, C.AbsoluteSizeSpan);
          info.size = a.getDip() ? a.getSize() * info.density : a.getSize();
        } else if (is(C.ForegroundColorSpan, span)) {
          info.color = hex(Java.cast(span, C.ForegroundColorSpan).getForegroundColor());
        } else if (is(C.RnStyleSpan, span)) {
          const r = Java.cast(span, C.RnStyleSpan);
          if (r.getWeight() > 0) info.weight = r.getWeight();
          if (r.getStyle() === 2) info.italic = true;
          if (r.getFontFamily() !== null) info.family = r.getFontFamily();
        } else if (is(C.StyleSpan, span)) {
          const style = Java.cast(span, C.StyleSpan).getStyle();
          if (style & 1) info.weight = Math.max(info.weight || 400, 700);
          if (style & 2) info.italic = true;
        } else if (is(C.TypefaceSpan, span)) {
          const family = Java.cast(span, C.TypefaceSpan).getFamily();
          if (family !== null) info.family = family;
        } else if (is(C.RnLetterSpacingSpan, span)) {
          info.letterSpacingPx = Java.cast(span, C.RnLetterSpacingSpan).getSpacing();
        } else if (is(C.RnLineHeightSpan, span)) {
          info.lineHeight = Java.cast(span, C.RnLineHeightSpan).getLineHeight();
        }
      } catch (e) {}
    }
  }

  function cardInfo(v, state) {
    const card = Java.cast(v, C.CardView);
    const info = { kind: "card", radius: card.getRadius() };
    try { info.color = stateColor(card.getCardBackgroundColor(), state); } catch (e) {}
    if (is(C.MaterialCardView, v)) {
      const m = Java.cast(v, C.MaterialCardView);
      try {
        if (m.getStrokeWidth() > 0) { info.strokeWidth = m.getStrokeWidth(); info.strokeColor = hex(m.getStrokeColor()); }
      } catch (e) {}
    }
    return info;
  }

  // A react-native-svg brush is [type, value]: 0 = color (ARGB as a number), 1 = gradient or pattern,
  // 2 = currentColor; null, empty or anything else draws nothing.
  function svgBrush(array, currentColor) {
    if (array === null) return null;
    const a = Java.cast(array, C.ReadableArray);
    if (a.size() === 0) return null;
    const type = a.getDouble(0);
    if (type === 0 && a.size() > 1) return a.getDouble(1);
    if (type === 2) return currentColor;
    if (type === 1) return "gradient";
    return null;
  }

  const withOpacity = (argb, opacity) => {
    const alpha = Math.round(((argb >>> 24) & 0xff) * opacity);
    return ((alpha << 24) | (argb & 0xffffff)) >>> 0;
  };

  // Colors an <Svg> paints: the fill and stroke of every shape, with group inheritance and opacity applied.
  function svgInfo(svgView) {
    const fills = new Set(), strokes = new Set();
    let gradient = false;

    function visit(group, inheritedFill, inheritedStroke, inheritedOpacity, depth) {
      if (depth > 30) return;
      const g = Java.cast(group, C.ViewGroup);
      for (let i = 0; i < g.getChildCount(); i++) {
        const child = g.getChildAt(i);
        // <Defs>, <ClipPath>, <Mask>, <Pattern>, <Symbol> and <Marker> hold shapes that aren't painted as such.
        if (/(Defs|Definition|ClipPath|Mask|Pattern|Symbol|Marker)View$/.test(child.$className)) continue;
        if (!is(C.SvgRenderable, child)) continue;
        const r = Java.cast(child, C.SvgRenderable);
        let opacity = inheritedOpacity;
        try { opacity *= r.mOpacity.value; } catch (e) {}
        let current = 0;
        try { current = r.getCurrentColor(); } catch (e) {}
        // A shape keeps its default (black fill, no stroke) only if no group above sets one: the props the
        // element set itself are in mPropList, everything else is inherited (fill="none" is set to null).
        let props = null;
        try { props = r.mPropList.value; } catch (e) {}
        const own = (name) => props !== null && props.contains(name);
        // { brush, set }: set = some element in the chain set it, rather than the SVG default.
        const resolve = (name, value, inherited) =>
          own(name) ? { brush: svgBrush(value, current), set: true }
            : inherited !== undefined ? inherited : { brush: svgBrush(value, current), set: false };
        const fillRef = resolve("fill", r.fill.value, inheritedFill);
        const strokeRef = resolve("stroke", r.stroke.value, inheritedStroke);

        if (is(C.SvgGroup, child)) {
          visit(child, fillRef, strokeRef, opacity, depth + 1);
          continue;
        }
        if (opacity <= 0) continue;
        const stroke = strokeRef.brush;
        // The default black fill of a stroked line nobody gave a fill to covers no area: it isn't painted.
        const fill = !fillRef.set && stroke !== null ? null : fillRef.brush;
        if (typeof fill === "number") {
          let fillOpacity = 1;
          try { fillOpacity = r.fillOpacity.value; } catch (e) {}
          if (fillOpacity > 0) fills.add(hex(withOpacity(fill, fillOpacity * opacity)));
        } else if (fill === "gradient") gradient = true;
        if (typeof stroke === "number") {
          let width = 1, strokeOpacity = 1;
          try { width = parseFloat(String(r.strokeWidth.value.value.value)); } catch (e) {}
          try { strokeOpacity = r.strokeOpacity.value; } catch (e) {}
          if (width > 0 && strokeOpacity > 0) strokes.add(hex(withOpacity(stroke, strokeOpacity * opacity)));
        } else if (stroke === "gradient") gradient = true;
      }
    }

    visit(svgView, undefined, undefined, 1, 0);
    const info = { fills: Array.from(fills), strokes: Array.from(strokes), gradient: gradient };
    try {
      const s = Java.cast(svgView, C.SvgView);
      info.viewBox = [s.mVbWidth.value, s.mVbHeight.value];
    } catch (e) {}
    return info;
  }

  function resourceName(v, res) {
    const id = v.getId();
    if (id === -1 || id === 0) return null;
    try {
      return res.getResourceName(id);
    } catch (e) {
      return null; // Generated ids (View.generateViewId) have no name.
    }
  }

  const views = [];
  let metrics = null;
  const rect = C.Rect.$new();
  const loc = Java.array("int", [0, 0]);

  function walk(v, parentAlpha, depth) {
    if (v.getVisibility() !== 0 || depth > 80) return; // 0 = VISIBLE
    const alpha = parentAlpha * v.getAlpha();
    if (alpha <= 0) return;
    if (!v.getGlobalVisibleRect(rect)) return; // Off screen or clipped away.

    const res = v.getResources();
    if (metrics === null) {
      metrics = { density: res.getDisplayMetrics().density.value, fontScale: res.getConfiguration().fontScale.value };
    }

    // Visible rect is relative to the window; uiautomator reports screen coordinates.
    v.getLocationOnScreen(loc);
    const sx = loc[0], sy = loc[1];
    v.getLocationInWindow(loc);
    const ox = sx - loc[0], oy = sy - loc[1];

    const state = v.getDrawableState();
    const entry = {
      id: resourceName(v, res),
      cls: v.$className,
      bounds: [rect.left.value + ox, rect.top.value + oy, rect.right.value + ox, rect.bottom.value + oy],
      alpha: alpha,
      elevation: v.getElevation(),
      padding: [v.getPaddingLeft(), v.getPaddingTop(), v.getPaddingRight(), v.getPaddingBottom()],
    };

    if (is(C.CardView, v)) entry.background = cardInfo(v, state);
    else entry.background = drawableInfo(v.getBackground(), state, 0);

    if (is(C.MaterialButton, v)) {
      const b = Java.cast(v, C.MaterialButton);
      const bg = entry.background || { kind: "material" };
      try { bg.radius = b.getCornerRadius(); } catch (e) {}
      try {
        if (b.getStrokeWidth() > 0) { bg.strokeWidth = b.getStrokeWidth(); bg.strokeColor = stateColor(b.getStrokeColor(), state); }
      } catch (e) {}
      try {
        const tint = b.getBackgroundTintList();
        if (tint !== null) bg.color = stateColor(tint, state);
      } catch (e) {}
      entry.background = bg;
    }

    if (is(C.TextView, v)) entry.text = textInfo(v);
    if (is(C.ImageView, v)) {
      const img = Java.cast(v, C.ImageView);
      entry.image = { drawable: drawableInfo(img.getDrawable(), state, 0) };
      try { entry.image.tint = stateColor(img.getImageTintList(), state); } catch (e) {}
    }

    if (is(C.SvgView, v)) {
      try { entry.svg = svgInfo(v); } catch (e) { entry.svg = { error: String(e) }; }
      views.push(entry);
      return; // Its children are the drawing's shapes, already summed up in entry.svg.
    }

    views.push(entry);

    if (is(C.ViewGroup, v)) {
      const g = Java.cast(v, C.ViewGroup);
      for (let i = 0; i < g.getChildCount(); i++) walk(g.getChildAt(i), alpha, depth + 1);
    }
  }

  const roots = C.WindowManagerGlobal.getInstance().mViews.value;
  for (let i = 0; i < roots.size(); i++) {
    walk(Java.cast(roots.get(i), Java.use("android.view.View")), 1, 0);
  }
  return { metrics: metrics, views: views };
}

rpc.exports = {
  dump() {
    return new Promise((resolve) => {
      Java.perform(() => {
        Java.scheduleOnMainThread(() => {
          try {
            resolve(JSON.stringify(collect()));
          } catch (e) {
            resolve(JSON.stringify({ error: String(e && e.stack ? e.stack : e) }));
          }
        });
      });
    });
  },
};
