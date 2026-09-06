# Technical Plan — Interface Localization (i18n)

> The interface is currently single-language Spanish by decision — [PLAN-UX-ROUND-2.md](PLAN-UX-ROUND-2.md)
> **U4** swept 419 lines to make it so, and a residue of English survives in places U4's scoping
> missed. This plan **supersedes U4's premise**: the UI becomes translatable, ships English and
> Spanish, defaults to **English**, and lets the user pick the language in Settings without
> restarting. It also defines the seam that makes a third language a data change rather than a
> code change — the procedure for that lives in
> [`.claude/skills/add-language/SKILL.md`](../.claude/skills/add-language/SKILL.md).
>
> Companions: [ARCHITECTURE.md](ARCHITECTURE.md), [PLAN-UX-ROUND-2.md](PLAN-UX-ROUND-2.md) (§U4),
> [PLAN-CLOUD-PROVIDERS.md](PLAN-CLOUD-PROVIDERS.md) (the `ProviderCatalog` shape `LanguageCatalog`
> copies), [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md).
>
> Implementation branch: not started. Branch from `main` after `feature/ux-round-2` merges.

## Status

> **L0-L9 implemented on branch `feature/i18n`, 2026-09-05**, from `main` at `14413d8`. 1053 tests
> passing (from 1001). The AOT publish is clean — only the five warnings that predate this work —
> and both locales are verifiably embedded in the single-file binary. **Not visually verified: this
> environment has no working screenshot tool** (GNOME refuses `ScreenshotWindow` over D-Bus), so
> the Settings layout with the picker, and the language actually changing on screen, are both
> unconfirmed by eye and want a human pass — see the 2026-09-06 note below, which closes part of
> this. The counts in §0.1 were taken on `feature/ux-round-2`
> and are slightly low against the merged `main`, which added the per-pane toolbars.
>
> **Final state: 620 keys, English and Spanish, 1096 tests passing (from 1001).** Every phase's AOT
> publish was clean, and both locales are present in the single-file binary.
>
> **Confirmed working in the running app, 2026-09-06** — the picker changes the whole interface,
> verified by the user after the `LocalizedStrings` fix ([§3.1](#31-what-the-compile-check-could-not-see)).
> What that confirms is the *mechanism*. Layout under English copy — which is a different length
> from the Spanish it replaced, in a UI full of fixed widths — has still not been looked at.
>
> L3 turned up one thing worth carrying forward: `{Binding Loc[key]}` needs the DataContext to be
> a view model, and one `DataTemplate` in the header is typed against `ProviderDescriptor`. That
> single site names the singleton explicitly —
> `{Binding [key], Source={x:Static loc:Localizer.Instance}, x:DataType=loc:Localizer}` — which is
> still a compiled binding. Expect the same for any template over a model type in L4-L6.

- [x] **L0 — Live-switch spike.** Both halves work, but **not the way this phase concluded** — it
      called the markup proven and the view-model half unproven, and it was the other way round.
      Corrected in [§3.1](#31-what-the-compile-check-could-not-see) after the shipped picker changed
      nothing on screen; the fix is `LocalizedStrings`.
- [x] **L1 — Localization infrastructure.** `Services/Localization/` — `Language`,
      `LanguageCatalog`, `StringKeys`, `LocaleCatalogLoader`, `Localizer`, and `Locales/en.json` /
      `es.json` (45 keys) globbed as embedded resources. `AppSettings.Language` +
      `LanguageOrDefault`, applied in `App.OnFrameworkInitializationCompleted` beside `ApplyTheme`.
      `ObservableObject` gained `Loc` and `OnAllPropertiesChanged`. Nine locale-integrity tests and
      seventeen behaviour tests in `tests/.../Services/Localization/`.
- [x] **L2 — Settings view + the language picker.** The whole Settings `ScrollViewer` reads through
      `{Binding Loc[...]}`; a `ComboBox` over `LanguageCatalog.Available` is the first row of
      General preferences. Three near-duplicate sign-in/sign-out literals per provider collapsed
      into `SignInTooltip` / `SignOutTooltip` taking the provider name. Found in passing: the S3
      button's `Content` said "Conectar Bucket S3" while its own inner label said "Conectar S3" —
      one key now covers both.
- [x] **L3 — Shell and Explorer markup.** All 128 remaining literals in `MainWindow.axaml`
      (header, view tabs, both explorer toolbars, context menus, the status/details sidebar, folder
      metrics, the CLI console, the viewer) resolved into 83 keys — the ratio is the four context
      menus, which carried four copies of the same vocabulary. Both hidden `StringFormat` literals
      became view-model properties: `LocalExplorerViewModel.FreeSpaceLabel` (which had kept an
      English `"{0} free"` through the Spanish-only round) and
      `MainWindowViewModel.ActiveOperationsText` (which had a Spanish-specific
      `"operación(es) activa(s)"` plural hack, now `Localizer.Plural`). What is left in the file is
      exactly L9's intended allowlist: the `DRIVE` wordmark, a `·` separator, and the five provider
      names.
- [x] **L4 — Code-built dialogs (`MainWindow.axaml.cs`).** Every user-facing string in the file:
      the six platform pickers, the rename/new-folder/copy prompts, the upload-conflict picker, the
      add/edit sync pair form and its remote folder browser, the sync preview, the conflict and
      failure resolution dialogs, properties, and the generic confirm/alert. Dialogs are built on
      demand, so nothing here needed a language-change subscription. The one design decision is the
      preview summary — see [§6.4](#64-l4s-one-design-decision-the-preview-summarys-two-counts).
      Two findings parked rather than fixed inline:
      [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md) **B6.3** (the sync dialogs name Proton Drive whichever
      provider is syncing) and **B6.4** (a second byte formatter disagreeing with `ByteSize`).
- [x] **L5 — `MainWindowViewModel`, and the rest of the non-sync view models.**
      `MainWindowViewModel` (183 sites), `LocalExplorerViewModel`, `FolderMetricsViewModel`,
      `DriveNodeViewModel`, `TransferItemViewModel`/`TransferQueueViewModel`, and
      `ProviderDescriptor.AccountSummary`. §6.3's three cases were applied as written; the machinery
      is `Services/Localization/LocalizedText.cs` — see
      [§6.5](#65-l5-what-actually-happened-to-the-three-cases).
- [x] **L6 — Sync surface.** `SyncPanelView.axaml`, `SyncPanelViewModel`, `SyncPairViewModel`,
      `SyncFailureViewModel`, `AccountSyncToggleViewModel`, `ProviderFilterViewModel`. Both status
      lines here are `LocalizedText` too, so a pair row reading "Up to date (…)" — which sits
      untouched for as long as nothing changes, the worst case for a frozen string — follows the
      picker. The plural work this phase was supposed to exercise landed as expected: conflicts,
      failures, retried/discarded actions, attempts, and recovered download folders all go through
      `Plural`. One thing is deliberately still `Verbatim`: `SyncPanelViewModel.AlertAsync`'s
      message, which comes from `SyncPairValidator` — that is L7's typed-reason work.
- [x] **L7 — Service-layer messages → typed reasons.** `SyncPairValidator` and
      `LocalFolderInspector` return a `Models/SyncPairIssue` (kind + arguments) instead of a
      sentence; `ViewModels/SyncIssuePresenter` words it. `DriveErrorPresenter` holds the
      `DriveErrorKind` → key table. `FileKindClassifier`, `SyncExecutor`'s progress/summary/skip
      copy, `LocalFileWatcher`'s degradation notice and `SyncScheduler`'s retry notice went through
      the string table. **§9's boundary moved slightly** — see
      [§9.1](#91-what-l7-actually-drew-the-line-around).
- [x] **L8 — Culture-aware formatting, and the invariant-culture audit.** `.editorconfig` turns
      CA1304/CA1305/CA1310 on as warnings; the sweep behind them is done and the build is clean.
      Seven flagged sites plus two the analyzer could not see, each classified as machine data
      (invariant) or presentation (`Localizer.Culture`). `ByteSize` moved from invariant to the
      interface language's culture — it is presentation, and a Spanish interface should say
      "1,2 GB". `CultureHazardTests` pins both halves. Also folded in
      [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md) **B6.4**, the third byte formatter. The audit found
      **two real latent bugs**, both machine data formatted through the ambient culture: the local
      trash folder's `yyyy-MM-dd` name and the corrupt-settings quarantine file's timestamp.
- [x] **L9 — The no-literals lint gate.** Three tests in
      `tests/.../Localization/NoHardcodedStringsTests.cs`: no `.axaml` carries a literal
      `Text`/`Content`/`PlaceholderText`/`Header`/`ToolTip.Tip`, no binding carries a word-bearing
      `StringFormat`, and no value is byte-identical across locales outside a list of
      language-neutral templates. The allowlist is **seven entries** — the `DRIVE` wordmark, a `·`
      separator and the five provider names — which is what this phase was waiting for.

---

## 0. Executive summary

### 0.1 The size of the problem, measured

Counted on `feature/ux-round-2` (`2026-09-05`), literal user-facing strings:

| Surface | Count | Notes |
|---|---:|---|
| `Views/MainWindow.axaml` | 191 | `Text` / `Content` / `PlaceholderText` / `ToolTip.Tip` / `Header` attributes |
| `Views/SyncPanelView.axaml` | 15 | |
| `Views/BreadcrumbBar.axaml` | 0 | already fully bound — the target shape |
| `ViewModels/MainWindowViewModel.cs` | ~292 | the single biggest block; 3959 lines |
| `Views/MainWindow.axaml.cs` | ~127 | code-built dialogs |
| `ViewModels/Sync/*` | ~113 | panel, pair, failure |
| `Services/Sync/*` | ~105 | **not all UI** — see [§9](#9-l7--service-layer-messages--typed-reasons) |
| `Services/Providers/*` | ~250 | mostly JSON property names and API literals, *not* UI |

So the honest figure is **roughly 750–800 real user-facing strings**, concentrated in four files.
This is not a one-commit change and must not be attempted as one.

### 0.2 The five decisions this plan makes

1. **No `.resx`, no `ResourceManager`, no satellite assemblies.** The app is `PublishAot=true` /
   `TrimMode=partial` (see [AGENTS.md](../AGENTS.md) non-negotiables). `ResourceManager` resolves
   satellite assemblies by culture through reflection and assembly probing — exactly the pattern
   AOT and trimming are hostile to. Instead: **one flat JSON file per language, embedded, loaded
   through `AppJsonContext`**, which is the mechanism this codebase already uses for every other
   serialized type.
2. **Keys are C# constants, values are JSON.** `StringKeys` gives compile-time safety and
   find-all-references; the JSON gives a translator a file they can edit without touching code. A
   test asserts the two sets are identical, so they cannot drift.
3. **Language changes live, without a restart** — subject to the L0 spike proving it. The
   mechanism is an indexer-change notification from a singleton `Localizer`, which is the standard
   Avalonia idiom, plus one new `ObservableObject` helper for ViewModels.
4. **Services do not translate.** `Services/` throws `DriveException` with a typed
   `DriveErrorKind` and a raw provider sentence; the UI turns the *kind* into a localized sentence
   and shows the raw sentence as untranslated detail. This is already the repo's stated rule
   ("Errors are typed") — L7 finishes applying it rather than inventing anything.
5. **`CultureInfo.CurrentCulture` becomes non-invariant, which is a parsing hazard.** See
   [§10](#10-l8--culture-aware-formatting-and-the-invariant-culture-audit). This is the single
   most likely way this change breaks something unrelated.

### 0.3 Why the phases are ordered this way

- **L0 before everything** because the whole ergonomic promise ("pick a language, see it change")
  rests on one unproven mechanism. Finding out at L5 that it needs a restart would mean redoing
  the shape of every migrated ViewModel property. One day of spike de-risks eight phases.
- **L1 before any string moves** so that every later phase is mechanical.
- **L2 (Settings) first among the surfaces** because the picker lives there: until Settings is
  translated *and* holds the switch, no other phase can be verified by eye at all.
- **Markup (L3) before C# (L4/L5)** because attribute → `{loc:T …}` is a pure substitution with a
  compiler check behind it, whereas C# strings need a per-site decision about *when* the string is
  evaluated (§6.3). Doing the easy 200 first builds the key vocabulary the hard 400 reuse.
- **ViewModels (L5/L6) before Services (L7)** because L7 is not a string move — it is a small API
  change (typed validation reasons) and it should be designed once the UI's actual needs are known.
- **Formatting (L8) late** because it needs `Localizer.Culture` to already exist and be switchable.
- **The lint gate (L9) last**, because a gate that has to ship with a 400-entry allowlist teaches
  nothing. It goes in when the allowlist is genuinely short.

### 0.4 Explicitly out of scope

- **RTL / bidirectional layout.** English and Spanish are both LTR. Adding Arabic or Hebrew later
  is a *layout* project (`FlowDirection`, mirrored icons, breadcrumb chevrons), not a strings one.
  The skill says so.
- **Regional variants.** `es`, not `es-AR` / `es-ES`. Vos/tú wording follows the existing copy,
  which is Rioplatense ("Arrastrá", "Agregá").
- **Plural rules beyond one/other.** The `PluralRule` delegate in `LanguageCatalog` exists so a
  Slavic language can be added without re-architecting, but no such rule is written now.
- **A translator pipeline** (`.po`, Crowdin, machine translation on CI).
- **Localizing anything the app parses.** CLI stdout, Graph and Drive API payloads stay invariant.
- **Localized packaging** — `.desktop` entry name, tarball contents, `README`.
- **Localized number *input*.** `NumericUpDown` for the bandwidth limit stays invariant-parsed.

---

## 1. Target shape

```
src/MyPersonalDrive/
  Services/Localization/
    Language.cs            record: Code, EnglishName, NativeName, PluralRule
    LanguageCatalog.cs     the static list + ResolveOrDefault  (mirrors ProviderCatalog)
    StringKeys.cs          public const string keys, grouped in nested static classes
    LocaleCatalogLoader.cs reads the embedded JSON via AppJsonContext
    Localizer.cs           the singleton: indexer, T/F/Plural, Culture, SetLanguage
    Locales/
      en.json              the reference locale — every key exists here
      es.json
  Views/Localization/
    TranslateExtension.cs  the {loc:T key} markup extension
```

Nothing else moves. There is no new project, no new package reference.

## 2. L1 — Localization infrastructure

### 2.1 `Language` and `LanguageCatalog`

Deliberately shaped like `Services/Providers/ProviderCatalog` — same "known list + degrade to a
default rather than throw" contract, for the same reason (a settings file written by a newer build
must not crash an older one).

```csharp
public sealed record Language(string Code, string EnglishName, string NativeName)
{
    /// One/other only, today. A language needing few/many supplies its own.
    public Func<int, string> PluralCategory { get; init; } = n => n == 1 ? "one" : "other";
}

public static class LanguageCatalog
{
    public const string DefaultCode = "en";

    public static IReadOnlyList<Language> Available { get; } =
    [
        new("en", "English", "English"),
        new("es", "Spanish", "Español"),
    ];

    public static Language ResolveOrDefault(string? code) => ...;   // never throws
}
```

`NativeName` is what the picker shows — a Spanish speaker looking for their language scans for
"Español", not "Spanish". `EnglishName` is for logs and the console.

### 2.2 The locale files

`Locales/en.json`, flat, dotted keys, sorted:

```json
{
  "nav.explorer": "Explorer",
  "settings.general.title": "General preferences",
  "settings.language.label": "Interface language:",
  "sync.pairs.count.one": "{0} sync pair",
  "sync.pairs.count.other": "{0} sync pairs"
}
```

Flat, not nested: it keeps the `AppJsonContext` type to `Dictionary<string, string>` (one new
`[JsonSerializable]` line, no custom converter, no AOT surprise), it makes a missing key trivially
diffable between locales, and it makes the "same key set" test a one-liner.

Both files are `<EmbeddedResource>` in the `.csproj`, globbed:

```xml
<ItemGroup>
  <EmbeddedResource Include="Services/Localization/Locales/*.json" />
</ItemGroup>
```

The glob matters: **adding a language must not require a `.csproj` edit.**

### 2.3 `StringKeys`

```csharp
public static class StringKeys
{
    public static class Nav
    {
        public const string Explorer = "nav.explorer";
        public const string Viewer   = "nav.viewer";
        public const string Sync     = "nav.sync";
    }
    public static class Settings { /* … */ }
}
```

Grouped by surface, not by control type. Callers do `using static … StringKeys;` and write
`Loc.T(Nav.Explorer)`.

### 2.4 `Localizer`

One singleton, `Localizer.Instance`, deriving from `ObservableObject` so XAML can bind to it.

```csharp
public sealed class Localizer : ObservableObject
{
    public static Localizer Instance { get; } = new();

    public Language Current { get; private set; }
    public CultureInfo Culture { get; private set; }   // for dates, numbers, ByteSize
    public event EventHandler? LanguageChanged;

    [IndexerName("Item")]
    public string this[string key] => T(key);

    public string T(string key);
    public string F(string key, params object?[] args);      // string.Format(Culture, …)
    public string Plural(string keyPrefix, int count, params object?[] args);

    public void SetLanguage(string code);
}
```

Contracts, each with a test:

- **Missing key → fall back to `en`.** If `en` lacks it too, return `⟦key⟧` in `DEBUG` (loud, so it
  is caught while developing) and the bare `key` in release (ugly but not a crash). Never throw.
- **`SetLanguage` with an unknown code** resolves to `en` and does not throw.
- **`SetLanguage` sets `CultureInfo.DefaultThreadCurrentCulture` and `…UICulture`** as well as its
  own `Culture`, then raises `OnPropertyChanged("Item[]")` and `LanguageChanged`, in that order.
- **`Plural(prefix, n)`** looks up `$"{prefix}.{Current.PluralCategory(n)}"`, falling back to
  `$"{prefix}.other"`.
- **Loading is eager and total** — both locales parse at startup, at a cost of a few dozen KB. No
  lazy per-key IO, nothing that can fail mid-session.

### 2.5 `AppSettings.Language`

Same string-not-enum reasoning already documented on `ViewMode` and `ActiveProvider`:

```csharp
/// <summary>The interface language, as a <see cref="Localization.Language.Code"/> ("en", "es").
/// String, not enum, for the same reason as <see cref="ActiveProvider"/>: a code written by a
/// newer build must degrade to English, not throw. Read through <see cref="LanguageOrDefault"/>.</summary>
public string Language { get; set; } = LanguageCatalog.DefaultCode;

public string LanguageOrDefault() => LanguageCatalog.ResolveOrDefault(Language).Code;
```

Applied in `App.OnFrameworkInitializationCompleted`, on the line next to the existing
`ApplyTheme(settings.Load().ThemeOrDefault())` — same place, same shape, before any ViewModel is
constructed.

### 2.6 Open decision — what existing installs see on upgrade

The default is English. An existing `settings.json` has no `Language` field, so it deserializes to
`"en"` and **the app a current user knows in Spanish flips to English on first launch after the
upgrade.**

Three options, decide before L1 lands:

| | Behavior | Cost |
|---|---|---|
| **A (recommended)** | Default `en`, no migration. | One user, one visit to Settings. Honest and trivial. |
| **B** | On first run *with no settings file at all*, seed from `CultureInfo.CurrentUICulture` if it matches a known language, else `en`. On an *existing* file with no `Language`, write `"es"` once. | ~20 lines and a migration test; preserves what today's user sees. |
| **C** | Seed from OS culture on first run only; existing files get `en`. | Middle. Still flips today's user. |

**Decided: A**, on `feature/i18n`. `AppSettings.Language` defaults to `"en"` with no migration
branch, and `AppSettingsLanguageTests` pins that a settings file predating the field reads as
English. The rest of this section is kept as the record of what was weighed.

**A** unless the app already has users beyond the author. B's "existing file ⇒ es" branch is the
only thing that actually prevents the flip, and it is the kind of one-shot migration that outlives
its usefulness.

### 2.7 Tests landing with L1

In `tests/MyPersonalDrive.Tests/Services/Localization/`:

- `EveryLocaleHasTheSameKeysAsEnglish` — set equality, both directions, failure message naming the
  offending keys. This is the test that makes the skill safe.
- `NoLocaleValueIsEmptyOrWhitespace`.
- `PlaceholderSetsMatchEnglishPerKey` — the `{0}`/`{1}` set in `es["x"]` equals the one in
  `en["x"]`. Catches the classic "translator dropped the `{0}`" crash.
- `StringKeysConstantsAreExactlyTheEnglishKeySet` — reflection over `StringKeys` is fine here;
  it is a test, which runs on the JIT host.
- `EveryPluralKeyHasBothOneAndOther`.
- `UnknownLanguageCodeFallsBackToEnglish`, `MissingKeyFallsBackToEnglish`, `SetLanguageRoundTrip`.
- `FakeLocalizer` in `tests/Fakes/`, returning the key verbatim, so ViewModel tests assert on keys
  and never on prose. **Existing ViewModel tests asserting Spanish sentences will need updating as
  each phase lands** — budget for that, it is not incidental.

## 3. L0 — The live-switch spike

Do this **first**, in a throwaway branch, and let it decide L2's shape.

Two mechanisms have to be proven together:

**(a) Markup.** A `TranslateExtension` used as `Text="{loc:T settings.general.title}"`. The naive
implementation returns `new Binding("[key]") { Source = Localizer.Instance }` — a *reflection*
binding, which contradicts `AvaloniaUseCompiledBindingsByDefault` and is exactly the sort of thing
`aot-check` exists to catch. Prefer returning an `InstancedBinding` over an `IObservable<object?>`
fed by `Localizer.LanguageChanged` — no reflection over the indexer at all. **The spike must
`./scripts/publish-linux.sh` and show zero new trim/AOT warnings**, not merely run under `dotnet run`.

**(b) ViewModels.** Avalonia's binding system treats `PropertyChangedEventArgs("")` as "every
property changed". If that holds, one helper on `ObservableObject`:

```csharp
/// <summary>Every property is stale — used when the interface language changes and every
/// derived label has to be re-read. Avalonia treats an empty property name as "all".</summary>
protected void OnAllPropertiesChanged()
    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
```

…and each ViewModel subscribes to `Localizer.LanguageChanged` once. That single helper is what
keeps L5 from becoming 292 hand-written `OnPropertyChanged` calls.

**Exit criteria.** A Settings label and a ViewModel-derived label both change on the spot, with a
clean AOT publish. **If either fails**, fall back to: apply on next launch, with an inline
"Restart to apply" note beside the picker — and then §6.3's `LocalizedText` machinery is
unnecessary and L5 gets substantially cheaper. Record the outcome in this section before L1.

### Outcome, 2026-09-05

**(a) Markup — proven, and cheaper than this section assumed.** No markup extension was needed at
all. `ObservableObject` exposes `Loc`, so every ViewModel is already a valid binding source and the
markup writes `{Binding Loc[settings.general.title]}` — a plain **compiled** binding that the
Avalonia XAML compiler resolves statically against `Localizer`'s indexer. No reflection binding, no
`InstancedBinding`/`IObservable` plumbing, no departure from
`AvaloniaUseCompiledBindingsByDefault`. The open question this section flagged — whether the
compiled-binding path parser tolerates dotted keys inside `[...]` — is answered: **it does**, which
is why the dotted convention in §8 survives. Verified by:

- the XAML compiler accepting all 45 bindings (a bad path is a build error, not a runtime one);
- `./scripts/publish-linux.sh` producing **zero** new trim/AOT warnings;
- both `en.json` and `es.json` string content being present in the published single-file binary,
  so the embedded-resource glob survives AOT;
- the published binary running for 25s with no crash and an empty `crash.log`.

**(b) ViewModels — wired, not observed.** `ObservableObject.OnAllPropertiesChanged()` raises
`PropertyChangedEventArgs(string.Empty)`, and `MainWindowViewModel` subscribes to
`Localizer.LanguageChanged`. `LocalizerTests.SetLanguageRaisesTheIndexerChangeThenLanguageChanged`
pins the notification order. **What is not proven is that Avalonia re-reads on an empty property
name** — that needs a running window and an eye on it, and this environment has no screenshot tool.

**Consequence for the phases after this one.** Treat the restart fallback as still live until
someone watches the picker change a ViewModel-derived label. If it turns out Avalonia ignores the
empty name, the fix is local — enumerate the affected property names in `OnAllPropertiesChanged`,
or re-raise per view model — and does **not** invalidate (a) or anything in L1/L2, because the
markup path does not go through it.

## 4. L2 — Settings view and the language picker

1. Translate the Settings `ScrollViewer` (`MainWindow.axaml:1495`+) — section titles, field
   labels, placeholders, tooltips, button captions. Provider *names* ("Proton Drive", "OneDrive",
   "Google Drive", "Nextcloud", "Custom S3") are proper nouns and stay literal; they go on L9's
   allowlist.
2. Add the picker as the **first** row of "General preferences", above Theme:
   - `ComboBox`, not the Theme row's button group. Two buttons would be fine today and wrong at
     five languages, and the picker is the one control this whole plan exists to add.
   - `ItemsSource="{Binding Languages}"`, `DisplayMemberBinding` on `NativeName`,
     `SelectedItem="{Binding SelectedLanguage, Mode=TwoWay}"`.
   - The setter calls `Localizer.SetLanguage(code)`, then `_settings.Update(s => s.Language = code)`
     — mirroring `ThemePreference` (`MainWindowViewModel.cs:644`) exactly.
3. Both locale files get every key this phase introduces.
4. Tests: setting `SelectedLanguage` persists and flips `Localizer.Current`; the persisted value is
   read back at construction.

At the end of L2 the feature is **usable and demonstrable**, with the rest of the app still
Spanish-only. That is a deliberate, shippable checkpoint.

## 5. L3 — Shell and Explorer markup

The remaining ~160 attributes in `MainWindow.axaml`: header, the three view tabs, breadcrumb
labels, toolbar tooltips, both panes' column headers, the status card, empty states, the console.

Mechanical, but two rules:

- **Do not invent a key per occurrence.** "Cancel" appears many times; it is `common.cancel` once.
  Build the `common.*` group early — it is the vocabulary L4 and L5 reuse most.
- **`StringFormat=` in a binding is a hidden literal** (e.g.
  `StringFormat='{}{0} operación(es) activa(s)'` — which also encodes a Spanish-specific plural
  hack). Every one of these becomes a ViewModel property that goes through `Plural`. Grep for
  `StringFormat` explicitly; the attribute-name grep above does not find them.

## 6. L4/L5 — Code-built dialogs and `MainWindowViewModel`

### 6.1 L4 — `MainWindow.axaml.cs` (~127 strings)

Dialog titles, body copy, button captions, file-picker filter names. These are built and shown
immediately, so `Loc.T(key)` at the call site is correct with no lifetime concern. Cheap phase;
do it before L5 to get the `common.*` dialog vocabulary settled.

### 6.4 L4's one design decision: the preview summary's two counts

The sync preview's three summary lines each carried *two* counts in one sentence —
`"↓ {n} archivo(s) a descargar ({size}), {m} carpeta(s) a crear localmente."`. The `(s)` hack is
Spanish-specific and no other language can reproduce it, but the deeper problem is that a single
format string cannot make two different counts agree independently: at 1 file and 3 folders, no
one wording is correct.

Each count became its own clause with its own plural lookup, joined with `", "` in code (a local
`TwoClauses` helper), with the trailing period living in the second clause. This is a small
concession — the joining comma is not itself translatable — and the honest alternative, a full
message-format grammar with nested plurals, is far more machinery than nine sentences justify.
Revisit only if a language arrives where the clause order has to change.

### 6.2 L5 — `MainWindowViewModel` (~292 strings)

The hard one. Split it into reviewable commits **by region of the file**, not one sweep.

### 6.3 The one real design question: when is a string evaluated?

Three cases, and each site must be classified:

1. **Derived label** — `StatusActionLabel`, `QuotaTooltip`, a computed caption. Make the getter
   call `Loc.T(...)`. `OnAllPropertiesChanged()` re-reads it on language change. No storage.
2. **Transient message** — `"Abriendo la autenticación…"` shown for a second mid-operation.
   Translate at the assignment site. If the user switches language during it, the *next* message
   is right. Acceptable; do not over-engineer.
3. **Persistent message** — a stored status/error that stays on screen indefinitely
   (`StatusMessage` after a failure, a sync pair's last-result line). Storing the rendered string
   freezes it in the old language forever. These store the key and args instead:

```csharp
public readonly record struct LocalizedText(string Key, params object?[] Args)
{
    public string Render() => Args.Length == 0 ? Loc.T(Key) : Loc.F(Key, Args);
    public static readonly LocalizedText None = new(string.Empty);
}
```

Backing field is `LocalizedText`, the bound property renders. **Apply this only to case 3** — using
it everywhere buys nothing and makes 292 sites harder to read. If L0 lands on the restart fallback,
`LocalizedText` is not needed at all.

### 6.5 L5: what actually happened to the three cases

**Case 1, derived labels** — done as planned: the getter calls `Loc`, and
`ObservableObject.OnAllPropertiesChanged()` makes the bindings re-read. Nothing is stored.

**Case 2, transient messages** — translated at the assignment site, as planned. A "Uploading 3
files…" that is replaced a second later does not need to survive a language change.

**Case 3, persistent messages** — `LocalizedText` exists and is used, and it turned out to be
*cheaper* than this section assumed, because the 94 `StatusMessage = $"…"` sites had to be edited
anyway. Each became `SetStatus(key, args…)` or `SetStatusPlural(prefix, count, args…)`, which is
the same edit count and reads better. `StatusMessage`'s plain string setter is kept for the one
caller outside the view model (the view's drag handlers), and wraps its argument as
`LocalizedText.Verbatim` so already-rendered text is never looked up as a key.

Two consequences worth recording:

- **`FormatDriveError` returns a `LocalizedText`, not a string.** It has to: a status line holding
  a provider failure is exactly a case-3 message, and the provider's own sentence rides along as an
  argument rather than being baked into a rendered string. The two call sites that build a list of
  failure lines call `.Render()` explicitly.
- **`internal LocalizedText StatusText`** exists so tests can assert on a key rather than on prose.
  Without it the whole test suite is pinned to English copy.

Selection labels needed one extra piece: the details sidebar's seven fields are stored, and the
"None" placeholder would otherwise stay in the old language. `SelectItem` was split, with
`RefreshSelectionLabels()` re-derivable on a language change — deliberately without `SelectItem`'s
status-line side effect, so switching language does not re-announce the selection.

**What is knowingly left stale on a language change:** the CLI console's transient text
(`ActiveCommand`, `CommandLogText`), the viewer note, and `CliVersion`/`CliUpdateStatus`. All are
overwritten by the next operation, and none is worth a fourth mechanism.

**The test suite cost was real**: 30 tests asserted Spanish prose and now assert English, since
English is the default locale a test host starts in. That is the honest consequence of §2.6 option
A, not a regression.

## 7. L6 — Sync surface

`SyncPanelView.axaml` (15), `SyncPanelViewModel` (~57), `SyncPairViewModel` (~45),
`SyncFailureViewModel` (~11). Concentrated plural and time-formatting work: "Al día
({time})", "calculado hace {n} días", "{n} operación(es)". Every one of these goes through
`Plural` and `Localizer.Culture` — this phase is the real exercise of both, which is why it comes
after the plumbing is proven on easier surfaces.

Note the standing rule from U6: **`SyncPairViewModel` must not substring-match `LastError`.** U4
introduced that bug and U6 removed it; localization is exactly the change that would reintroduce
it. Behavior reads `FailedCount`, never prose.

## 8. Naming convention for keys

`<surface>.<component>.<role>[.<pluralCategory>]`, lowercase, dot-separated:

```
nav.explorer                     settings.language.label
common.cancel                    settings.language.combo.tooltip
common.retry                     sync.pair.uptodate            → "Up to date ({0})"
error.notauthenticated.title     sync.pairs.count.one/.other
```

Surfaces: `common`, `nav`, `header`, `explorer`, `local`, `viewer`, `sync`, `settings`, `dialog`,
`error`, `console`, `units`. Keys are stable identifiers; **renaming one is a breaking change to
every locale file** and needs the same care as renumbering a plan ID.

## 9. L7 — Service-layer messages → typed reasons

`Services/` currently throws and logs Spanish prose. Do **not** wire `Localizer` into `Services/` —
it would make services depend on UI state and localize strings that also end up in the CLI console
and crash log, where a stable, greppable, English sentence is worth more.

- `DriveException.Message` keeps the provider's own raw sentence, untranslated. The UI shows a
  localized sentence keyed off `Kind` (`error.notauthenticated`, `error.notfound`, …) via a new
  `ViewModels/DriveErrorPresenter`, and offers the raw sentence as a "details" line.
  `DriveErrorKind` already exists and `CliErrorClassifier` already populates it — this phase only
  builds the `Kind` → key table and finds the call sites still reading `.Message` directly.
- `SyncPairValidator` returns a **typed reason enum** instead of a Spanish sentence. Small API
  change, its own commit, its own tests.
- `SyncLogEntry` / `CommandLogBuffer` stay English/raw. The console is a diagnostic surface. Say so
  in a comment so a later sweep does not "fix" it.

### 9.1 What L7 actually drew the line around

§9 as written said "services do not translate", with the provider's raw sentence as the untranslated
detail. Applying it literally would have left a large amount of *our own* Spanish copy inside
`Services/` — the file-kind labels on the filter chips, the sync progress line, the skip
explanations, the inotify degradation notice. None of those is a provider's words; they are the
app's, and they happen to live in a service.

So the boundary is now: **a service must not word an exception**, and must not word anything that
goes to the CLI console or the crash log — those stay English and greppable. A service *may* use
the string table for copy that is only ever presentation. Where the wording depends on a decision
the service made, the service returns the decision and the UI does the wording — which is what
`SyncPairIssue` is.

Concretely, still untranslated on purpose: `CommandLogBuffer`, `SyncLogEntry` and the crash log.

**Superseded, after the fact.** This section originally also excluded every `DriveException` message
the three providers throw, on the grounds that localizing them meant changing the exception
contract. That was parked as [PLAN-TECH-DEBT.md](PLAN-TECH-DEBT.md) B6.5 and then done — see
[§9.2](#92-b65-an-exception-carries-both-sentences). The rule that survives is the *reason* for the
original exclusion, not the exclusion: a sentence bound for the console or the crash log must be
stable English, and a provider's own words are shown verbatim.

`DriveErrorPresenter`'s table is therefore only lightly exercised today — `FormatDriveError` falls
back to it when a provider produced no message at all. It exists because the moment any surface
needs to *lead* with a failure reason rather than quote one (a per-action failure kind on the sync
rows, say), the table is what it needs, and building it per-caller is how the "errors are typed"
rule gets eroded.

The validator tests are the visible payoff: they assert on `SyncPairIssueKind` now, not on a Spanish
sentence, so a copy edit can no longer break a rule check.

### 9.2 B6.5: an exception carries both sentences

The tension §9 was working around is that one string had two readers with opposite requirements.
The console and the crash log want a sentence that is stable, greppable and never moves with the
user's language; the screen wants the user's language. Resolving it by picking one reader was always
going to leave the other badly served — which it did: before this, both got Spanish.

`Services/Localization/ILocalizedError` lets an exception carry both. `Message` stays English;
`Detail` is a `LocalizedText`. `DriveException` and `CliUpdateException` implement it directly;
three thin wrappers (`LocalizedIOException`, `LocalizedFileNotFoundException`,
`LocalizedInvalidOperationException`) cover the handful of sites that throw a framework type.
Subclassing rather than inventing a hierarchy is deliberate: every existing `catch (IOException)`
keeps working, and a test pins that.

The UI reads it through `exception.DescribeForUser()`, which returns the `Detail` when there is one
and the `Message` verbatim when there is not — so a provider's own words are still never
paraphrased. `DriveErrorPresenter`'s kind table remains the last fallback, for a transport that
failed before producing any sentence at all.

**All 62 remaining Spanish literals in `Services/` are gone**, and
`NoSourceFileCarriesASpanishSentence` (L9) stops them coming back. Four `SyncExecutor` guard
clauses were translated to English *without* a key on purpose: they are internal invariants
("a local rename has no destination path"), never advice to a user.

**One thing this does not reach:** `SyncStateStore` persists `ex.Message` as a failed action's
`LastError`, and the failures dialog shows it. That string is English now rather than Spanish, which
is an improvement, but it is stored text — following the language would mean persisting the key and
its arguments. Not worth a schema change for a field that is usually the provider's own words
anyway.



## 10. L8 — Culture-aware formatting, and the invariant-culture audit

Setting `CultureInfo.DefaultThreadCurrentCulture` is what makes dates and numbers render correctly
— **and is the most likely way this plan breaks something unrelated.** Under `es`, `double.Parse`
reads `"1.5"` as `15`, and `ToString()` writes `"1,5"`. Any such call on a path touching CLI stdout,
a Graph payload, a Drive API payload, SQLite, or `settings.json` is now a live bug.

Two parts, and **the audit is not optional**:

1. **Audit.** Grep `Parse(`, `TryParse(`, `ToString(`, `ToString("` across `Services/` and
   `Models/`. Every one on machine data gets an explicit `CultureInfo.InvariantCulture`. Add
   `CA1305`/`CA1304` as build warnings so new occurrences cannot be added silently — this is the
   durable half of the fix; the sweep alone is a snapshot.
2. **Presentation.** `Services/ByteSize`, timestamps in `FolderMetricsViewModel` and
   `SyncPairViewModel`, and the relative-time strings ("hace 1 día") route through
   `Localizer.Culture` and the `units.*` / `time.*` keys. Relative time is plural-sensitive and
   uses `Plural`.

A regression test that runs the existing formatting tests under `es-AR` as
`DefaultThreadCurrentCulture` is the cheapest proof this landed.

### 10.1 What the audit actually found

Seven sites the analyzers flagged, plus two they could not see (both inside interpolated strings).
Each was classified, and the classification was the whole exercise:

**Machine data, now explicitly invariant** — and two of these were live bugs waiting for someone to
pick a language:

- `SyncExecutor.MoveToLocalTrash` names the local trash folder `yyyy-MM-dd`. It is a path. Under a
  culture with a different calendar it changes shape, and yesterday's trash stops being comparable
  with today's — which is what crash recovery walks.
- `AppSettingsService.QuarantineCorruptFile` stamps the quarantined file the same way.
- `SqliteMigrationRunner`'s two `Convert.ToInt32` reads and `SyncStateStore`'s `last_insert_rowid`.

**Presentation, now on `Localizer.Culture`**: `ByteSize.Format` (which had a comment explicitly
justifying invariant — correct before there was a language picker, wrong after), the two panes'
`ModifiedText`, and the properties dialog's timestamp.

The test project needed the same pass: 54 `DateTimeOffset.Parse` calls in fixtures had no explicit
provider, and `CultureHazardTests` moves the process culture, so one of them could have started
failing depending on test order. Fixed rather than suppressed.

**The durable half is the analyzers**, not the sweep — a sweep is a snapshot. `.editorconfig` turns
CA1304/CA1305/CA1310 on as warnings across the repo, and the build is clean, so the next
unqualified `Parse` shows up as a warning rather than as a bug report from a Spanish user.

## 11. L9 — The no-literals lint gate

A test that scans `Views/*.axaml` for `Text=` / `Content=` / `PlaceholderText=` /
`ToolTip.Tip=` / `Header=` / `StringFormat=` with a non-`{` value and fails on anything outside a
small allowlist (provider names, `KB/s`, glyphs, `·`, `—`). Ships **only at the end**, when the
allowlist is short enough to read. A gate with 400 exemptions is decoration.

Companion, cheaper and worth having earlier: a test asserting no locale value is byte-identical
across `en` and `es` **except** on an allowlist of proper nouns — a decent smell test for
untranslated copy pasted between files.

## 12. Verification per phase

Every phase: `./scripts/run-tests.sh` green, plus —

- **L0, L1, L8:** the [`aot-check`](../.claude/skills/aot-check/SKILL.md) skill. L1 adds a
  `[JsonSerializable]` type, L0 adds a markup extension, L8 changes formatting paths; all three are
  exactly what AOT breaks on. Tests run on the JIT host and prove nothing here.
- **L2 onwards:** [`run-app`](../.claude/skills/run-app/SKILL.md) with the stub CLI, switch the
  language, look at the phase's surface in both. A phase with no visual pass is not done —
  PLAN-UX-ROUND-2's follow-up section is the standing evidence for why.
- **Before merge:** [`smoke-test`](../.claude/skills/smoke-test/SKILL.md), run once end-to-end in
  **English**, since it will have been developed against Spanish muscle memory.

## Appendix A — Rejected alternatives

- **`.resx` + `ResourceManager`.** Rejected on AOT/trim grounds (§0.2). Also: live switching needs
  wrapper plumbing anyway, so the tooling win is smaller than it looks; and `.resx` is XML in a
  repo whose serialization is uniformly source-generated JSON.
- **A `LocalizedString` type replacing every `string` on ViewModels.** Type-safe, and a very large
  diff across the binding layer for a benefit §6.3 gets from three classified cases.
- **Nested JSON (`{"nav": {"explorer": …}}`).** Prettier; costs a custom AOT-safe converter or a
  flattening pass, and makes the key-set test recursive. Not worth it.
- **Translating `Services/` too.** Rejected in §9 — the console and crash log want stable English.
- **Machine-translating `en.json` from the existing Spanish copy.** The existing copy is Rioplatense
  and informal ("Arrastrá", "Agregá"); round-tripping it through a translator produces stilted
  English. Write `en.json` by hand from the UI's intent.

## Appendix B — Adding the *next* language

Not in this document, on purpose. The procedure is
[`.claude/skills/add-language/SKILL.md`](../.claude/skills/add-language/SKILL.md), which is the
single source of truth per [AGENTS.md](../AGENTS.md). The two-line summary: drop
`Locales/<code>.json`, add one row to `LanguageCatalog.Available`, run the tests. If it needs more
than that, L1 built the seam wrong.
