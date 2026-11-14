# Workspaces and access

A workspace keeps its photographs, configuration and membership separate from other workspaces on the same installation.
Your account can belong to several workspaces and have a different role in each.

## Choose a workspace

Sign in. If a complete check finds exactly one accessible workspace and no workspace was selected in the URL, it opens automatically. Otherwise, choose an assigned workspace and select **Open workspace**.
Use the header's **Workspace** selector to switch. Resolve or explicitly abandon unsaved edits when prompted; switching reloads the page.
Each tab keeps its selected workspace in its URL, so you can use different workspaces in separate tabs. A shared URL grants no access.

If some memberships could not be checked, explicitly open a listed, confirmed workspace or choose **Retry**.
An unavailable or unconfirmed URL selection never opens another workspace automatically.
If **No workspace access** appears after a complete check, contact the operator if you expected access.

## Join an invited workspace

Complete any account-setup steps from the sign-in service, then sign in to ShutterSpool through its ordinary application URL.
On the workspace chooser, review **Workspace invitations**. If you already have a workspace open, select **Invitations**.
Check the workspace name and offered role, then choose **Join workspace** only for a workspace you recognize.
After joining, open it from the workspace list. Joining another workspace does not retarget work in the current one.

The invitation is tied to the intended account. Sharing an application URL cannot let a different signed-in account accept it.
Your sign-in service must confirm email verification in a fresh sign-in; finishing account setup alone does not create a membership.
Keep account-setup emails private: someone who controls a setup link may gain control of that same sign-in account.

Workspace invitations expire 24 hours after creation. An expired or cancelled invitation needs a new invitation from a workspace Owner or Administrator.
Account-setup links have their own expiry. Renewing a workspace invitation preserves existing credentials and may send another native setup email if account setup is still needed.
If a Join result is uncertain, use **Check previous Join** to reconcile it. **Refresh invitations** retries an incomplete or unavailable check.
An unavailable invitation check is not proof that no invitation exists.

## Understand your role

| Role | What you can do | Important limit |
| --- | --- | --- |
| Owner | Administer the workspace, curate media and manage members, including other Owners. | The workspace must retain an active Owner. |
| Administrator | Administer the workspace, curate media and manage non-Owner members, including other Administrators. | Cannot appoint, demote or remove an Owner. |
| Curator | Browse and curate media, release Captures, move them between existing Collections, retry unfinished delivery to its recorded target, and use recoverable Trash/Restore. | Cannot manage configuration, policies or members; request purge/privacy erasure; or create new delivery intent through Resend/Reevaluate. |
| Viewer | Browse eligible photographs, previews, Collections and safe delivery outcomes. | Cannot change media or configuration, or browse Trash. |

Owner and Administrator access does not include installation settings, storage allocations, keys or host administration.
They can configure deployment-enabled Sources and destinations and set workspace quotas within the operator's allocation.
All roles retain the existing [media protection and retention rules](retention.md); ownership does not override them.

## Control what Viewers can see

Owners and Administrators choose one visibility policy for all Viewers in the workspace.
Open **Settings → Members → Viewer visibility**, choose the setting and select **Save Viewer visibility**.
The default shows Active Captures that have been released and are not currently rejected.
Release can be automatic or manual; a failed delivery does not hide an otherwise eligible Capture.

The broader setting also includes Active unreleased and rejected Captures. Neither setting grants access to Trash or unavailable originals.
Personal filters can narrow the authorized photographs but cannot reveal more; direct URLs and previews follow the same policy.
Individual Viewer restrictions by tag, Collection or rating are unavailable.

## Invite a member

Owners and Administrators open **Settings → Members → Invitations**, enter the recipient's **Email address**, choose **Offered role**, and select **Create invitation**.
Viewer is the default; choose Curator or Administrator only when that access is intended. Owner authority requires the separate promotion of an active member after joining.
The installation resolves an existing sign-in account or creates one through its configured Keycloak companion. There is no per-recipient operator approval or global account directory to browse.

New recipients receive a native account-setup email from the sign-in service. Existing verified users find the invitation in ShutterSpool; share the ordinary application URL with them.
An existing account that still needs email verification receives native verification steps without resetting its password. Account setup and sign-in alone grant no workspace access: the recipient must explicitly join.
Check both the invitation and setup-email result. If the invitation was saved but its setup email failed, keep the pending invitation and use the offered retry action; contact the operator if native enrollment is unavailable or the account is rejected.
An email submission result does not prove mailbox delivery. Check spam folders or ask the operator for help if the message does not arrive.
**Renew invitation** starts a fresh 24-hour offer with the selected role and supersedes its previous version. It does not reset credentials or change an existing membership.
**Cancel invitation** stops an unused offer. Once the recipient has joined, use membership removal to revoke access instead.
An issued invitation remains valid if its inviter later changes role or leaves. Removing the recipient invalidates their pending offers; old offers and Join retries cannot restore removed access.
To invite a removed member back, use **Create invitation** again for their email address. Accepted offers and offers invalidated by recipient removal cannot be renewed.

## Manage existing members

Owners and Administrators can change non-Owner roles and remove non-Owner members.
Open **Settings → Members** to review existing memberships and make a change.
A member's display name comes from their authenticated identity claims and may be absent until they access the workspace. Names are labels only; the stable subject is shown when no name is available.
Only an Owner can promote an existing active member to Owner or demote/remove another Owner. Promotion requires a separate confirmation.
To transfer ownership, promote the new Owner first, then change or leave the previous membership.
The last active Owner cannot leave, be removed or be demoted.

Every active member can leave their own membership, subject to that last-Owner safeguard.
Open **Settings → Workspace → Workspace membership** to leave.
Leaving or removal affects only this workspace: it does not delete the account, another membership or any photographs.
Invitations do not change an existing member's role; use these member controls for that change.

If an Owner cannot sign in, use the identity provider's native account recovery or ask its trusted administrator to restore access to that same account. Another Owner who can sign in can manage membership normally. If no Owner account can be recovered, contact the deployment operator for exceptional maintenance.
Ordinary restart or an identity-provider administrator role cannot take over a workspace.

## When access changes

The Server checks current permission for each new request and each item in a bulk action.
An action that passed its permission check before removal or a role change may still finish afterward, including releasing media for delivery.
Later requests and bulk items use the new permissions. A denied action does not itself mean the account has been signed out.

Visibility changes apply to new reads; a response already in progress may finish.
Live connections recheck access within 60 seconds and on reconnect. Previously delivered bytes cannot be recalled.
Removing membership does not cancel already authorized durable delivery work.

Membership, Owner and visibility-policy changes check current authority again inside their atomic, audited update.
A stale administration screen cannot bypass a newer role change or the last-Owner safeguard.
