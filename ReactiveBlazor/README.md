# ReactiveBlazor

**Stateful interactive components for Blazor Static SSR — no SignalR, no WebAssembly.**

ReactiveBlazor lets you build interactive server-rendered Blazor components that respond to user input via standard HTTP round-trips. Every click, change, or keypress dispatches a `fetch` POST to the server, which re-renders the component and morphs the DOM in place using [Idiomorph](https://github.com/bigskysoftware/idiomorph).

## Features

- **Zero client-side framework** — ~220 lines of vanilla JS, no build step
- **Signed & encrypted state** — component state is protected with ASP.NET Data Protection
- **CSRF protected** — antiforgery token validated on every request
- **DOM morphing** — Idiomorph preserves focus, scroll position, and CSS transitions
- **Request queuing** — rapid interactions are serialized per component
- **Two-way binding** — `data-bind` syncs input values to component properties
- **Debounce support** — `data-debounce="300"` for input-heavy scenarios
- **Redirect support** — set `RedirectUrl` in an action to navigate after response
- **Configurable** — customize dispatch path, max state size, and more

## Quick Start

### 1. Install

```bash
dotnet add package ReactiveBlazor
```

### 2. Register Services

```csharp
// Program.cs
builder.Services.AddReactiveComponents(assemblies: typeof(Program).Assembly);

// After app.MapRazorComponents<App>()
app.MapReactiveComponents();
```

### 3. Add Scripts to App.razor

```html
<head>
    <!-- ... -->
    <ReactiveScripts />
</head>
```

### 4. Create a Reactive Component

```razor
@inherits ReactiveBlazor.ReactiveComponent

<ReactiveRoot Owner="this">
    <p>Count: @Count</p>
    <button type="button" data-on-click="Increment">+1</button>
    <button type="button" data-on-click="Add" data-args="[5]">+5</button>
</ReactiveRoot>

@code {
    public int Count { get; set; }

    [ReactiveAction]
    public void Increment() => Count++;

    [ReactiveAction]
    public void Add(int amount) => Count += amount;
}
```

## How It Works

1. Your component inherits from `ReactiveComponent` and wraps its markup in `<ReactiveRoot>`.
2. Public read/write properties become **state** — serialized, encrypted, and embedded in the HTML via `data-state`.
3. Methods decorated with `[ReactiveAction]` become **actions** — callable from the client via `data-on-click`, `data-on-change`, etc.
4. The client JS intercepts events, collects state + bindings, and POSTs to `/_reactive/dispatch`.
5. The server decrypts the state, rehydrates the component, runs the action, re-renders, and returns HTML.
6. The client morphs the DOM with Idiomorph — preserving focus, scroll, and animations.

## Data Attributes

| Attribute | Description |
|---|---|
| `data-on-click="ActionName"` | Invoke an action on click |
| `data-on-change` | Invoke on change (useful with `data-bind`) |
| `data-on-input="ActionName"` | Invoke on input |
| `data-on-submit="ActionName"` | Invoke on form submit |
| `data-on-keydown="ActionName"` | Invoke on keydown |
| `data-bind="PropertyName"` | Two-way bind an input's value to a state property |
| `data-args="[1, \"hello\"]"` | Pass arguments to the action method |
| `data-debounce="300"` | Debounce the dispatch by N milliseconds |
| `data-queue="all"` | Queue every request (don't drop intermediate ones) |

## Configuration

```csharp
builder.Services.AddReactiveComponents(options =>
{
    options.MaxStateBytes = 128 * 1024;        // Max state size (default: 64KB)
    options.DispatchPath = "/_reactive/dispatch"; // Endpoint path (default)
}, assemblies: typeof(Program).Assembly);
```

## Excluding Properties from State

Use `[ReactiveIgnore]` on properties that shouldn't round-trip:

```csharp
[ReactiveIgnore]
public string[] StaticOptions { get; set; } = ["A", "B", "C"];
```

## License

MIT
