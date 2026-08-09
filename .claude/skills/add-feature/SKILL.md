---
name: add-feature
description: Add a UI feature to the Avalonia app following the repo's MVVM pattern (ViewModel + compiled bindings + AsyncCommand + ViewModel tests). Use when adding a view, panel, button, or any user-facing action.
---

# Add a UI feature (Avalonia MVVM)

Hand-rolled MVVM — no ReactiveUI, no CommunityToolkit. The base types are
`ViewModels/ObservableObject.cs` and `ViewModels/AsyncCommand.cs`. Match them; don't bring a
framework in for one feature.

## Layering

```
View (.axaml)  →  ViewModel  →  Service (ProtonDriveService / Sync/*)  →  IProtonDriveCliExecutor
```

- Code-behind (`*.axaml.cs`) holds only what genuinely needs the visual tree: dialogs,
  storage-provider pickers, focus. **No business logic, no CLI calls.**
- ViewModels never touch `Process`, the filesystem, or Avalonia types. That's what makes them
  testable without a UI thread.

## Steps

1. **ViewModel** in `src/MyPersonalDrive/ViewModels/` (sync features go under `ViewModels/Sync/`).
   - Inherit `ObservableObject`. Use `SetProperty(ref _field, value)` for stored properties.
   - For computed properties whose source lives elsewhere, call `OnPropertyChanged(nameof(X))`
     explicitly when the dependency changes — `SetProperty` cannot see it.
   - Collections bound to the UI: `ObservableCollection<T>`, mutated only from the UI thread.
   - Take dependencies through the constructor (`ProtonDriveService`, `AppSettingsService`,
     `TimeProvider`, …). Never `new` a service inside a ViewModel — it kills testability.
   - Use `TimeProvider` for anything time-based; tests substitute `FakeTimeProvider`.

2. **Commands** — `AsyncCommand`, always:
   ```csharp
   RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy, onError: HandleError);
   ```
   - `ICommand.Execute` is `async void`; an escaping exception kills the process. Always pass
     `onError`, and call `RaiseCanExecuteChanged()` when the `canExecute` inputs change.
   - Tests await `command.ExecuteAsync()`, not `Execute()`. Keep the awaitable path intact.

3. **View** in `src/MyPersonalDrive/Views/`.
   - `AvaloniaUseCompiledBindingsByDefault` is on: every binding needs a resolvable
     `x:DataType` on the root or the enclosing template, or it won't compile.
   - Icons go in `Assets/Icons.axaml` as resources, not inline paths.
   - Bind `IsEnabled` to the command, not to a duplicate boolean property.

4. **Errors.** Convert `CliException` to a user-facing message by switching on `.Kind`
   (`CliErrorKind`). Never match on the exception message.

5. **Persistence.** New user-facing settings go on `AppSettings`, are saved via
   `AppSettingsService`, and — because the app publishes with AOT — the type must be reachable
   from `AppJsonContext`. Durable sync state belongs in `SyncStateStore` / `DriveCacheService`
   with a migration in `DriveDatabaseMigrations`, never in `settings.json`.

6. **Tests** in `tests/MyPersonalDrive.Tests/ViewModels/`.
   - Build the ViewModel over `FakeCliExecutor` (+ `FakeTimeProvider` if time matters).
   - Assert on observable state and on the CLI calls the action produced, not on rendering.
   - Cover the failure path: enqueue a `CliException` and assert the ViewModel surfaces it
     instead of throwing.
   - Follow the shape of `SyncPanelPairCreationTests.cs` / `SyncConflictFlowTests.cs`.

7. **Verify**:

   ```bash
   ./scripts/run-tests.sh
   ```

   Then run the app for real — see the `run-app` and `smoke-test` skills. Compiled-binding and
   layout mistakes do not show up in unit tests.

## Checklist

- [ ] No CLI, filesystem, or `Process` access in the ViewModel; no logic in code-behind
- [ ] Dependencies injected via constructor; `TimeProvider` instead of `DateTime.Now`
- [ ] `AsyncCommand` with `onError`; `RaiseCanExecuteChanged` wired
- [ ] `x:DataType` present so compiled bindings resolve
- [ ] Errors routed through `CliErrorKind`
- [ ] ViewModel tests for success and failure; `scripts/run-tests.sh` green
- [ ] App launched and the feature exercised by hand
- [ ] `README.md` Features list and `docs/ARCHITECTURE.md` updated if behavior is user-visible
