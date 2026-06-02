// ReactiveBlazor client runtime v2.
// Handles: dispatch, request queuing, DOM morphing (Idiomorph), busy state,
//          error handling, debounce, redirect, and generic data-on-* events.
(function () {
  "use strict";

  var ENDPOINT = "/_reactive/dispatch";

  // Per-component request queues. Key = component element id.
  var queues = {};

  // ---- Helpers ----

  function csrfToken() {
    var m = document.querySelector('meta[name="reactive-csrf"]');
    return m ? m.getAttribute("content") : "";
  }

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
    try { return JSON.parse(raw); } catch (e) { return [raw]; }
  }

  // ---- Request queue (serializes dispatches per component) ----

  function enqueue(rootId, fn) {
    if (!queues[rootId]) {
      queues[rootId] = { running: false, pending: null };
    }
    var q = queues[rootId];
    // Only keep the latest pending request (newer input supersedes older).
    q.pending = fn;
    processQueue(rootId);
  }

  function processQueue(rootId) {
    var q = queues[rootId];
    if (!q || q.running || !q.pending) return;
    q.running = true;
    var fn = q.pending;
    q.pending = null;
    fn().finally(function () {
      q.running = false;
      processQueue(rootId);
    });
  }

  // ---- Core dispatch ----

  function dispatch(root, action, args) {
    var rootId = root.id;
    enqueue(rootId, function () {
      return doDispatch(root, action, args);
    });
  }

  async function doDispatch(root, action, args) {
    // Re-query root by id in case it was replaced by a previous morph.
    root = document.getElementById(root.id);
    if (!root) return;

    var body = JSON.stringify({
      state: root.getAttribute("data-state"),
      action: action || null,
      args: args || [],
      bindings: collectBindings(root)
    });

    root.setAttribute("data-reactive-busy", "");
    try {
      var res = await fetch(ENDPOINT, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "RequestVerificationToken": csrfToken()
        },
        body: body
      });

      if (!res.ok) {
        var errorText = await res.text();
        console.error("ReactiveBlazor dispatch failed:", res.status, errorText);
        root.removeAttribute("data-reactive-busy");
        // Fire error event for custom handling.
        root.dispatchEvent(new CustomEvent("reactive:error", {
          bubbles: true,
          detail: { status: res.status, message: errorText }
        }));
        return;
      }

      var html = (await res.text()).trim();
      var tmp = document.createElement("template");
      tmp.innerHTML = html;

      // Find the matching ReactiveRoot by its stable ID.
      // The server may return HTML containing content outside the ReactiveRoot
      // (e.g., a page component with child components), so we can't use firstElementChild.
      var incoming = tmp.content.querySelector("#" + CSS.escape(root.id))
                  || tmp.content.querySelector("[data-component]")
                  || tmp.content.firstElementChild;
      if (!incoming) {
        root.removeAttribute("data-reactive-busy");
        return;
      }

      // Check for server-side redirect.
      var redirectUrl = incoming.getAttribute("data-redirect");
      if (redirectUrl) {
        window.location.href = redirectUrl;
        return;
      }

      // Morph the DOM.
      if (window.Idiomorph) {
        Idiomorph.morph(root, incoming, { morphStyle: "outerHTML" });
      } else {
        root.replaceWith(incoming);
      }

      // Clear busy on the (now-replaced) element.
      var updated = document.getElementById(incoming.id || root.id);
      if (updated) {
        updated.removeAttribute("data-reactive-busy");
        // Fire success event.
        updated.dispatchEvent(new CustomEvent("reactive:updated", { bubbles: true }));
      }
    } catch (err) {
      console.error("ReactiveBlazor network error:", err);
      root.removeAttribute("data-reactive-busy");
      root.dispatchEvent(new CustomEvent("reactive:error", {
        bubbles: true,
        detail: { status: 0, message: err.message }
      }));
    }
  }

  // ---- Debounce ----

  var debounceTimers = {};

  function debounced(root, action, args, ms) {
    var key = root.id + ":" + (action || "");
    clearTimeout(debounceTimers[key]);
    debounceTimers[key] = setTimeout(function () {
      dispatch(root, action, args);
    }, ms);
  }

  // ---- Delegated event handlers ----

  // Generic handler factory for data-on-{event} attributes.
  function handleEvent(eventName) {
    document.addEventListener(eventName, function (e) {
      var attr = "data-on-" + eventName;
      var trigger = e.target.closest("[" + attr + "]");
      if (!trigger) return;
      var root = rootOf(trigger);
      if (!root) return;

      // Prevent default for click events (buttons, links).
      if (eventName === "click" || eventName === "submit") {
        e.preventDefault();
      }

      var action = trigger.getAttribute(attr) || null;
      var args = parseArgs(trigger);
      var debounceMs = parseInt(trigger.getAttribute("data-debounce"), 10);

      if (debounceMs > 0) {
        debounced(root, action, args, debounceMs);
      } else {
        dispatch(root, action, args);
      }
    });
  }

  // Register all supported events.
  ["click", "change", "input", "submit", "keydown", "keyup", "focus", "blur"].forEach(handleEvent);

  // ---- Public API ----

  window.ReactiveBlazor = {
    dispatch: function (el) {
      var root = rootOf(el) || el;
      dispatch(root, null, []);
    },
    version: "2.0.0"
  };
})();
