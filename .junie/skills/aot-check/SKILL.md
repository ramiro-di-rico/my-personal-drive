---
name: aot-check
description: Verify a change stays Native AOT / trim safe — publish with warnings visible, check JSON source generation and Avalonia compiled bindings. Use after touching serialization, reflection, dynamic types, or before a release.
---

# Native AOT / trim safety check

`src/MyPersonalDrive/MyPersonalDrive.csproj` sets `IsAotCompatible`, `PublishAot=true` and
`TrimMode=partial`. Code that works under `dotnet run` (JIT) can still fail in a published build,
and it fails at runtime, on the user's machine, on a path the trimmer removed. The test project
runs on the JIT test host, so **tests passing proves nothing about AOT**.

## When to run this

Any change that touches:

- `System.Text.Json` usage or a type that gets serialized
- Reflection, `Type.GetType`, `Activator.CreateInstance`, dynamic dispatch
- New NuGet packages
- Avalonia bindings, styles, or `DataTemplate`s
- Anything in `Program.cs` startup

## Procedure

1. **Publish with warnings visible** and read them — a warning is the finding:

   ```bash
   dotnet publish src/MyPersonalDrive/MyPersonalDrive.csproj -c Release -r linux-x64 --self-contained true -o /tmp/aot-check
   ```

   `IL2xxx` (trim) and `IL3xxx` (AOT) warnings are not noise. Each one is a code path the
   compiler cannot prove safe. Do not suppress; fix or make the dependency static.

2. **JSON.** Every serialized type must be registered in
   `src/MyPersonalDrive/Services/AppJsonContext.cs`:

   ```csharp
   [JsonSerializable(typeof(YourType))]
   ```

   and serialized through the context (`AppJsonContext.Default.YourType`), never through a
   reflection-based `JsonSerializer.Serialize<T>(value)` overload. Grep for regressions:

   ```bash
   grep -rn "JsonSerializer\.\(Serialize\|Deserialize\)" src/ --include=*.cs
   ```

   Every hit should pass a `JsonTypeInfo` / the context.

3. **Bindings.** `AvaloniaUseCompiledBindingsByDefault` is on. Confirm no binding fell back to
   reflection: missing `x:DataType` produces a build warning. Treat those as errors.

4. **Run the published binary** — the only real proof:

   ```bash
   /tmp/aot-check/MyPersonalDrive
   ```

   Exercise the code path you changed, not just startup. Trim failures are lazy: they surface
   when the removed member is first touched. Check `crash.log` in the app data folder
   (`$XDG_CONFIG_HOME`/`~/.config/MyPersonalDrive`) if it exits.

5. Clean up `/tmp/aot-check`.

## Common failures in this codebase

| Symptom | Cause | Fix |
|---|---|---|
| Settings silently reset on launch | Type missing from `AppJsonContext` | Add `[JsonSerializable]` |
| `MissingMetadataException` / `NotSupportedException` on save | Reflection-based serializer overload | Use the source-generated context |
| Blank control, binding does nothing, no exception | Binding without `x:DataType` | Add it to the root or template |
| Works in `dotnet run`, crashes when installed | Only the JIT path was tested | Always run the published binary |

## Report

State plainly: the publish command run, the warning count (and each warning), whether the
published binary was launched, and which code path was exercised. If you could not run the
published binary, say so — don't call it AOT-safe.
