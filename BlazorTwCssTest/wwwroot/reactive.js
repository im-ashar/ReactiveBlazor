// ReactiveBlazor client runtime.
// Requires Idiomorph (https://github.com/bigskysoftware/idiomorph) to be loaded before this script.
// Falls back to a plain replace if Idiomorph is absent (loses focus/scroll - install Idiomorph for production).
(function () {
  "use strict";

  var ENDPOINT = "/_reactive/dispatch";

  function csrfToken() {
    var m = document.querySelector('meta[name="reactive-csrf"]');
    return m ? m.getAttribute("content") : "";
  }

  function rootOf(el) {
    return el.closest("[data-component]");
  }

  // Collect the live values of every [data-bind] input within the component boundary.
  function collectBindings(root) {
    var bindings = {};
    root.querySelectorAll("[data-bind]").forEach(function (el) {
      var name = el.getAttribute("data-bind");
      bindings[name] = el.type === "checkbox" ? el.checked : el.value;
    });
    return bindings;
  }

  async function dispatch(root, action, args) {
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
        console.error("ReactiveBlazor dispatch failed:", res.status, await res.text());
        return;
      }

      var html = (await res.text()).trim();
      var tmp = document.createElement("template");
      tmp.innerHTML = html;
      var incoming = tmp.content.firstElementChild;
      if (!incoming) return;

      if (window.Idiomorph) {
        Idiomorph.morph(root, incoming, { morphStyle: "outerHTML" });
      } else {
        root.replaceWith(incoming); // fallback - no focus/scroll preservation
      }
    } finally {
      // root may have been replaced; re-query by id is fine since the id is stable.
    }
  }

  function parseArgs(el) {
    var raw = el.getAttribute("data-args");
    if (!raw) return [];
    try { return JSON.parse(raw); } catch (e) { return [raw]; }
  }

  // Delegated handlers - work for elements added by morphing, no re-binding needed.
  document.addEventListener("click", function (e) {
    var trigger = e.target.closest("[data-on-click]");
    if (!trigger) return;
    var root = rootOf(trigger);
    if (!root) return;
    e.preventDefault();
    dispatch(root, trigger.getAttribute("data-on-click"), parseArgs(trigger));
  });

  document.addEventListener("change", function (e) {
    var trigger = e.target.closest("[data-on-change]");
    if (!trigger) return;
    var root = rootOf(trigger);
    if (!root) return;
    // Empty action => just sync bindings and re-render.
    dispatch(root, trigger.getAttribute("data-on-change") || null, []);
  });
})();
