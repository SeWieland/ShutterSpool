# Contributing to ShutterSpool

Start with the [source map](src/README.md), [architecture](docs/architecture/system-boundaries.md), and [a photograph's journey](docs/core-workflow.md).
The [documentation index](docs/README.md) separates photographer, operator and developer tasks.

## Set up a checkout

Install Git LFS and hydrate the repository assets with `git lfs pull`.
Use the .NET SDK selected by [global.json](global.json), Node selected by [the Web package](src/ShutterSpool.Web/package.json), and Docker with Linux-container support.
Install Aspire CLI **13.6.0** for the documented development and package build commands.
Keep generated artifacts, deployment keys, tokens and media outside Git.

From the repository root:

```sh
dotnet tool restore
dotnet restore ShutterSpool.slnx --locked-mode
dotnet build ShutterSpool.slnx --no-restore
```

Restore, typecheck, test and build the frontend from `src/ShutterSpool.Web` using the [Web instructions](src/ShutterSpool.Web/README.md#build-and-check).
There is no root `package.json`.
Follow [AppHost setup](src/ShutterSpool.AppHost/README.md#start-the-development-environment) to supply persistent development secrets, prepare application data and start the full environment.
Camera edges are opt-in and require their documented ports and certificate setup.

## Make a focused change

Explain the observable problem and intended result. Preserve durable acceptance, original identity, privacy and recovery boundaries.
Follow [code style](docs/style.md) and, for visible changes, the [UI guidance](docs/ui.md).
Add meaningful regression checks for changed behavior and choose the relevant real PostgreSQL/filesystem checks when persistence or concurrency changes.
The [test guide](tests/README.md) explains required dependencies, explicit skips and the limits of synthetic evidence.

If an HTTP contract changes, build Server's OpenAPI output and regenerate the client using the Web guide; verify repeated generation is deterministic.
Update instructions where behavior or commands change, and check Markdown links and anchors.

## Submit a reviewable change

Keep one coherent change per branch and use semantic commit messages, such as `fix: preserve delivery target after retry`.
Describe the concrete behavior, checks performed and material limits in the pull request.
Changes to `main` require a pull request, passing `verify` and `Dependency review` checks against the current base branch, and resolved review conversations.
CodeQL merge protection requires completed analysis and blocks qualifying new high/critical security findings or errors in changed code. GitHub exempts Dependabot PRs analyzed by default setup from this gate; `verify` and `Dependency review` remain required.
Force-pushing or deleting `main` is blocked. There is no required approval count while the project has one maintainer; this does not replace the review expectations below.
Workflows from external contributors require maintainer approval before running. Keep actions pinned to full commit SHAs and request only the token permissions each job needs.
Substantive changes need review of behavior, code quality and documentation; visible changes also need desktop/phone and keyboard inspection.
Use synthetic photographs and controlled endpoints in review evidence.
Do not publish packages, deploy, modify production data or contact external recipients as part of an ordinary contribution.

The [release conventions](docs/release-policy.md) describe shared versions, artifact identity, compatibility and published notes.
Report vulnerabilities using the [security policy](SECURITY.md), rather than a public issue.

## Contribution rights

Submit only material you have the right to contribute.
Your original contributions are provided under the project's [MIT License](LICENSE).
Identify any third-party material and its source, preserve its license and notices, and explain any redistribution requirements.
Do not submit credentials, private media or material with uncertain provenance.
