# Releasing

Releases are cut on GitHub. Publishing a release runs
[release.yml](../.github/workflows/release.yml), which tests and packs the
plugin, uploads `TMDbFileMatcher-<tag>-any.zip` to the release, and records
it, with its SHA-256 checksum and sizes, in `manifest.json` on the
`metadata` branch. That file is the repository feed Shoko reads. The
`manifest.json` on `stable` is a stub for local builds, and is never
published.

No secrets or variables are needed: the workflow uses the run's own
`GITHUB_TOKEN`, and takes the server and repository names from the run.

## Once, to set the repository up

1. Create the repository on GitHub, empty: no README, licence or
   `.gitignore`, so the first push is this history.
2. Push `stable` first, so it becomes the default branch:

   ```sh
   git remote add origin git@github.com:<owner>/<repo>.git
   git push -u origin stable
   ```

   The test workflow runs on that push.
3. Seed the `metadata` branch with the manifest below, on a branch with no
   history of its own:

   ```sh
   git worktree add --orphan -b metadata ../tmdb-file-matcher-metadata
   cd ../tmdb-file-matcher-metadata
   # write manifest.json as below, with <owner>/<repo> filled in
   git add manifest.json
   git commit -m "chore: seed the published manifest"
   git push -u origin metadata
   cd - && git worktree remove ../tmdb-file-matcher-metadata
   ```

   The release workflow stops before building anything when this branch is
   missing, rather than publishing a release against the stub.
4. Put the feed's address in the README's Install section:
   `https://raw.githubusercontent.com/<owner>/<repo>/metadata/manifest.json`.
5. Leave `metadata` unprotected, or let GitHub Actions push to it: the
   release workflow commits the manifest there.

The seed manifest. `shoko-build` refuses a manifest whose `id` is not the
plugin's, and rewrites the name, overview, namespace and tags from the
project on every release:

```json
{
  "$schema": "https://raw.githubusercontent.com/ShokoAnime/ShokoServer/master/Shoko.BuildTools/manifest.schema.json",
  "id": "69303ecc-c9b5-431c-94e5-755161badf5d",
  "name": "TMDb File Matcher",
  "overview": "Matches files AniDB does not know to TMDb shows, episodes and movies, by the TMDb IDs tagged in their paths or by name and year, and validates the releases that link to TMDb.",
  "namespace": "tmdb-file-matcher",
  "authors": "revam",
  "repository_url": "https://github.com/<owner>/<repo>",
  "releases": []
}
```

## Each release

1. Set `<Version>` in `source/Shoko.Plugin.TmdbFileMatcher.csproj` and
   commit it on `stable`. The workflow refuses a tag that is not
   `v<Version>`.
2. Publish a release whose tag is `v<Version>`, on `stable`, with the
   release notes in its body:

   ```sh
   gh release create v0.1.0 --target stable --title v0.1.0 --notes-file notes.md
   ```

3. When the workflow is done, the release has its archive and the feed has
   the release. A failed run can be re-run: it replaces the archive and
   rewrites the release's entry.
