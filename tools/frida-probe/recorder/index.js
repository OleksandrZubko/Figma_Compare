// Frida agent for Driftcheck's monitor: while loaded, it reports every HTTP call made through OkHttp (what React
// Native's fetch and most Android apps use) and every event sent to a native analytics SDK, as `send()` messages.
// Built with `npm run build` into ../_recorder.js, which record.py loads.
import Java from "frida-java-bridge";

const MAX_BODY = 64 * 1024;

function tryUse(name) {
  try {
    return Java.use(name);
  } catch (e) {
    return null;
  }
}

function str(v) {
  try {
    return v === null || v === undefined ? null : String(v.toString());
  } catch (e) {
    return null;
  }
}

function isTextual(contentType) {
  return contentType !== null && /json|text|xml|x-www-form-urlencoded|graphql/i.test(contentType);
}

function hookOkHttp() {
  // OkHttp 4 (Kotlin) and OkHttp 3: the call runs every interceptor and returns the final response here.
  const variants = [
    ["okhttp3.internal.connection.RealCall", "getResponseWithInterceptorChain$okhttp"],
    ["okhttp3.RealCall", "getResponseWithInterceptorChain"],
  ];
  const Buffer = tryUse("okio.Buffer");
  let hooked = null;

  for (const [cls, method] of variants) {
    const RealCall = tryUse(cls);
    if (RealCall === null || RealCall[method] === undefined) continue;

    RealCall[method].implementation = function () {
      const started = Date.now();
      const event = { type: "http", started: started };
      let request = null;
      try {
        request = this.request();
        event.method = str(request.method());
        event.url = str(request.url());
        const body = request.body();
        if (body !== null) {
          const contentType = str(body.contentType());
          event.requestType = contentType;
          event.requestSize = body.contentLength();
          let oneShot = false;
          try { oneShot = body.isOneShot(); } catch (e) {}
          const encoding = str(request.header("Content-Encoding"));
          // Reading a one-shot body would consume it before the real request is sent.
          if (Buffer !== null && !oneShot && encoding === null && isTextual(contentType) &&
              event.requestSize >= 0 && event.requestSize <= MAX_BODY) {
            const buffer = Buffer.$new();
            body.writeTo(buffer);
            event.requestBody = str(buffer.readUtf8());
          }
        }
      } catch (e) {
        event.note = "request: " + e;
      }

      try {
        const response = this[method]();
        event.ms = Date.now() - started;
        try {
          event.status = response.code();
          event.protocol = str(response.protocol());
          const contentType = str(response.header("Content-Type"));
          event.responseType = contentType;
          if (isTextual(contentType)) {
            const peeked = response.peekBody(MAX_BODY);
            event.responseSize = peeked.contentLength();
            event.responseBody = str(peeked.string());
          }
        } catch (e) {
          event.note = (event.note ? event.note + "; " : "") + "response: " + e;
        }
        send(event);
        return response;
      } catch (e) {
        event.ms = Date.now() - started;
        event.error = str(e);
        send(event);
        throw e;
      }
    };
    hooked = cls;
  }
  return hooked;
}

// The parameters of an analytics call: a Bundle, a Map or a JSONObject, printed as text.
function paramsText(value) {
  if (value === null || value === undefined) return null;
  try {
    // An argument typed as an interface (kotlin Map) has no toString of its own on the wrapper.
    return String(Java.cast(value, Java.use("java.lang.Object")).toString());
  } catch (e) {
    return null;
  }
}

function hookAnalytics() {
  // [class, method, index of the event-name argument] — every overload of the method is hooked.
  const targets = [
    ["com.google.firebase.analytics.FirebaseAnalytics", "logEvent", "firebase"],
    ["com.amplitude.core.Amplitude", "track", "amplitude"],
    ["com.amplitude.api.AmplitudeClient", "logEvent", "amplitude"],
    ["com.segment.analytics.Analytics", "track", "segment"],
    ["com.segment.analytics.kotlin.core.Analytics", "track", "segment"],
    ["com.mixpanel.android.mpmetrics.MixpanelAPI", "track", "mixpanel"],
    ["com.appsflyer.AppsFlyerLib", "logEvent", "appsflyer"],
    ["com.posthog.PostHog", "capture", "posthog"],
    ["com.facebook.appevents.AppEventsLogger", "logEvent", "facebook"],
  ];
  const hooked = [];
  for (const [cls, method, sdk] of targets) {
    const C = tryUse(cls);
    if (C === null || C[method] === undefined) continue;
    for (const overload of C[method].overloads) {
      overload.implementation = function () {
        const args = Array.prototype.slice.call(arguments);
        // The event name is the first String argument; the parameters the first Bundle/Map/JSONObject after it.
        let name = null, params = null;
        for (const a of args) {
          if (a === null || a === undefined) continue;
          if (name === null && typeof a === "string") { name = a; continue; }
          if (name !== null && params === null && typeof a === "object") {
            let cn = "";
            try { cn = String(Java.cast(a, Java.use("java.lang.Object")).getClass().getName()); } catch (e) {}
            if (/Bundle|Map|JSONObject|Properties/.test(cn)) params = paramsText(a);
          }
        }
        send({ type: "analytics", sdk: sdk, name: name, params: params, at: Date.now() });
        return overload.apply(this, arguments);
      };
    }
    hooked.push(sdk);
  }
  return hooked;
}

rpc.exports = {
  start() {
    return new Promise((resolve) => {
      Java.perform(() => {
        const result = {};
        try { result.http = hookOkHttp(); } catch (e) { result.httpError = String(e); }
        try { result.analytics = hookAnalytics(); } catch (e) { result.analyticsError = String(e); }
        resolve(JSON.stringify(result));
      });
    });
  },
};
