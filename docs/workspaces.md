# Workspaces and access

A workspace keeps its photographs, configuration and membership separate from other workspaces on the same installation.
Your account can belong to several workspaces and have a different role in each.

## Choose a workspace

Sign in, choose a workspace assigned to your account and select **Open workspace**.
Use the header's **Workspace** selector to switch. Resolve or explicitly abandon unsaved edits when prompted; switching reloads the page.
Each tab keeps its selected workspace in its URL, so you can use different workspaces in separate tabs. A shared URL grants no access.

If some memberships could not be checked, explicitly open a listed, confirmed workspace or choose **Retry**.
An unavailable or unconfirmed URL selection never opens another workspace automatically.
If **No workspace access** appears after a complete check, contact the operator if you expected access.

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

## Manage existing members

Owners and Administrators can change non-Owner roles and remove non-Owner members.
Open **Settings → Members** to review existing memberships and make a change.
Only an Owner can promote an existing active member to Owner or demote/remove another Owner. Promotion requires a separate confirmation.
To transfer ownership, promote the new Owner first, then change or leave the previous membership.
The last active Owner cannot leave, be removed or be demoted.

Every active member can leave their own membership, subject to that last-Owner safeguard.
Open **Settings → Workspace → Workspace membership** to leave.
Leaving or removal affects only this workspace: it does not delete the account, another membership or any photographs.
Member administration applies to existing memberships; invitation creation is unavailable.

If the only Owner cannot sign in, use the identity provider's recovery first, then contact the deployment operator through the established recovery channel.
Ordinary restart or signing in with an operator account cannot take over a workspace.

## When access changes

The Server checks current permission for each new request and each item in a bulk action.
An action that passed its permission check before removal or a role change may still finish afterward, including releasing media for delivery.
Later requests and bulk items use the new permissions. A denied action does not itself mean the account has been signed out.

Visibility changes apply to new reads; a response already in progress may finish.
Live connections recheck access within 60 seconds and on reconnect. Previously delivered bytes cannot be recalled.
Removing membership does not cancel already authorized durable delivery work.

Membership, Owner and visibility-policy changes check current authority again inside their atomic, audited update.
A stale administration screen cannot bypass a newer role change or the last-Owner safeguard.
