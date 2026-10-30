# Operator setup documentation

This guide is for the person running the installation. Camera users should start with [camera setup and compatibility](../camera-compatibility.md).

## Packaged installation

The [package guide](../../deploy/README.md) owns packaged installation, certificate lifecycle and maintenance commands.
Start with its [bundled installation TL;DR](../../deploy/README.md#tldr) for the shortest setup path.
Inspect the package manifest for exact artifact identities and its declared compatibility profile. A locally built package is not a published release.
Keep development commands in the owning component guide and camera instructions in [camera compatibility](../camera-compatibility.md).
The compatibility matrix distinguishes observed physical-camera uploads from manufacturer-documented support.

## Shared workspaces

For multiple workspaces on one Server, follow [shared workspace provisioning](../../deploy/operations/README.md#shared-workspace-provisioning).
Use fresh tenant databases and fixed filesystem allocations; the setup does not convert or adopt existing single-workspace media.

## Moving from an older pre-alpha installation

Follow the canonical [pre-alpha transition procedure in the packaged maintenance guide](../../deploy/operations/README.md#moving-from-an-older-pre-alpha-installation) for an incompatible older installation.
That procedure is separate from upgrades between explicitly compatible package versions.
Contributor setup is in [CONTRIBUTING.md](../../CONTRIBUTING.md).
