using System;

namespace ReactiveBlazor;

/// <summary>
/// Marks a public read/write property on a <see cref="ReactiveComponent"/> subclass as part of the
/// serialized reactive state when opt-in state serialization is enabled.
/// </summary>
/// <remarks>
/// This is used when <see cref="ReactiveOptions.RequireOptInState"/> is set to <c>true</c>
/// to explicitly choose which properties should be round-tripped to the client.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ReactiveStateAttribute : Attribute;
