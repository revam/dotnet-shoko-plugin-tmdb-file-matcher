# TMDb File Matcher

A Shoko plugin that matches files AniDB does not know to **TMDb** shows,
episodes and movies: by the TMDb IDs tagged in their paths, or by name and
year. It hands core a release whose cross-references name the TMDb entries,
and it ships the validator that lets any release link to TMDb.

It is small on purpose, and meant to be read: a plain plugin showing a
release provider with non-AniDB cross-references, a cross-reference
validator with healing, and a required and an optional plugin dependency.
[docs/how-it-works.md](docs/how-it-works.md) walks through the code.

## What it does

- **Tags first.** `[tmdbid-tv12345]`, `[tmdb-e1234501003]`, `[tmdbid-m372058]`
  or a bare `[tmdbid-12345]`, in the file name or a folder above it. A tag that
  does not resolve stops the match rather than guess.
- **Then name and year.** The series name and year from the path are searched
  on TMDb, shows and movies, the likelier first, and every hit is scored on
  its title, its year and whether it can hold the file.
- **Then placement.** A show is fetched into Shoko once, with a bounded quick
  refresh, and the file placed by `SxxEyy`, as season 1, by absolute
  numbering across the seasons, or as a special in season 0. A movie is
  linked as it is.
- **Unsure matches defer.** A match below the accept score, or one that does
  not lead the next by 10, is saved provisionally, with `DeferToNext` set so
  the next release provider still gets its turn.
- **Manual by default.** Files are matched when you ask. Automatic matching
  needs the Offline Importer and at least one of its auto-match rules.

## The two dependencies

- **TMDB, required (`>=1.3.0`).** It is what files are matched to: the
  searches, lookups and refreshes all go through it, by way of core's
  metadata services.
- **Offline Importer, optional (`^2.0.0`).** When it is installed, its parsing
  rules read the paths and its engine judges the auto-match rules. Without it,
  a small built-in parser reads the common shapes,
  `Name (Year)/Season N/Name - SxxEyy.mkv`, `Name - 05.mkv` and TMDb tags, and
  files are matched only when asked. Its abstractions assembly ships with this
  plugin, so its types load either way.

## Install

Add this repository's manifest to Shoko's plugin repositories, then install
**TMDb File Matcher** from the plugin list. Shoko installs the TMDB plugin
it needs from its core repository when it is missing, and it must be
enabled.

<!-- TODO: the manifest URL, once the GitHub repository exists: https://raw.githubusercontent.com/<owner>/<repo>/metadata/manifest.json -->

## Settings

| Setting | Default | What it does |
|---|---|---|
| Accept score | 80 | The score, out of 100, a search hit needs to be accepted, leading the next by 10. |
| Provisional score | 60 | The score a hit needs to be saved provisionally. Set it to the accept score to save only accepted matches. |
| Include restricted entries | off | Whether restricted TMDb entries may match. |
| Auto-match rules | none | The Offline Importer's rules that allow automatic matching. With none, or without the importer, files are matched only when asked. |

## Limits

- **Episodes, specials and movies only.** Credits, trailers and other kinds
  of file are not matched, nor is an episode number that is not whole, such
  as 12.5.
- **A tag is final.** A TMDb tag that does not resolve ends the match with
  nothing; the name is not searched instead.
- **A show is fetched before a file is placed in it.** The first file of a
  show not yet in Shoko waits up to 30 seconds for a quick refresh of it.
- **No TMDb, no match.** While TMDb cannot be reached, or the TMDB plugin is
  paused, files get no match. A file that another provider matched in the
  meantime is tried again later; one that is left with none waits for its
  next search.
- **Searches cost requests.** A file can take a few searches, and looking up
  the likeliest hits, before it is matched. They go through the TMDB plugin,
  whose own limits apply, and are kept for an hour, so the files of one
  folder search once.
- **No lookup by release ID.** Releases are read from a file's path, so
  there is no ID to look one up by.

## Building

```sh
mise install
dotnet build Shoko.Plugin.TmdbFileMatcher.slnx --no-incremental
dotnet test tests/Shoko.Plugin.TmdbFileMatcher.Tests.csproj
```

Releases are packed by `shoko-build` in CI when a GitHub release is
published, and recorded in the manifest on the `metadata` branch; see
[docs/releasing.md](docs/releasing.md).

## License

[MIT](LICENSE).
