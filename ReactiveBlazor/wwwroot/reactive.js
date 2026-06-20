// ReactiveBlazor client runtime.
// Version is injected by the server at render time via the
// <meta name="reactive-version"> tag emitted by <ReactiveScripts />.
// Handles: dispatch, request queuing, DOM morphing (Idiomorph), busy state,
//          error handling, debounce, redirect, retry, and generic data-on-* events.
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

  // ---- Per-component request queues ----
  // Key = component element id.
  var queues = {};

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
    // Re-query root by id in case it was replaced by a previous morph.
    root = document.getElementById(root.id);
    if (!root) return;

    // Collect all active components on the page
    var components = [];
    document.querySelectorAll("[data-component]").forEach(function (el) {
      components.push({
        id: el.id,
        state: el.getAttribute("data-state")
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
          Idiomorph.morph(target, incoming, { morphStyle: "outerHTML" });
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
      var queueMode = trigger.getAttribute("data-queue") || "latest";

      if (debounceMs > 0) {
        debounced(root, action, args, debounceMs, queueMode);
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
