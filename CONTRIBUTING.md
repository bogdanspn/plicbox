# Contributing to Plicbox

Thanks for wanting to help. Bug reports and ideas are welcome as issues; for code, open an issue first for anything
bigger than a small fix, so we can agree on the approach before you spend time on it.

## What you agree to when you contribute

By sending a contribution (a pull request, a patch, or code in an issue) you confirm that:

1. It's your own work, or you have the right to submit it, and it doesn't include code under a licence that isn't
   compatible with GPL-3.0.
2. You license it to everyone under the GNU General Public License, version 3, with the additional terms in
   [NOTICE.md](NOTICE.md), like the rest of Plicbox.
3. You also give Cristian Bogdan Rosu, the author of Plicbox, the right to use, change and relicense your contribution
   under other terms as well (for example a later GPL version, or another licence for a future edition of Plicbox).
   You keep the copyright in your own work.

Point 3 keeps Plicbox under one owner's control of its licence, so it can still change if that's ever needed.

## Before you send it

- `.\run.ps1 -Test` passes, and the app runs (`.\run.ps1 -Demo` is enough for most changes).
- New text the app shows goes through `L.T(...)` / `{local:T '...'}`, with translations in `src/Plicbox.Core/Languages`
  (the localization tests list anything missing).
- A new NuGet package: run `licenses\update-notices.ps1` and include the updated `THIRD-PARTY-NOTICES.txt`.
