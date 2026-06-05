# ReactiveBlazor

**Stateful interactive components for Blazor Static SSR — no SignalR, no WebAssembly.**

ReactiveBlazor lets you build interactive server-rendered Blazor components that respond to user input via standard HTTP round-trips. Every click, change, or keypress dispatches a `fetch` POST to the server, which re-renders the component and morphs the DOM in place using [Idiomorph](https://github.com/bigskysoftware/idiomorph).

---

## Features

- **Zero client-side code required** — ~220 lines of vanilla JS inside the library, no developer-written JS or build steps needed.
- **Multi-Component OOB Updates** — Actions that mutate shared state or DI services automatically re-render and morph all sibling components on the page in a single request.
- **Signed & encrypted state** — Component state is protected with ASP.NET Data Protection to prevent tampering.
- **Time-limited tokens** — State tokens expire after a configurable lifetime (default: 24 hours) to prevent stale submissions.
- **One-Time Use Tokens (Anti-Replay)** — Nonce validation to protect non-idempotent actions from duplicate replay.
- **CSRF protected** — Antiforgery tokens are automatically validated on every request.
- **DOM morphing** — Idiomorph preserves focus, text selection, scroll position, and CSS transitions.
- **Request queuing** — Rapid clicks or inputs are serialized per component to prevent race conditions.
- **Two-way binding** — `data-bind` syncs input values (text, dropdowns, checkboxes, radios) back to component properties.
- **Debounce support** — `data-debounce="300"` for search and text inputs to reduce network load.
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

---

## Multi-Component Out-of-Band (OOB) Updates

In static SSR, components are typically isolated. However, ReactiveBlazor supports **automatic out-of-band updates** to keep sibling components in sync without any client-side JavaScript glue.

When an action is dispatched:
1. The client sends the state of the target component *and* the states of all other reactive components currently present on the page.
2. The server processes the action on the target component first, mutating system state (e.g. updating a singleton `CartService`).
3. The server then batch renders the target component and all other sibling components.
4. It returns a JSON dictionary of updates (`id -> html`).
5. The client runtime morphs all updated components on the page.

No developer configuration is required. Simply use standard C# Dependency Injection or services (like a shared Cart or Notification service), and any component depending on that service will update in real-time when actions occur!

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

## License

This project is licensed under the [MIT License](LICENSE).
