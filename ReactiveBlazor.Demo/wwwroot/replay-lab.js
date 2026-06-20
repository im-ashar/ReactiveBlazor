// Token Replay Lab harness (demo-only).
//
// Loaded once from App.razor so it survives Blazor enhanced navigation — unlike a <script> inside
// a page component, which enhanced nav injects but never executes. Buttons are wired with delegated
// listeners on `document` (which persists across navigations) keyed by data-replay attributes.
//
// Reads the encrypted token straight from the DOM (as any script — or an attacker — could) and
// re-submits it to the dispatch endpoint under the current session's cookies + antiforgery token.
(function () {
    "use strict";

    var captured = null;

    function meta(name, fallback) {
        var m = document.querySelector('meta[name="' + name + '"]');
        return m ? m.getAttribute("content") : fallback;
    }

    // The encrypted token lives on the ReactiveRoot BOUNDARY element (data-component + data-state),
    // not on the inner #secret-note card.
    function note() { return document.querySelector('[data-component="SecretNote"]'); }
    function el(id) { return document.getElementById(id); }

    function capture() {
        var boundary = note();
        var out = el("replay-captured");
        if (!out) return; // not on the replay-lab page
        var state = boundary && boundary.getAttribute("data-state");
        if (!state) {
            out.textContent = "Couldn't read a token — type a note first so the component has state, then capture.";
            return;
        }
        captured = { id: boundary.id, state: state };
        out.textContent = "Captured token (" + state.length + " chars):\n" + state.slice(0, 120) + "…";
        var result = el("replay-result");
        if (result) result.innerHTML = '<span class="text-base-content/50">Now switch user (top right) and hit Replay.</span>';
    }

    async function replay() {
        var result = el("replay-result");
        if (!result) return;
        if (!captured) { result.innerHTML = '<span class="text-warning">Capture a token first.</span>'; return; }

        var boundary = note();
        var targetId = boundary ? boundary.id : captured.id;
        var body = {
            targetId: targetId,
            action: "Touch",
            args: [],
            bindings: null,
            // Replay the CAPTURED token, but align the id to the component on the page so the
            // endpoint matches it as the dispatch target.
            components: [{ id: targetId, state: captured.state }]
        };

        try {
            var res = await fetch(meta("reactive-endpoint", "/_reactive/dispatch"), {
                method: "POST",
                headers: { "Content-Type": "application/json", "RequestVerificationToken": meta("reactive-csrf", "") },
                body: JSON.stringify(body)
            });
            if (!res.ok) {
                result.innerHTML = '<span class="text-error">Dispatch failed (' + res.status + ').</span>';
                return;
            }
            var updates = await res.json();
            var html = updates[targetId] || "";
            var tmp = document.createElement("div");
            tmp.innerHTML = html;
            var secret = tmp.querySelector("#secret-value");
            var value = secret ? secret.textContent.trim() : "(not found)";
            var reset = value === "(empty)";
            result.innerHTML =
                '<div class="alert ' + (reset ? "alert-success" : "alert-warning") + ' py-2 text-xs">' +
                (reset
                    ? "🛡️ Protected — the replayed token reset to <strong>(empty)</strong>. The other user's secret was NOT exposed."
                    : "⚠️ Returned secret: <strong>" + value + "</strong> — this is the same user (legitimate), or BindStateToUser is off.") +
                "</div>";
        } catch (e) {
            result.innerHTML = '<span class="text-error">' + e.message + "</span>";
        }
    }

    // Delegated click handler — works no matter when the buttons enter the DOM (enhanced nav included).
    document.addEventListener("click", function (e) {
        var btn = e.target.closest("[data-replay]");
        if (!btn) return;
        e.preventDefault();
        if (btn.getAttribute("data-replay") === "capture") capture();
        else if (btn.getAttribute("data-replay") === "replay") replay();
    });
})();
