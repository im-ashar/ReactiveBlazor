// ReactiveBlazor client runtime.
// Version is injected by the server at render time via the
// <meta name="reactive-version"> tag emitted by <ReactiveScripts />.
// Handles: dispatch, request queuing, DOM morphing (Idiomorph), busy state,
//          error handling, debounce, throttle, redirect, retry, and generic data-on-* events.
//
// Queue behavior:
//   By default, rapid dispatches for the same component keep only the latest pending
//   request (ideal for input/change events where intermediate values are superseded).
//   For non-idempotent actions (e.g. "delete item"), add data-queue="all" to the trigger
//   element so every request is processed sequentially.
(function () {
  "use strict";

  // ---- Configuration (read from meta tags emitted by ReactiveScripts) ----

  function endpoint() {
    var m = document.querySelector('meta[name="reactive-endpoint"]');
    return m ? m.getAttribute("content") : "/_reactive/dispatch";
  }

  function csrfToken() {
    var m = document.querySelector('meta[name="reactive-csrf"]');
    return m ? m.getAttribute("content") : "";
  }

  function libraryVersion() {
    var m = document.querySelector('meta[name="reactive-version"]');
    return m ? m.getAttribute("content") : "";
  }

  function reloadOnUnauthorized() {
    var m = document.querySelector('meta[name="reactive-reload-on-401"]');
    // Default to true when the meta tag is absent (matches ReactiveOptions default).
    return !m || m.getAttribute("content") !== "false";
  }

  // Set once a 401 triggers a full-page reload, so no further dispatches/polls fire in the
  // brief window before navigation completes.
  var reloading = false;

  // ---- Per-component request queues ----
  // Key = component element id.
  var queues = {};

  // ---- Per-component dispatch sequence ----
  // Key = component element id. Monotonic counter, bumped on every dispatch start. Used to drop
  // a morph whose response arrives after a newer dispatch for the same component has been issued,
  // so a slow round-trip can't overwrite the DOM with stale state (e.g. an input the user has
  // since typed more into). A counter is immune to clock skew, unlike a timestamp.
  var dispatchSeq = {};

  // ---- Polling ----
  // Key = component element id. One poller per component (attributes live on the root div).
  var MIN_POLL_INTERVAL = 250; // ms floor to prevent runaway/abusive timers
  var pollers = {};            // id -> { timer, interval }

  function rootOf(el) {
    return el.closest("[data-component]");
  }

  function collectBindings(root) {
    var bindings = {};
    root.querySelectorAll("[data-bind]").forEach(function (el) {
      var name = el.getAttribute("data-bind");
      if (el.type === "checkbox") {
        bindings[name] = el.checked;
      } else if (el.type === "radio") {
        if (el.checked) bindings[name] = el.value;
      } else {
        bindings[name] = el.value;
      }
    });
    return bindings;
  }

  function parseArgs(el) {
    var raw = el.getAttribute("data-args");
    if (!raw) return [];
    try {
      var parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed : [parsed];
    } catch (e) {
      return [raw];
    }
  }

  // ---- Request queue ----
  // Two modes:
  //   "latest" (default): only the most recent pending request is kept.
  //   "all": every request is queued and processed in order.

  function enqueue(rootId, fn, mode) {
    if (!queues[rootId]) {
      queues[rootId] = { running: false, pending: [] };
    }
    var q = queues[rootId];

    if (mode === "all") {
      q.pending.push(fn);
    } else {
      // "latest" — supersede any pending request.
      q.pending = [fn];
    }

    processQueue(rootId);
  }

  function processQueue(rootId) {
    var q = queues[rootId];
    if (!q || q.running || q.pending.length === 0) return;
    q.running = true;
    var fn = q.pending.shift();
    fn().finally(function () {
      q.running = false;
      processQueue(rootId);
    });
  }

  // ---- Core dispatch ----

  function dispatch(root, action, args, queueMode) {
    var rootId = root.id;
    enqueue(rootId, function () {
      return doDispatch(root, action, args);
    }, queueMode);
  }

  async function doDispatch(root, action, args) {
    if (reloading) return; // a 401 reload is in flight; don't start new requests
    // Re-query root by id in case it was replaced by a previous morph.
    root = document.getElementById(root.id);
    if (!root) return;

    var rootId = root.id;
    // Claim the latest sequence for this component. Any response that completes after a newer
    // dispatch starts will see seq !== dispatchSeq[rootId] and bail before morphing.
    var seq = (dispatchSeq[rootId] = (dispatchSeq[rootId] || 0) + 1);

    // Collect all active components on the page. Skip authorization-suppressed boundaries:
    // a denied component renders as <div data-component data-reactive-denied> with NO data-state,
    // so it is a morph placeholder, not a dispatch participant. Including it would post a null
    // state token and the server would reject the whole batch ("State token is missing").
    var components = [];
    document.querySelectorAll("[data-component]").forEach(function (el) {
      var state = el.getAttribute("data-state");
      if (!state) return; // denied/placeholder boundary — not dispatchable
      components.push({
        id: el.id,
        state: state
      });
    });

    var body = JSON.stringify({
      targetId: root.id,
      action: action || null,
      args: args || [],
      bindings: collectBindings(root),
      components: components
    });

    root.setAttribute("data-reactive-busy", "");
    root.classList.add("reactive-loading");

    try {
      var res = await fetchWithRetry(body);

      if (!res.ok) {
        // 401: the user's session/cookie expired or they are unauthenticated. Stop pollers and
        // full-page reload the current URL so the app's auth pipeline issues its login redirect
        // (with returnUrl). 403 and other errors are surfaced as reactive:error below.
        if (res.status === 401 && reloadOnUnauthorized()) {
          reloading = true;
          clearAllPollers();
          window.location.assign(window.location.href);
          return;
        }
        var errorText = await res.text();
        console.error("ReactiveBlazor dispatch failed:", res.status, errorText);
        clearBusy(root);
        root.dispatchEvent(new CustomEvent("reactive:error", {
          bubbles: true,
          detail: { status: res.status, message: errorText }
        }));
        return;
      }

      var updates = await res.json();

      // Stale-guard: a newer dispatch for this component superseded us while we were in flight.
      // Drop this morph so we don't overwrite the DOM (and the focused input) with stale state.
      if (seq !== dispatchSeq[rootId]) {
        clearBusy(document.getElementById(rootId) || root);
        return;
      }

      var redirectUrl = null;

      // Check for server-side redirects in any of the returned HTML components
      for (var id in updates) {
        var html = updates[id].trim();
        var tmp = document.createElement("div");
        tmp.innerHTML = html;
        var incoming = tmp.firstElementChild;
        if (incoming && incoming.getAttribute("data-redirect")) {
          redirectUrl = incoming.getAttribute("data-redirect");
          break;
        }
      }

      if (redirectUrl) {
        window.location.href = redirectUrl;
        return;
      }

      // ignoreActiveValue must be decided per-morph from the *currently focused* element.
      // In this idiomorph build the option skips child-node morphing of document.activeElement,
      // not just its value — so leaving it on while a <button> is focused (e.g. just clicked)
      // would suppress that button's own label/content update. Enable it only when a text field
      // is focused, which is the sole place the keystroke-vs-stale-echo race exists.
      var ignoreActiveValue = isTextFieldFocused();

      // Morph all updated components on the page
      for (var id in updates) {
        var target = document.getElementById(id);
        if (!target) continue;

        var html = updates[id].trim();
        var tmp = document.createElement("div");
        tmp.innerHTML = html;
        var incoming = tmp.firstElementChild;
        if (!incoming) continue;

        if (window.Idiomorph) {
          // When a text field is focused, don't overwrite its value: the browser already holds the
          // user's latest keystrokes, and the server's echoed value is one round-trip stale.
          // Everything else in the component still morphs.
          Idiomorph.morph(target, incoming, {
            morphStyle: "outerHTML",
            ignoreActiveValue: ignoreActiveValue
          });
        } else {
          target.replaceWith(incoming);
        }

        var updated = document.getElementById(id);
        if (updated) {
          clearBusy(updated);
          updated.dispatchEvent(new CustomEvent("reactive:updated", { bubbles: true }));
        }
      }

      // Safeguard: Ensure busy state on the original target root is cleared
      var finalRoot = document.getElementById(root.id);
      if (finalRoot) {
        clearBusy(finalRoot);
      }

      // Reconcile pollers: morphs may have added, removed, or retuned data-poll attributes.
      scanPollers();
    } catch (err) {
      console.error("ReactiveBlazor network error:", err);
      clearBusy(root);
      root.dispatchEvent(new CustomEvent("reactive:error", {
        bubbles: true,
        detail: { status: 0, message: err.message }
      }));
    }
  }

  function clearBusy(el) {
    el.removeAttribute("data-reactive-busy");
    el.classList.remove("reactive-loading");
  }

  // True when the focused element is an editable text field whose value the user may be mid-typing
  // into. Used to scope idiomorph's ignoreActiveValue so it never suppresses morphing of a focused
  // non-text element (e.g. a button's own label after it was clicked).
  function isTextFieldFocused() {
    var el = document.activeElement;
    if (!el) return false;
    var tag = el.tagName;
    if (tag === "TEXTAREA") return true;
    if (el.isContentEditable) return true;
    if (tag !== "INPUT") return false;
    // A readonly/disabled field can be focused but not typed into, so there's no keystroke race —
    // let server updates to its value morph through.
    if (el.readOnly || el.disabled) return false;
    // Non-text input types (checkbox, radio, button, range, color, file, ...) carry no
    // typed-but-unsent value, so there's nothing to protect from the stale server echo.
    var type = (el.getAttribute("type") || "text").toLowerCase();
    var nonText = {
      checkbox: 1, radio: 1, button: 1, submit: 1, reset: 1,
      range: 1, color: 1, file: 1, image: 1
    };
    return !nonText[type];
  }

  // ---- Fetch with one retry on network failure ----

  async function fetchWithRetry(body) {
    var url = endpoint();
    var headers = {
      "Content-Type": "application/json",
      "RequestVerificationToken": csrfToken()
    };

    try {
      return await fetch(url, { method: "POST", headers: headers, body: body });
    } catch (firstErr) {
      // Retry once after 1 second on network error (not HTTP errors).
      await new Promise(function (r) { setTimeout(r, 1000); });
      return await fetch(url, { method: "POST", headers: headers, body: body });
    }
  }

  // ---- Debounce ----

  var debounceTimers = {};

  function debounced(root, action, args, ms, queueMode) {
    var key = root.id + ":" + (action || "");
    clearTimeout(debounceTimers[key]);
    debounceTimers[key] = setTimeout(function () {
      dispatch(root, action, args, queueMode);
    }, ms);
  }

  // ---- Throttle ----
  // Unlike debounce (which waits for a pause in typing), throttle dispatches at a steady cadence
  // during sustained input: leading edge fires immediately, then at most once per `ms`. A trailing
  // call is always scheduled so the final keystroke is never dropped.

  var throttleState = {}; // key -> { last: ms, timer }

  function throttled(root, action, args, ms, queueMode) {
    var key = root.id + ":" + (action || "");
    var now = performance.now();
    var st = throttleState[key] || (throttleState[key] = { last: 0, timer: null });
    var remaining = ms - (now - st.last);

    if (remaining <= 0) {
      clearTimeout(st.timer);
      st.timer = null;
      st.last = now;
      dispatch(root, action, args, queueMode);
    } else {
      // Trailing edge: ensure the latest values are sent once the window elapses.
      clearTimeout(st.timer);
      st.timer = setTimeout(function () {
        st.last = performance.now();
        st.timer = null;
        dispatch(root, action, args, queueMode);
      }, remaining);
    }
  }

  // ---- Pollers (periodic auto-refresh) ----
  // A component opts into polling by emitting data-poll / data-poll-interval (and optional
  // data-poll-args) on its <ReactiveRoot> div. Each tick fires the normal dispatch pipeline,
  // so polling reuses queuing, signals/OOB, and DOM morphing for free. Because pollers are
  // reconciled from the DOM after every morph, a component can start/stop/retune its own
  // polling purely by toggling a server-side state property.

  function readPollConfig(root) {
    var action = root.getAttribute("data-poll");
    if (!action) return null; // no action => no poll
    var ms = parseInt(root.getAttribute("data-poll-interval"), 10);
    if (!isFinite(ms) || ms <= 0) return null; // off / invalid / NaN
    if (ms < MIN_POLL_INTERVAL) ms = MIN_POLL_INTERVAL; // clamp to floor
    var args = [];
    var raw = root.getAttribute("data-poll-args");
    if (raw) {
      try {
        var parsed = JSON.parse(raw);
        args = Array.isArray(parsed) ? parsed : [parsed];
      } catch (e) {
        args = [];
      }
    }
    return { action: action, interval: ms, args: args };
  }

  function pollTick(id) {
    return function () {
      if (reloading) return; // a 401 reload is in flight; stop polling
      // Re-query by id: a morph (or the replaceWith fallback) may have replaced the node.
      var root = document.getElementById(id);
      if (!root) { clearPoller(id); return; }     // component gone
      var cfg = readPollConfig(root);
      if (!cfg) { clearPoller(id); return; }       // polling turned off server-side
      // Skip while a dispatch for this component is in flight. "latest" queuing is the real
      // anti-pile-up guarantee; this is just a cheap optimization to avoid redundant requests.
      if (root.hasAttribute("data-reactive-busy")) return;
      dispatch(root, cfg.action, cfg.args, "latest");
    };
  }

  function clearPoller(id) {
    var p = pollers[id];
    if (p) {
      clearInterval(p.timer);
      delete pollers[id];
    }
  }

  function clearAllPollers() {
    for (var id in pollers) {
      clearInterval(pollers[id].timer);
    }
    pollers = {};
  }

  // Reconcile timers against the current DOM: start new pollers, stop removed ones, and
  // recreate any whose interval changed. Called on load, after every morph, and on tab focus.
  function scanPollers() {
    var seen = {};
    document.querySelectorAll("[data-component]").forEach(function (el) {
      var id = el.id;
      if (!id) return;
      var cfg = readPollConfig(el);
      if (!cfg) return; // not polling; cleanup pass below handles any stale timer
      seen[id] = true;
      var existing = pollers[id];
      if (existing && existing.interval === cfg.interval) {
        return; // same cadence — keep the running timer to avoid phase reset / drift
      }
      clearPoller(id); // new poller, or interval changed -> (re)create
      pollers[id] = {
        timer: setInterval(pollTick(id), cfg.interval),
        interval: cfg.interval
      };
    });
    // Cleanup: stop pollers whose component disappeared or stopped polling.
    for (var pid in pollers) {
      if (!seen[pid]) clearPoller(pid);
    }
  }

  // ---- Delegated event handlers ----

  function handleEvent(eventName) {
    document.addEventListener(eventName, function (e) {
      var attr = "data-on-" + eventName;
      var trigger = e.target.closest("[" + attr + "]");
      if (!trigger) return;
      var root = rootOf(trigger);
      if (!root) return;

      // Prevent default for click and submit events.
      if (eventName === "click" || eventName === "submit") {
        e.preventDefault();
      }

      var action = trigger.getAttribute(attr) || null;
      var args = parseArgs(trigger);
      var debounceMs = parseInt(trigger.getAttribute("data-debounce"), 10);
      var throttleMs = parseInt(trigger.getAttribute("data-throttle"), 10);
      var queueMode = trigger.getAttribute("data-queue") || "latest";

      if (debounceMs > 0) {
        debounced(root, action, args, debounceMs, queueMode);
      } else if (throttleMs > 0) {
        throttled(root, action, args, throttleMs, queueMode);
      } else {
        dispatch(root, action, args, queueMode);
      }
    });
  }

  // Register all supported events.
  // Note: only events that bubble are usable with document-level delegation.
  // (focus/blur do not bubble and are intentionally excluded.)
  ["click", "change", "input", "submit", "keydown", "keyup"].forEach(handleEvent);

  // ---- Poller bootstrap & visibility pause ----

  // Start pollers for components present on initial load (covers deferred/async script tags).
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", scanPollers);
  } else {
    scanPollers();
  }

  // Blazor enhanced navigation swaps page content via fetch without a full reload, so
  // DOMContentLoaded never fires for subsequent pages. Re-scan after each enhanced load so
  // pollers on a freshly-navigated page start immediately (not only after a first dispatch).
  // The event fires on the page that owns the runtime; clear stale pollers first so timers
  // for components that no longer exist are torn down. (No-op if Blazor isn't present.)
    Blazor.addEventListener("enhancedload", function () {
    clearAllPollers();
    scanPollers();
  });

  // Pause polling while the tab is hidden; resume (rebuild from current DOM) when visible.
  // No immediate catch-up dispatch on resume — avoids a thundering-herd refresh.
  document.addEventListener("visibilitychange", function () {
    if (document.hidden) {
      clearAllPollers();
    } else {
      scanPollers();
    }
  });

  // ---- Public API ----

  window.ReactiveBlazor = {
    dispatch: function (el) {
      var root = rootOf(el) || el;
      dispatch(root, null, [], "latest");
    },
    rescanPollers: scanPollers,
    version: libraryVersion()
  };
})();
