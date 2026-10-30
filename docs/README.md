# Documentation

Choose the guide for the task you are doing. These pages describe the implemented runtime; installation and compatibility limits remain explicit.

## Using ShutterSpool

- [Camera setup and compatibility](camera-compatibility.md): connect a camera, send a first photograph and understand tested hardware limits.
- [Collections](collections.md): prepare an event, choose incoming defaults, move photographs and use Collection names in delivery folders.
- [Gallery slideshow](gallery.md): play filtered photographs and resume after interruptions.
- [Retention and recovery](retention.md): understand lifetimes, Favorites and Trash before removing photographs.

### Choose a workspace

On a shared installation, sign in, choose a workspace assigned to your account and select **Open workspace**.
Use the header's **Workspace** selector to switch. Resolve or explicitly abandon unsaved edits when prompted; switching reloads the page.
Each tab keeps its selected workspace in its URL, so you can use different workspaces in separate tabs. A shared URL grants no access.

If some memberships could not be checked, you can explicitly open a listed, confirmed workspace or choose **Retry** to check again.
An unavailable or unconfirmed URL selection never opens another workspace automatically.
If **No workspace access** appears after a complete check, contact the operator if you expected access.

## Operating ShutterSpool

- [Packaged installation](../deploy/README.md#tldr): configure the bundled single-host installation or select external dependencies.
- [Operator setup](setup/README.md): installation navigation and the incompatible older-data transition.
- [Shared workspace provisioning](../deploy/operations/README.md#shared-workspace-provisioning): separate databases, fixed storage allocations, explicit setup and upgrades on one Server.
- [Certificates](../deploy/certificates/README.md): supply, validate and replace HTTPS/FTPS material.
- [Maintenance and recovery](../deploy/operations/README.md): stop writers, preserve matching state, restore, upgrade and roll back.
- [Storage policy](retention.md#settings-and-safe-activation): capacity, admission, safe activation and privacy-erasure limits.

A locally built package is not a published release. Inspect its manifest and version-specific compatibility information before installation.

## Developing ShutterSpool

Start with [contributing](../CONTRIBUTING.md), then choose a reference:

| Guide | What you will learn |
| --- | --- |
| [System boundaries](architecture/system-boundaries.md) | Ownership, dependency direction and extension recipes. |
| [A photograph's journey](core-workflow.md) | Acceptance, grouping, curation, routing, delivery and recovery. |
| [Authentication and authorization](architecture/authentication-and-authorization.md) | Management access, camera credentials and secret custody. |
| [Persistence](architecture/persistence.md) | Transaction boundaries, lock order and recovery constraints. |
| [Source map](../src/README.md) | Component responsibilities and runnable guides. |
| [AppHost](../src/ShutterSpool.AppHost/README.md) | Development resources, persistent secrets and startup. |
| [Tests](../tests/README.md) | .NET, frontend and public script checks, dependencies and evidence limits. |
| [Server](../src/ShutterSpool.Server/README.md) | HTTP behavior, configuration and limits. |
| [Infrastructure](../src/ShutterSpool.Infrastructure/README.md) | Spool, providers, persistence and restore details. |
| [Style](style.md), [logging](logging.md), [UI guidance](ui.md) | Contribution conventions. |
| [Product context](../PRODUCT.md), [visual system](../DESIGN.md) | Purpose and implemented interface language. |
| [Release conventions](release-policy.md) | Shared version, immutable artifact identity, compatibility and published notes. |

[AGENTS.md](../AGENTS.md) gives coding agents the same public safety and contribution boundaries.
