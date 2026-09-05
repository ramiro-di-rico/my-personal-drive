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

> **L0-L2 implemented on branch `feature/i18n`, 2026-09-05**, from `main` at `14413d8`. 1053 tests
> passing (from 1001). The AOT publish is clean — only the five warnings that predate this work —
> and both locales are verifiably embedded in the single-file binary. **Not visually verified: this
> environment has no working screenshot tool** (GNOME refuses `ScreenshotWindow` over D-Bus), so
> the Settings layout with the picker, and the language actually changing on screen, are both
> unconfirmed by eye and want a human pass. The counts in §0.1 were taken on `feature/ux-round-2`
> and are slightly low against the merged `main`, which added the per-pane toolbars.

- [x] **L0 — Live-switch spike (partial).** The markup half is proven; the ViewModel half is wired
      but unobserved. See [§3](#3-l0--the-live-switch-spike) for what was and was not shown.
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
- [ ] **L3 — Shell and Explorer markup.** Not started.
- [ ] **L4 — Code-built dialogs (`MainWindow.axaml.cs`).** Not started.
- [ ] **L5 — `MainWindowViewModel`.** Not started.
- [ ] **L6 — Sync surface.** Not started.
- [ ] **L7 — Service-layer messages → typed reasons.** Not started.
- [ ] **L8 — Culture-aware formatting, and the invariant-culture audit.** Not started.
- [ ] **L9 — The no-literals lint gate.** Not started.

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
