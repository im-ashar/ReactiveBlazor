# ReactiveBlazor

**Stateful interactive components for Blazor Static SSR — no SignalR, no WebAssembly.**

ReactiveBlazor lets you build interactive server-rendered Blazor components that respond to user input via standard HTTP round-trips. Every click, change, or keypress dispatches a `fetch` POST to the server, which re-renders the component and morphs the DOM in place using [Idiomorph](https://github.com/bigskysoftware/idiomorph).

---

## Features

- **Zero client-side code required** — ~280 lines of vanilla JS inside the library, no developer-written JS or build steps needed.
- **Strongly-typed signals for OOB updates** — Actions publish typed `IReactiveSignal` records; components opt in with `[OnReactiveSignal<T>]` to be re-rendered out-of-band. No tight coupling through shared services required.
- **Signed & encrypted state** — Component state is protected with ASP.NET Data Protection to prevent tampering.
- **Time-limited tokens** — State tokens expire after a configurable lifetime (default: 24 hours) to prevent stale submissions.
- **One-Time Use Tokens (Anti-Replay)** — Nonce validation to protect non-idempotent actions from duplicate replay.
- **CSRF protected** — Antiforgery tokens are automatically validated on every request.
- **DOM morphing** — Idiomorph preserves focus, text selection, scroll position, and CSS transitions.
- **Request queuing** — Rapid clicks or inputs are serialized per component to prevent race conditions.
- **Two-way binding** — `data-bind` syncs input values (text, dropdowns, checkboxes, radios) back to component properties.
- **Debounce support** — `data-debounce="300"` for search and text inputs to reduce network load.
- **Polling** — components can auto-refresh on a timer (`PollAction` / `PollInterval` on `<ReactiveRoot>`); ticks reuse the normal dispatch pipeline (queuing, signals, morphing), pause on hidden tabs, and start/stop by toggling a state property.
- **Redirect support** — Set `RedirectUrl` in an action to navigate the browser to a new URL after processing.
- **Multi-target** — Supports .NET 8, .NET 9, and .NET 10.

---

## Quick Start

### 1. Install

```bash
dotnet add package ReactiveBlazor
```

### 2. Register Services

In your `Program.cs`, register the required services:

```csharp
// Program.cs
builder.Services.AddDataProtection();  // Required — configure secure key storage for production
builder.Services.AddReactiveComponents(assemblies: typeof(Program).Assembly);

var app = builder.Build();

// After app.MapRazorComponents<App>()
app.MapReactiveComponents();
app.Run();
```

### 3. Add Scripts to App.razor

Add `<ReactiveScripts />` and the default loading indicator styles to the `<head>` of your root component:

```html
<head>
    <!-- ... -->
    <ReactiveScripts />
    <!-- Optional: include the default loading indicator style (fades out busy components) -->
    <link rel="stylesheet" href="/_content/ReactiveBlazor/reactive.css" />
</head>
```

### 4. Create a Reactive Component

Inherit from `ReactiveComponent`, wrap your markup in `<ReactiveRoot>`, and declare public properties and action methods:

```razor
@inherits ReactiveBlazor.ReactiveComponent

<ReactiveRoot Owner="this">
    <p>Count: @Count</p>
    <button type="button" class="btn" data-on-click="Increment">+1</button>
    <button type="button" class="btn" data-on-click="Add" data-args="[5]">+5</button>
</ReactiveRoot>

@code {
    // Public properties represent state. They are encrypted and serialized automatically.
    public int Count { get; set; }

    // Methods decorated with [ReactiveAction] can be called from client events.
    [ReactiveAction]
    public void Increment() => Count++;

    [ReactiveAction]
    public void Add(int amount) => Count += amount;
}
```

> [!IMPORTANT]
> `<ReactiveRoot Owner="this">` must be the component's **single outermost element** — don't render
> page headers, wrappers, or sibling markup outside it. The client morphs the server-rendered HTML
> onto the one `data-component` boundary element, so markup outside `ReactiveRoot` causes the
> component to be nested into itself on update. Put shared chrome in a parent page and make each
> reactive piece its own component (see the `/notifications` and `/polling` demos). The dispatch
> endpoint fails fast with a clear error if this invariant is violated.

---

## Multi-Component Out-of-Band (OOB) Updates via Reactive Signals

In static SSR, components are typically isolated. ReactiveBlazor lets you keep them in sync
without coupling them through shared services or writing any client-side glue — just publish
a typed signal from an action, and every subscribed component on the page is re-rendered
out-of-band in the same dispatch response.

### 1. Define a signal

Any `record` (or class) that implements `IReactiveSignal` is a signal. Payloads are optional:

```csharp
public sealed record CartChanged : IReactiveSignal;
public sealed record NotificationAdded(int Id, string Level) : IReactiveSignal;
```

### 2. Publish from an action

Every `ReactiveComponent` has access to `ReactiveSignals` for publishing:

```csharp
[ReactiveAction]
public void AddToCart(int productId)
{
    _cart.Add(productId);
    ReactiveSignals.Publish<CartChanged>();
    // Or with a payload:
    // ReactiveSignals.Publish(new NotificationAdded(id: 42, level: "info"));
}
```

Three overloads are available:

```csharp
ReactiveSignals.Publish<CartChanged>();                            // default-constructed
ReactiveSignals.Publish(new NotificationAdded(42, "info"));        // instance with payload
ReactiveSignals.Publish(typeof(CartChanged));                      // runtime Type
```

### 3. Subscribe on the consumer component

Decorate the consumer class with `[OnReactiveSignal<T>]`. Stack the attribute to subscribe
to multiple signals:

```razor
@inherits ReactiveBlazor.ReactiveComponent
@attribute [OnReactiveSignal<CartChanged>]

<ReactiveRoot Owner="this">
    <span class="badge">@ItemCount</span>
</ReactiveRoot>

@code {
    public int ItemCount { get; set; }
    protected override void OnInitialized() => ItemCount = _cart.Count();
}
```

```razor
@attribute [OnReactiveSignal<NotificationAdded>]
@attribute [OnReactiveSignal<NotificationRead>]
@attribute [OnReactiveSignal<NotificationsCleared>]
```

### 4. (Optional) Read the published payload

The class attribute alone is enough to get a component re-rendered. If you also want the
**data** that was published — not just a refresh — query the bus from inside a Blazor
lifecycle method using the same `ReactiveSignals` property you publish with:

```razor
@inherits ReactiveBlazor.ReactiveComponent
@attribute [OnReactiveSignal<NotificationAdded>]
@attribute [OnReactiveSignal<NotificationsCleared>]
@inject NotificationService Notifications

<ReactiveRoot Owner="this">
    <span>🔔 @UnreadCount</span>
    @if (LastToast is not null)
    {
        <div class="alert alert-@LastToast.Level">#@LastToast.Id — @LastToast.Message</div>
    }
</ReactiveRoot>

@code {
    public int UnreadCount { get; set; }
    public NotificationAdded? LastToast { get; set; }

    protected override void OnInitialized()
    {
        // Always refresh from source of truth
        UnreadCount = Notifications.UnreadCount();

        // Read every NotificationAdded published this dispatch (empty if none)
        foreach (var s in ReactiveSignals.GetPublished<NotificationAdded>())
            LastToast = s;

        // Cheap boolean check for payload-less signals
        if (ReactiveSignals.WasPublished<NotificationsCleared>())
            LastToast = null;
    }
}
```

Three query methods are available on `IReactiveSignals`:

```csharp
IEnumerable<T> GetPublished<T>() where T : IReactiveSignal;   // payloads, in publish order
bool WasPublished<T>() where T : IReactiveSignal;             // any of T published?
bool WasPublished(Type signalType);                            // runtime form
```

Polymorphic queries work too — `GetPublished<ICartSignal>()` returns every published signal
whose type is assignable to `ICartSignal`.

### How dispatch works

For every dispatch:

1. The client sends the state of the target component **plus** the states of every other
   reactive component currently on the page.
2. The server runs the action on the target, which may publish zero or more signals into
   the per-request `IReactiveSignals` bus.
3. The server collects every component type subscribed to a published signal via
   `[OnReactiveSignal<T>]` and re-renders the corresponding instances on the page.
4. The response returns a JSON dictionary of `id → html` containing only the target + the
   matched subscribers — not every component on the page.
5. The client morphs each entry into the DOM using Idiomorph.

Components that don't subscribe to any published signal are **not** re-rendered, even if
they were sent up in the request. This keeps updates targeted and avoids unintended
side-effects on unrelated components.

> The demo project includes a multi-signal page at `/notifications` showing three signal
> types, three subscribers (each subscribed to a different combination), and one isolated
> component to verify that non-subscribers are skipped.

---

## Data Attributes

Decorate HTML elements inside `<ReactiveRoot>` to connect them to C# actions and properties:

| Attribute | Description |
|---|---|
| `data-on-click="ActionName"` | Invoke an action on click |
| `data-on-change="ActionName"` | Invoke an action when the input value changes |
| `data-on-input="ActionName"` | Invoke an action on text input |
| `data-on-submit="ActionName"` | Invoke an action on form submit (prevents default postback) |
| `data-on-keydown="ActionName"` | Invoke on keydown |
| `data-bind="PropertyName"` | Two-way bind an input's value to a C# property |
| `data-args="[1, \"hello\"]"` | Pass arguments (serialized as a JSON array) to the action method |
| `data-debounce="300"` | Delay dispatch by N milliseconds (ideal for inputs and search boxes) |
| `data-queue="all"` | Queue every request (default is `latest`, which drops intermediate requests) |

---

## Configuration

Customize limits and behavior during service registration:

```csharp
builder.Services.AddReactiveComponents(options =>
{
    options.MaxStateBytes = 128 * 1024;              // Max state size (default: 64KB)
    options.MaxTokenBytes = 512 * 1024;              // Max encrypted token size (default: 256KB)
    options.StateTokenLifetime = TimeSpan.FromHours(12); // Token expiry (default: 24h)
    options.DispatchPath = "/_reactive/dispatch";    // Custom dispatch endpoint (default)
}, assemblies: typeof(Program).Assembly);
```

---

## Excluding Properties from State

Use `[ReactiveIgnore]` on public properties that shouldn't be serialized into the page token (e.g. static lists, read-only cache data):

```csharp
[ReactiveIgnore]
public string[] StaticOptions { get; set; } = ["Classic", "Cyberpunk", "Forest"];
```

---

## Loading States

While a dispatch is in-flight, the library adds the `data-reactive-busy` attribute and the `reactive-loading` CSS class to the component's root element. 

Include the default stylesheet for built-in styling (adds opacity fade and disables mouse clicks):
```html
<link rel="stylesheet" href="/_content/ReactiveBlazor/reactive.css" />
```

Or write custom CSS rules:
```css
[data-reactive-busy] {
    pointer-events: none;
    opacity: 0.6;
    transition: opacity 0.2s ease;
}
```

---

## Polling (Auto-Refresh)

Components can refresh themselves on a timer — no user interaction required. Polling is configured on `<ReactiveRoot>` and reuses the entire dispatch pipeline, so each tick benefits from request queuing, out-of-band signal fan-out, and DOM morphing exactly like a click would.

```razor
@inherits ReactiveBlazor.ReactiveComponent
@inject MetricsService Metrics

<ReactiveRoot Owner="this" PollAction="Refresh" PollInterval="@(IsLive ? 2000 : 0)">
    <p>CPU: @Cpu% — updated @LastUpdated</p>
    <button type="button" data-on-click="ToggleLive">@(IsLive ? "Stop" : "Start")</button>
</ReactiveRoot>

@code {
    public bool IsLive { get; set; } = true;
    public double Cpu { get; set; }
    public string LastUpdated { get; set; } = "—";

    // Invoked on every poll tick.
    [ReactiveAction]
    public void Refresh()
    {
        Cpu = Metrics.ReadCpu();
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    [ReactiveAction]
    public void ToggleLive() => IsLive = !IsLive;
}
```

`<ReactiveRoot>` polling parameters:

| Parameter | Description |
|---|---|
| `PollAction` | Name of the `[ReactiveAction]` to invoke on each tick. Polling is on only when this is set **and** `PollInterval > 0`. |
| `PollInterval` | Interval in milliseconds. `0` (default) disables polling. The client enforces a **250ms floor**. |
| `PollArgs` | Optional pre-serialized JSON array string of arguments for the poll action (mirrors `data-args`). |

**Start / stop / retune at runtime:** bind `PollInterval` (and/or `PollAction`) to a state property. When `PollInterval` returns to `0`, the poll attributes disappear on the next morph and the client clears the timer automatically — so a component controls its own polling purely through server-side state.

**Behavior:**

- Ticks use `"latest"` queue semantics, so they never pile up — a pending tick supersedes the previous one.
- A tick is skipped while a dispatch for that component is already in flight.
- Polling **pauses while the browser tab is hidden** and resumes (without a catch-up burst) when it regains focus.
- Timers are torn down automatically on page navigation/redirect — no manual cleanup needed.

> The demo project includes a live metrics dashboard at `/polling` with a Start/Stop toggle and an OOB subscriber (`MetricsSampled`) updated by each poll tick.

---

## Security Guidelines & Production Configuration

### ⚠️ Production Data Protection Configuration
By default, ASP.NET Core Data Protection uses an ephemeral in-memory key store or a local filesystem store. 

> [!WARNING]
> If your application restarts, runs inside transient containers (like Docker/Kubernetes), or is scaled horizontally behind a load balancer (High Availability), the keys used to encrypt state tokens will mismatch or be lost. This will result in immediate decryption failures (`400 Bad Request`) for your users.
> 
> **For single-server VM setups (surviving server restarts without external databases/caches):**
> ```csharp
> builder.Services.AddDataProtection()
>     .PersistKeysToFileSystem(new DirectoryInfo(@"C:\app-keys\")) // Survives server restarts
>     .ProtectKeysWithDpapi(); // Or DPAPI-NG / X.509 Certificate
> ```
> 
> **For load-balanced, multi-instance, or container environments (HA):**
> ```csharp
> builder.Services.AddDataProtection()
>     .PersistKeysToDbContext<MyDbContext>() // Or PersistKeysToStackExchangeRedis()
>     .ProtectKeysWithAzureKeyVault(...); // Or ProtectKeysWithDpapi() / Certs
> ```

---

### 🛡️ One-Time Use Tokens (Anti-Replay)
For non-idempotent actions (like checkouts, processing payments, or adding database records), you can prevent users from resending/replaying the same interaction request within the token lifetime.

Decorate your critical actions with `RequireOneTimeToken`:
```csharp
[ReactiveAction(RequireOneTimeToken = true)]
public void ProcessPayment()
{
    // This action can only be invoked once per state token payload.
}
```

#### Multi-Instance Nonce Store (e.g. Redis)
By default, nonces are tracked in local memory. If you are running multiple instances of your application, you must replace the default in-memory store with a shared/distributed nonce store by implementing `IReactiveNonceStore`:

```csharp
public class RedisNonceStore : IReactiveNonceStore
{
    private readonly IDatabase _redis;
    public RedisNonceStore(IConnectionMultiplexer redis) => _redis = redis.GetDatabase();

    public bool TryConsume(string nonce, TimeSpan lifetime)
    {
        // Try to set the key in Redis with PX (expire) and NX (set if not exists)
        return _redis.StringSet($"nonce:{nonce}", "used", lifetime, When.NotExists);
    }
}
```
And register it in your `Program.cs`:
```csharp
builder.Services.AddSingleton<IReactiveNonceStore, RedisNonceStore>();
builder.Services.AddReactiveComponents(); // Will automatically skip registering the in-memory fallback
```

#### Single-Instance Persistent Nonce Store (e.g. SQLite)
If you want one-time action tokens to survive server restarts on a single VM without deploying a Redis cache, you can implement a disk-backed store using a local SQLite database:

```csharp
using Microsoft.Data.Sqlite;

public class SqliteNonceStore : IReactiveNonceStore
{
    private readonly string _connectionString = "Data Source=nonces.db";

    public SqliteNonceStore()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS ConsumedNonces (Nonce TEXT PRIMARY KEY, ExpiresAt DATETIME)";
        cmd.ExecuteNonQuery();
    }

    public bool TryConsume(string nonce, TimeSpan lifetime)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Cleanup expired nonces
        var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM ConsumedNonces WHERE ExpiresAt < @now";
        deleteCmd.Parameters.AddWithValue("@now", DateTime.UtcNow);
        deleteCmd.ExecuteNonQuery();

        // Insert new nonce
        try
        {
            var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO ConsumedNonces (Nonce, ExpiresAt) VALUES (@nonce, @expires)";
            insertCmd.Parameters.AddWithValue("@nonce", nonce);
            insertCmd.Parameters.AddWithValue("@expires", DateTime.UtcNow.Add(lifetime));
            insertCmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException) // Unique constraint violation (nonce already used)
        {
            return false;
        }
    }
}
```

Register it in your `Program.cs`:
```csharp
builder.Services.AddSingleton<IReactiveNonceStore, SqliteNonceStore>();
```

---

### 🔒 Actions are Public Endpoints
Every public method marked with `[ReactiveAction]` is exposed as an endpoint that can be remotely invoked. 

> [!IMPORTANT]
> Do not rely on hiding buttons or elements in your Blazor markup to prevent users from executing actions. An attacker can easily read the state token from the DOM and fire a custom fetch POST request.
> 
> **You must perform all authorization, validation, and business rule checks inside the action method itself:**
> ```csharp
> [ReactiveAction]
> public void DeleteRecord(int id)
> {
>     if (!User.IsInRole("Admin")) throw new UnauthorizedAccessException();
>     // Delete code...
> }
> ```

---

### 🛡️ Opt-In State Serialization
By default, ReactiveBlazor uses an **opt-out** model: all public read/write properties are automatically serialized into the state token unless they are decorated with `[ReactiveIgnore]`.

To prevent accidental exposure of sensitive properties, you can switch to an **opt-in** model:

1. Enable opt-in in registration options:
```csharp
builder.Services.AddReactiveComponents(options =>
{
    options.RequireOptInState = true;
});
```

2. Explicitly decorate properties you want to serialize with `[ReactiveState]`:
```csharp
@inherits ReactiveBlazor.ReactiveComponent

<ReactiveRoot Owner="this">
    <p>User Profile for @Username</p>
</ReactiveRoot>

@code {
    [ReactiveState]
    public string Username { get; set; } // Will be serialized

    public string PasswordHash { get; set; } // Ignored (will not be sent to client)
}
```

---

## When to Use ReactiveBlazor

ReactiveBlazor fills a specific gap: **event-driven, granular interactivity on static SSR without a persistent connection**. It is not a replacement for Blazor's interactive render modes — pick the right tool:

| Scenario | Recommended approach |
|---|---|
| Public, high-traffic pages where a per-user SignalR circuit is too expensive | **ReactiveBlazor** |
| Cheap/stateless hosting, autoscaling, or serverless where circuits are impractical | **ReactiveBlazor** |
| Small islands of interactivity (counters, search, cart badges) on otherwise static pages | **ReactiveBlazor** |
| Rich, low-latency client apps with lots of fast UI state | **Interactive WebAssembly** |
| Highly interactive apps where a stateful circuit is acceptable | **Interactive Server** |
| Simple data submission that fits a `<form>` post | **Built-in enhanced form handling** (no library needed) |

Each interaction is a server round-trip, so ReactiveBlazor is best for interactions measured in clicks, not continuous high-frequency input.

---

## Limitations & Roadmap

- **Requires JavaScript.** Interactivity is driven by the bundled runtime; there is no no-JS form fallback. Progressive enhancement is an explicit non-goal — if you need it, use built-in enhanced forms for those interactions.
- **Whole-page state travels on each dispatch.** The client uploads the encrypted state of every reactive component on the page per interaction. Keep per-component state small (see `MaxStateBytes` / `MaxComponentsPerDispatch`).
- **Trimming / Native AOT.** State serialization and action dispatch use reflection and reflection-based `System.Text.Json`, so the library is **not currently trim-safe or AOT-safe**. Avoid `PublishTrimmed` / `PublishAot` for apps using ReactiveBlazor. A source-generator-based path is on the roadmap.
- **Authorize inside actions.** Every `[ReactiveAction]` is a remotely invokable endpoint. Throw `UnauthorizedAccessException` for denied access (returned to the client as `403`); perform all validation server-side (see the Security section).

---

## License

This project is licensed under the [MIT License](LICENSE).

It bundles [Idiomorph](https://github.com/bigskysoftware/idiomorph) (BSD 2-Clause); see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
