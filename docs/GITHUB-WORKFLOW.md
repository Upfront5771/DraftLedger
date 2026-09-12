# GitHub publishing workflow

## Connect DraftLedger to ChatGPT

1. Install and connect the GitHub integration in ChatGPT.
2. Sign in to the GitHub account that owns `Upfront5771/DraftLedger`.
3. Grant the integration access to that repository. Repository contents must be writable if you want ChatGPT to publish changes.
4. Return to the DraftLedger conversation and ask to commit and push the prepared changes.

Do not paste a GitHub password or personal access token into the conversation, source files, or repository secrets.

## Future changes

For a direct update, ask:

```text
Implement this DraftLedger change, run the tests, commit it, and push it to main.
```

For a safer review workflow, ask:

```text
Implement this DraftLedger change, run the tests, push it to a feature branch, and open a pull request.
```

Feature branches and pull requests are recommended for larger changes. They keep `main` stable and make it easier to review or undo a change.

## Automated builds

`.github/workflows/build.yml` runs on every push and pull request targeting `main`. The Windows runner:

1. Installs the .NET 10 SDK.
2. Runs the cross-platform tests.
3. Runs the native WPF regression tests.
4. Publishes the self-contained Windows application.
5. Stores `DraftLedger-win-x64.zip` as a workflow artifact for 14 days.

## Versioned releases

The root `VERSION` file controls release publication. When a commit changes `VERSION` on `main`, `.github/workflows/release.yml`:

1. Confirms that `VERSION`, the application project version, and release notes agree.
2. Runs the full Windows build and tests.
3. Creates a matching version tag.
4. Creates the GitHub Release and marks it latest.
5. Attaches `DraftLedger-win-x64.zip`.

For the next release, update all three version references, commit the changes, and push:

- `VERSION`
- `src/DraftLedger.App/DraftLedger.App.csproj`
- `docs/RELEASE-NOTES.md`

The workflow refuses to replace an existing release. Increment the version for every published build.

## Screenshot maintenance

Repository image placeholders are under `docs/screenshots`. Replace them with current screenshots and keep the filenames, or update the root README image links. Review screenshots carefully for API keys, provider account information, personal paths, and manuscript text before committing.
