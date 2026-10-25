# Camera compatibility

Start with **standard FTPS** for encrypted camera uploads.
**Check the manufacturer's latest firmware before switching to compatibility mode.** Firmware updates can improve FTPS support.

## Matrix

| Camera | FTPS | Setup notes |
| --- | --- | --- |
| Sony α7C / ILCE-7C | ✅ Confirmed | Standard FTPS on firmware **2.03**. Firmware **2.00** needs compatibility mode. |
| Sony α7 IV / ILCE-7M4 | 🔵 Documented | [Sony setup guide](https://helpguide.sony.net/di/ftp_2110/v1/en/print.pdf) |
| Canon EOS R6 Mark II | 🔵 Documented | [Canon setup guide](https://cam.start.canon/en/C012/manual/html/UG-07_Network_0070.html) |
| Nikon Z6III | 🔵 Documented | [Nikon setup guide](https://onlinemanual.nikonimglib.com/z6III/en/ftp_servers_connecting_via_wireless_lan_95.html) |

**Confirmed** means a successful camera upload to ShutterSpool. **Documented** means the manufacturer describes FTPS support; we haven't tested that model yet.
This is a shortlist, not an exhaustive list of supported cameras.

Use **compatibility mode** only if your camera still needs older encryption after checking the latest applicable firmware and trying standard FTPS.
Firmware updating remains an operator decision, not a ShutterSpool setup requirement.
Both modes use the camera's secure FTP setting (FTPS/FTPES) and validate the server certificate.
Compatibility mode allows weaker encryption without forward secrecy; your operator must enable it explicitly.

Unencrypted FTP is available for trusted local networks. Camera-facing SFTP is not currently available.

<details>
<summary>Technical test records</summary>

The α7C was tested over the LAN: standard FTPS worked on firmware 2.03, and compatibility mode worked on 2.00.
These are LAN observations for those firmware/profile combinations, not public-network, power-loss or universal firmware qualification.
Unencrypted FTP was also exercised with the α7C; it provides no credential or media confidentiality.

</details>

## Camera setup

1. Obtain the server hostname, control port and Source username/password from your ShutterSpool operator.
   Use the supplied hostname for certificate validation. Sources offers generated or custom credentials; save generated passwords when shown and keep them private.
   Connection settings provides copy buttons and credential replacement. Update every sharing device after replacement; passwords cannot be read back.
2. Enable passive mode and secure FTP (FTPS/FTPES), keeping certificate validation enabled.
   In **Sources → settings → Security**, select **FTPS — recommended** or, only when required, **FTPS — compatibility (legacy)**.
   The operator must enable that deployment profile; credentials work only on the selected profile. Profile edits require reconnecting.
3. In the Security tab, use **Download CA root**, verify its identity/fingerprint and import it only if your camera requires it; follow the instructions below.
   Missing/invalid CA information needs an operator setup correction; keep certificate validation enabled.
4. Send one selected picture, then check that it appears in ShutterSpool before enabling automatic or batch transfer.

For server configuration or an older pre-alpha installation, see [operator setup](setup/README.md).
Contributors configuring the local server should use [development FTPS setup](../src/ShutterSpool.AppHost/CameraFtps.md).

A Source is a logical incoming connection; several devices may share its credentials and Source policy.
Separate Sources per device are recommended for independent revocation, routing and troubleshooting.
Revoking/regenerating a shared credential affects all sharing devices; sharing does not establish camera or Capture identity.
General settings hold name, type, enabled state and curation. Connection shows endpoint/credentials and setup instructions;
Security holds the profile, warnings and CA material. Unavailable deployment profiles retain their saved settings and require an operator deployment correction.
Successful login prepares the virtual root automatically; use `/` without manual directory creation or a seed upload.
Authentication feedback is distinct from accepted ingest and delivery; check credentials, certificate trust and endpoint availability before considering compatibility.

**Unencrypted FTP** sends credentials and photographs unencrypted. Select it explicitly only on a trusted local network.
The compatibility profile remains encrypted but has weaker protection and lacks forward secrecy;
a later server-key compromise could expose recorded transfers. Check current applicable firmware and try standard FTPS first.

## FTPS certificate preparation

Obtain only the endpoint's public CA root and its fingerprint from the operator, then follow your camera's manufacturer guide.
Try existing trust first on cameras that allow it. The Canon EOS R6 Mark II and Nikon Z6III guides require root import.
Keep certificate validation enabled; never copy a server private key, certificate/key bundle or ACME account files to the camera.
For a Let's Encrypt/X1 endpoint, the [official ISRG Root X1 information](https://letsencrypt.org/certificates/#root-cas) identifies the public root.
Normal leaf-certificate renewal under the same usable root does not require importing each new leaf; root changes or camera resets may need another import.

## Sony α7C FTP settings and retransfers

In **Network → FTP Transfer Func. → Server Setting**, enter the supplied endpoint and Source credentials.
Use **Passive Mode: On**, **Secure Protocol: On**, and **Root Certificate Error: Does Not Connect**.
See the firmware guidance in the matrix above when selecting the endpoint.
If root import is needed, copy the public root to the card root as `cacert.pem`, then choose **Network → Imp Root Certificate**.
Import may replace trust used by other FTP profiles.

Use these defaults for ShutterSpool:

- **Specify Directory: leave empty.** Upload directly to this Source's root. Use a subdirectory only if you want to retain an incoming folder label; it does not select a delivery destination.
- **Directory Hierarchy: Standard.** Keep the incoming layout simple. Choose **Same as in Camera** only if retaining the card's folder structure matters to you; it does not create separate Sources or delivery routes.
- **Same File Name: Overwrite.** Keep the original filename when the camera encounters an existing name. In ShutterSpool, this still creates a new Asset and preserves the earlier original—it does not replace or deduplicate it. **Does Not Overwrite** adds numbered filenames instead and offers no extra protection for originals already accepted by ShutterSpool.
- **Transfer status: Only Non-transfer** for routine batches. This includes failed transfers. Use **Only Failed Transfer** to retry failures alone; choose **All** only when you deliberately want to resend successful pictures. Resending creates additional Assets.
- **File selection: RAW+JPEG** if you want both originals in ShutterSpool. Choose JPEG or RAW alone if that is all you need; sending fewer files reduces transfer time and spool use.

The camera's filename option controls incoming names; ShutterSpool controls original storage and delivery routing.
A retry after an uncertain result can create a duplicate even with these settings.

Let transfers finish before switching FTP off. After an interrupted transfer, resend the full picture and check it in ShutterSpool.
An interruption can appear as a clean early end of the data stream and still produce protocol success with a truncated Asset.
ShutterSpool cannot independently establish the length the camera intended to send.
The camera's success indicator therefore cannot detect every incomplete file; inspect the result and resend the complete original when uncertain.

Sony reference: [server and directory settings](https://helpguide.sony.net/di/ftp/v1/en/contents/TP1002006124.html),
[batch transfer and status filters](https://helpguide.sony.net/di/ftp/v1/en/contents/TP0002432599.html),
and [transfer/retry notes](https://helpguide.sony.net/di/ftp/v1/en/contents/TP1000228692.html).
