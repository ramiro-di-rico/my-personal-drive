---
name: add-language
description: Add a new interface language to the app, or fill in missing translations for an existing one. Use when the UI needs to ship in another language, when a locale file has fallen behind English, or when new UI strings were added and every locale needs the new keys.
---

# Add an interface language

The localization seam is specified in [docs/PLAN-I18N.md](../../../docs/PLAN-I18N.md). Read §1
(target shape) and §8 (key naming) before starting. **English (`en`) is the reference locale**:
every key exists there first, and every other locale is measured against it.

If the seam does not exist yet (no `Services/Localization/`), this skill does not apply — that is
PLAN-I18N.md phase L1, and it is a design task, not this procedure.

## The whole change, if the seam is intact

1. `src/MyPersonalDrive/Services/Localization/Locales/<code>.json`
2. One row in `LanguageCatalog.Available`
3. `./scripts/run-tests.sh`

No `.csproj` edit (the locale files are globbed as `EmbeddedResource`), no code path, no wiring.
**If you find yourself editing anything else, stop and say so** — either the seam has a gap worth
fixing properly, or the language needs something the seam does not cover (see "When it is not just
strings" below).

One thing legitimately outside those three steps: a gate written when only two languages existed may
need widening to keep covering the new one. Adding Italian needed exactly that (docs/PLAN-I18N.md,
Appendix B). Widening a gate's *coverage* is fine; weakening its *assertion* to make your locale
pass is the anti-pattern.

## Steps

### 1. Pick the code

BCP-47, lowercase, no region unless two variants genuinely ship at once (`pt`, not `pt-BR`, unless
`pt-PT` also exists). It becomes the filename, the `AppSettings.Language` value, and the
`CultureInfo` name — so it must be a culture .NET recognises. Verify:

```bash
dotnet run --project src/MyPersonalDrive -- --version >/dev/null 2>&1; \
  echo 'System.Globalization.CultureInfo.GetCultureInfo("pt")' # sanity-check the code exists
```

### 2. Create the locale file from English

Copy `en.json` so the key set starts correct and stays ordered, then translate the values:

```bash
cp src/MyPersonalDrive/Services/Localization/Locales/en.json \
   src/MyPersonalDrive/Services/Localization/Locales/<code>.json
```

Rules for the values:

- **Never touch the keys.** Only the right-hand side changes.
- **Preserve every placeholder.** `{0}`, `{1}` must all survive, and mean the same thing. Their
  *order* may change if the target language needs it — `string.Format` is positional, so
  `"{1} in {0}"` is fine. A dropped placeholder is a test failure; a swapped meaning is a silent bug.
- **Translate the intent, not the words.** `common.cancel` is whatever a native speaker's Cancel
  button says in that language's convention.
- **Plurals.** Every `*.one` key has a `*.other` sibling. If the language needs more categories
  (Polish, Russian, Arabic), add `*.few` / `*.many` / `*.zero` **and** supply a `PluralCategory`
  delegate in step 3 — the default one only ever returns `one`/`other`, so extra keys would be
  dead. Follow [CLDR plural rules](https://cldr.unicode.org/index/cldr-spec/plural-rules).
- **Keep the register consistent.** The Spanish copy is informal Rioplatense ("Arrastrá",
  "Agregá"). Pick a register for the new language and hold it across all ~750 strings; do not mix
  formal and informal address.
- **Do not translate proper nouns**: `Proton Drive`, `OneDrive`, `Google Drive`, `Nextcloud`,
  `Custom S3`, `proton-drive`, `KB/s`.
- **Length.** German and French run ~30% longer than English; Italian ran 24%. But **compare
  against the longest language that already ships, not against English** — Italian came out only
  4% longer than the Spanish already rendering fine, which is what made its layout risk small. A
  quick way to get that number before looking at anything:

  ```bash
  python3 -c "
  import json; d='src/MyPersonalDrive/Services/Localization/Locales/'
  a=json.load(open(d+'es.json')); b=json.load(open(d+'<code>.json'))
  print(sum(len(b[k])/max(1,len(a[k])) for k in a)/len(a))
  for k in sorted(a, key=lambda k: len(a[k])-len(b[k]))[:15]:
      print(f'{k}\n  es: {a[k]}\n  new: {b[k]}')"
  ```

  Long labels wrap or clip in a fixed-width column; that is a real finding, not a nitpick — note
  it in step 6.

### 3. Register it

```csharp
// LanguageCatalog.Available
new("pt", "Portuguese", "Português"),
```

`NativeName` is what the Settings picker shows — a speaker scans for their own language's name for
itself. Add a `PluralCategory` delegate here only if step 2 needed extra plural categories.

### 4. Run the tests

```bash
./scripts/run-tests.sh
```

The localization tests are the safety net and they are specific:

| Failure | What it means |
|---|---|
| `EveryLanguageCodeIsACultureDotNetKnows` | The code is not a culture .NET recognises. It would fall back to `InvariantCulture` silently, so the strings would switch while dates and numbers did not. |
| `EveryLocaleHasTheSameKeysAsEnglish` | A key is missing or extra. The message names them. |
| `PlaceholderSetsMatchEnglishPerKey` | A `{0}` was dropped or invented. Would crash at runtime. |
| `NoLocaleValueIsEmptyOrWhitespace` | An untranslated value was blanked instead of copied. |
| `EveryPluralKeyHasBothOneAndOther` | A `.one` without its `.other`. |
| `ALocaleOnlyMatchesEnglishWhereItShould` | A value is byte-identical to English — usually copied rather than translated. Two escape hatches, and picking the right one matters: `LanguageNeutral` for a value no language translates (a `"{0}: {1}"` template, a unit, a proper noun), and `BorrowsTheEnglishWord` for a key where *this* language legitimately uses the English word. Italian's `common.file` is the second kind — "File" is correct Italian — and putting it in the first list would stop the gate catching an untranslated Spanish "File". |
| `StringKeysConstantsAreExactlyTheEnglishKeySet` | You added a key to `en.json` with no constant, or a constant with no key. This one only fires when adding *new* strings, not when adding a locale. |

Fix the file, not the test.

Two more gates exist and should not fire for a locale-only change, but will tell you immediately if
a translation attempt strayed into the markup: `NoMarkupCarriesALiteralUserFacingString` and
`NoBindingCarriesALiteralStringFormat`. If either fires, you edited a `.axaml` — don't; the value
belongs in the JSON.

### 5. Verify it actually publishes

```bash
./scripts/publish-linux.sh
```

The locale files are embedded resources under `PublishAot=true`. The glob should pick the new file
up with no change — **confirm it did** by checking the published binary starts and the language
appears in the picker. A locale that works under `dotnet run` and is missing from the AOT binary is
the exact failure mode [`aot-check`](../aot-check/SKILL.md) exists for.

### 6. Look at it

Use [`run-app`](../run-app/SKILL.md) with the stub CLI, switch to the new language in Settings, and
walk the three views (Explorer, Viewer, Sync) plus Settings and one dialog. You are looking for:

- clipped or wrapped labels in fixed-width columns and buttons
- sentences that read as machine translation
- anything still in English that should not be (a key you missed at the call site, not in the file)
- dates and numbers in the right format — that comes from `Localizer.Culture`, so it is a real check

Record what you saw. PLAN-UX-ROUND-2 shipped ten unlooked-at layout changes and says so; do not add
to that pile.

### 7. Document it

- Tick or extend the language list in `docs/PLAN-I18N.md`.
- If `docs/ARCHITECTURE.md` enumerates the supported languages, update it and its commit header.
- Commit with the [`commit`](../commit/SKILL.md) skill — **no AI co-author trailer in this repo.**

## Filling in a locale that has fallen behind

Same thing, smaller. When new UI strings land, `en.json` grows and every other locale fails
`EveryLocaleHasTheSameKeysAsEnglish`. The failure message names the missing keys — add exactly
those, in their sorted position, then steps 4–7. **Do not add a key to a locale without translating
it**: the loader already falls back to English for a missing key, so a missing key degrades
gracefully while a key whose value is still English is invisible and permanent.

## When it is not just strings

Some languages need more than a JSON file. Say so up front rather than shipping half of it:

- **RTL (Arabic, Hebrew, Farsi).** Needs `FlowDirection`, mirrored chevrons and icons, and a pass
  over every hand-positioned element. Explicitly out of scope in PLAN-I18N.md §0.4 — it is a layout
  project. Do not start it as part of adding a locale file.
- **Complex plurals.** Handled above, but it is a code change (the delegate), not just data.
- **Scripts needing a different font.** The app bundles Inter (`Avalonia.Fonts.Inter`), which has no
  CJK, Devanagari, Thai or Arabic coverage. A CJK locale needs a font decision, and a bundled CJK
  font is several MB against an AOT binary — a real packaging tradeoff, worth raising before the
  translation work, not after.
- **Sorting.** `DriveItemSorter` sorts names. A locale with its own collation (Swedish å/ä/ö,
  Turkish dotted/dotless i) will sort differently once `CurrentCulture` changes. Usually the desired
  behavior — but check it deliberately, and check nothing that parses machine data got swept along
  (PLAN-I18N.md §10). The CA1304/CA1305 warnings in `.editorconfig` are what keep that split
  honest; a build that starts emitting them is telling you a formatting site lost its explicit
  provider.
- **Turkish specifically.** `tr-TR`'s dotless-i breaks `ToUpper()`/`ToLower()` round-trips on ASCII
  identifiers. Everything in this repo that case-folds machine data does so with an explicit
  `StringComparison`, and CA1310 keeps it that way — but this is the locale that finds any site
  that slipped through.

## Anti-patterns

- Machine-translating the whole file and shipping it unread.
- Adding the language to `LanguageCatalog` before the locale file is complete — it appears in the
  picker and half the UI falls back to English.
- Changing a key because the translation "reads better" under a different name. Keys are stable
  identifiers; renaming one is a change to every locale file at once.
- Translating `Services/` messages, the CLI console, or the crash log. Those stay English by
  decision (PLAN-I18N.md §9).
- Hardcoding the new language anywhere outside `LanguageCatalog` — no `if (code == "pt")`.
