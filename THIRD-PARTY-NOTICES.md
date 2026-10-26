# Third-party notices

ShutterSpool's [MIT License](LICENSE) covers its original code, documentation and project-created artwork.
Third-party material retains its own license; the project license does not replace those terms.
Package manifests and lockfiles identify dependencies fetched during a build. They do not include those dependencies' source or binaries in this repository.
Redistributing a built application or container requires preserving the notices and source obligations for the components actually included.

## Manrope font

Copyright 2018 The Manrope Project Authors.
The unmodified Manrope font is distributed under the SIL Open Font License 1.1.
The complete copyright and license accompany both the [web font](src/ShutterSpool.Web/public/Manrope-OFL.txt)
and the [sign-in theme font](src/ShutterSpool.AppHost/Themes/shutterspool/login/resources/fonts/Manrope-OFL.txt).
See the [asset guide](assets/workspace/README.md) for provenance.

## SFTPGo camera ingress

ShutterSpool uses the separate, unmodified SFTPGo Community container for camera FTP/FTPS ingress.
SFTPGo is developed by the SFTPGo authors; this project is not endorsed by or affiliated with them.
The [AppHost image pin](src/ShutterSpool.AppHost/Program.cs) selects the configured container.
The links below identify its matching source and terms.

- [Matching SFTPGo source](https://github.com/drakkan/sftpgo/tree/9888a3d169aed9011ae6e4f7a97ae735c1643068)
- [GNU AGPL version 3 license](https://github.com/drakkan/sftpgo/blob/9888a3d169aed9011ae6e4f7a97ae735c1643068/LICENSE), identified by upstream as `AGPL-3.0-only`
- [Additional attribution and source-access terms](https://github.com/drakkan/sftpgo/blob/9888a3d169aed9011ae6e4f7a97ae735c1643068/NOTICE)
- [Upstream licensing and trademark policy](https://sftpgo.com/licensing)

The repository configures this image; it does not vendor SFTPGo or its proprietary WebAdmin/WebClient theme.
The configured HTTP administration interface is disabled. See the [camera-edge guide](src/ShutterSpool.AppHost/README.md#camera-ingress) for integration details.
Use, distribution and hosting must satisfy SFTPGo's applicable license, notice and trademark terms, including attribution and access to matching source.
When updating the image pin, verify its source revision from the image metadata and update these source/license links to match.
The MIT grant for ShutterSpool does not establish that the integrated deployment is an independent aggregate, waive any applicable AGPL obligations, or grant commercial rights to SFTPGo's trademarks.
Review those conditions before distributing the integrated product or offering it as a paid service; this source repository is not a compliance certification for either use.
