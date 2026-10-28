# Third-party notices

ShutterSpool's [MIT License](LICENSE) covers its original code, documentation and project-created artwork.
Third-party material retains its own license; the project license does not replace those terms.
The source repository's package manifests and lockfiles identify dependencies fetched during a build.
Redistributing a built application or container requires preserving the notices and source obligations for the components actually included.

## Manrope font

Copyright 2018 The Manrope Project Authors.
The unmodified Manrope font is distributed under the SIL Open Font License 1.1.
The complete copyright and license accompany both the [web font](https://github.com/SeWieland/ShutterSpool/blob/main/src/ShutterSpool.Web/public/Manrope-OFL.txt)
and the [sign-in theme font](https://github.com/SeWieland/ShutterSpool/blob/main/src/ShutterSpool.AppHost/Themes/shutterspool/login/resources/fonts/Manrope-OFL.txt).
In release artifacts these are `/app/wwwroot/Manrope-OFL.txt` in the Server image and `package/theme/login/resources/fonts/Manrope-OFL.txt` in the Compose ZIP.
See the [asset guide](https://github.com/SeWieland/ShutterSpool/blob/main/assets/workspace/README.md) for provenance.

## SFTPGo camera ingress

ShutterSpool uses the separate, unmodified SFTPGo Community container for camera FTP/FTPS ingress.
SFTPGo is developed by Nicola Murino and contributors. ShutterSpool is not endorsed by or affiliated with them.
The [canonical image pin](https://github.com/SeWieland/ShutterSpool/blob/main/deploy/images/compose.yaml) selects the container.

<!-- BEGIN GENERATED SFTPGo ATTRIBUTION -->
Builds replace this paragraph with the selected image's version and matching source, LICENSE and NOTICE links.
Run `node tools/generate-attribution.mjs` from the source root to inspect `artifacts/attribution/sftpgo.json` and the generated notices.
<!-- END GENERATED SFTPGo ATTRIBUTION -->

For upstream's current development sources and terms, see the [SFTPGo repository](https://github.com/drakkan/sftpgo),
[AGPL license](https://github.com/drakkan/sftpgo/blob/main/LICENSE), [additional terms](https://github.com/drakkan/sftpgo/blob/main/NOTICE)
and [licensing/trademark policy](https://sftpgo.com/licensing). Released artifacts provide revision-specific links in the section above and in the app's **About & licenses** dialog.

The repository configures this image; it does not vendor SFTPGo or its proprietary WebAdmin/WebClient theme.
The configured HTTP administration interface is disabled. See the [camera-edge guide](https://github.com/SeWieland/ShutterSpool/blob/main/src/ShutterSpool.AppHost/README.md#camera-ingress) for integration details.
Use, distribution and hosting must satisfy SFTPGo's applicable license, notice and trademark terms, including attribution and access to matching source.
The MIT grant for ShutterSpool does not establish that the integrated deployment is an independent aggregate, waive any applicable AGPL obligations, or grant commercial rights to SFTPGo's trademarks.
Review those conditions before distributing the integrated product or offering it as a paid service; this source repository is not a compliance certification for either use.
