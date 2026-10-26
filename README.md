# ShutterSpool

Receive camera media, keep it safe, optionally curate each capture, then deliver the right originals to the configured destinations.

ShutterSpool sits between your camera and the places your photographs should end up.
It takes durable responsibility for accepted originals, lets you choose what to release, and keeps delivery outcomes visible when connections fail.
The spool is temporary storage with recoverable Trash and explicit retention policies; it is not your permanent photo archive.

The current runtime is a modular monolith: six .NET projects and a Nuxt application, developed together with Aspire.
PostgreSQL owns workflow state; the spool keeps original bytes; Valkey and previews are disposable.
Camera FTP/FTPS ingress, local/WebDAV delivery and the responsive web workspace are implemented.
See the guides below for their configuration and compatibility limits.

## Start here

Choose your task from the [Using, Operating and Developing guides](docs/README.md).
For camera setup, start with [camera compatibility](docs/camera-compatibility.md).
For a basic installation, start with the [packaged setup TL;DR](deploy/README.md#tldr).
[Operator instructions](docs/setup/README.md) cover configuration, supported recovery and incompatible older-data transitions. Generated candidates do not imply a published release.

Developers can [understand the architecture](docs/architecture/system-boundaries.md),
[follow a photograph through the code](docs/core-workflow.md), then
[run the development environment](src/ShutterSpool.AppHost/README.md).
The [source map](src/README.md) and [test guide](tests/README.md) locate implementation and checks.

For development setup, checks and contribution guidance, see [CONTRIBUTING.md](CONTRIBUTING.md) and [AGENTS.md](AGENTS.md).

## License

ShutterSpool's original code, documentation and project-created artwork are available under the [MIT License](LICENSE).
[Third-party material and separately fetched components](THIRD-PARTY-NOTICES.md) retain their own terms.
In particular, the optional SFTPGo camera edge uses AGPL-3.0-only with additional upstream terms; MIT does not license the entire combined deployment.
Distribution and hosted operation must satisfy the applicable component and combined-work obligations.

## AI usage

ShutterSpool is a personal hobby project created to solve practical problems I have encountered myself.

AI is used heavily throughout its development. It supports research, design exploration, implementation, debugging, and documentation.

The project is developed using test-driven development and reviewed by a human, me. Its direction, behavior, and quality remain guided by the problems it is intended to solve.
