# Organize photographs with Collections

A Collection gives a photograph one named home, such as `Berlin` or `Events/Berlin`.
Every Capture belongs to one Collection, shared by all its original variants.
New Sources use the built-in `ShutterSpool` Collection unless you choose another incoming default.
Collection controls use the existing signed-in management access; a camera's upload credentials do not grant access.

Collections organize photographs and can supply delivery-folder names.
They do not copy originals, move files already delivered, release photographs or extend their storage lifetime.
Use [retention and recovery](retention.md) for Favorites, Trash and deletion of media.

## Prepare a Collection before shooting

1. Open **Sources**, then the Source's **General** settings.
2. Beside **Incoming Collection default**, choose **Manage**, then **New Collection**.
3. Enter a name such as `Berlin` and choose **Create Collection**. You can do this before any photographs exist.
4. Close management, type the name in **Search collections**, choose the matching Collection and choose **Save settings**. For a new Source, choose **Create Source** after completing its connection settings.

The default applies when ShutterSpool groups an accepted upload into a new Capture.
An upload accepted before a default change can therefore use the new default if it has not yet been grouped.
Existing Captures keep their membership. A later complementary file joining a Capture follows that Capture's Collection, including a deliberate move.
Camera connection steps remain in [camera setup](camera-compatibility.md#camera-setup).

### Choose a name

Plain names need no slash. `Berlin` and `/Berlin` mean the same name; display and routing omit the leading slash.
For optional nested delivery folders, use `Events/Berlin`.
The final segment is the short name, `Berlin`; the full relative path is `Events/Berlin`.
Parent segments are not separate Collections, so renaming one Collection does not rename other paths.

Names are unique regardless of letter case or equivalent Unicode representation.
`Events/Berlin` and `Travel/Berlin` are distinct, while `Berlin`, `berlin` and `/Berlin` conflict.
Use portable folder names: empty segments, `.`/`..`, trailing or repeated slashes, backslashes, surrounding segment whitespace and reserved filesystem names are rejected.
Each segment allows at most 255 UTF-16 code units, and the full name at most 1,024.
A valid name still cannot guarantee that a particular storage provider accepts the final delivery target.

If a name is invalid or already used, your entry stays editable. Correct it and choose **Create Collection** or **Rename** again.
An uncertain save requires checking the saved state before retrying, as described below.

## Browse a Collection

In **Curation** or **Gallery**, open **Filters** and choose one or more Collections.
Type in **Search collections** to see matching names as you type; use **More Collections** when needed.
Matches appear after you enter a search. **Manage** opens the shared management panel, whose search also updates as you type.
Use **Remove** on a selected Collection to remove that filter, or **All Collections** in the search results to clear the Collection filter.

A photograph matches any selected Collection and must also match the other active filters.
Curation and Gallery remember their filters independently.
An empty result means no available photographs match these filters; it does not establish that a Collection is unused.

## Move existing photographs

For one photograph, use **Collection** directly below tags in the Curation editor, or open the photograph's detail in Gallery.
Type in **Search collections**, choose the target and choose **Move photograph**.

For a bulk move:

1. Explicitly select photographs in the bulk grid and find the **Collection** control; in Curation it sits below tags.
2. Search for and choose the target Collection, then choose **Move … photographs**, which shows the selected count.

Moving automatically checks the current membership of every selected photograph first.
If a photograph is unavailable or the check fails, nothing is moved; follow the recovery message before trying again.

Bulk moves cover only the explicit selection, up to 100 Captures. They do not select every result matching a filter.
A move may succeed for some photographs and fail for others; inspect the separate moved, failed and unknown counts.
Successful items leave the selection, while unresolved items remain available for inspection.
For a conflict or uncertain response, use **Check outcomes before retry**, review current membership and deliberately retry only unresolved items.
Do not assume that losing the response undid the move.

Only Active Captures can move; Trash and removed or erasing photographs cannot.
You can move released photographs, but this changes their current membership only.
The **At release** details and recorded delivery targets keep their earlier context.
Moving does not edit released rating/tags or repeat delivery.

## Use Collection names in delivery rules

In **Delivery**, the destination's rule editor exposes `collectionName` and `collectionPath` text conditions.
For `Events/Berlin`, their values are `Berlin` and `Events/Berlin` respectively.
Use `{collectionName}` in a folder or filename template; `{collectionPath}` is allowed only in a folder template because it can expand to several segments.
Review the target preview and keep the destination root and provider's restrictions in mind.

| Action | Collection context used |
| --- | --- |
| Release and automatic routing, including a late complementary original | ID, name and path frozen at release |
| Explicit Reevaluate | Current membership and name/path; preview and confirmation are required |
| Retry or Resend | The recorded target |

Renaming changes future name/path rule matches. Literal rules keep their text, so reusing a deleted name can match an existing rule.
Rename or move alone starts no routing work. If either changes after a Reevaluate preview, refresh that preview before confirming.
Older releases and decisions may say **Collection not recorded**; this means the historical value is unknown, even when today's membership is `ShutterSpool`.

## Rename or delete an unused Collection

Open **Manage**, search for the Collection and select it to rename or delete it.
The built-in `ShutterSpool` Collection is fixed and cannot be renamed or deleted.
Other Collections can be deleted only when no Source or retained Capture references them.
Disabled and archived Sources, Trash and retained historical Capture records still count; the displayed counts are advisory.
An archived Source cannot be edited to remove its reference in this version.

If deletion is blocked, review the Source and retained-photograph references.
An empty Gallery does not override those references, and Collection deletion is not a way to remove photographs.
Copied release/delivery history can survive deletion of an otherwise unused Collection.

To remove several unused Collections, choose **Clear unused** in management.
This checks Collections beyond the current search and presents up to 100 unused candidates per confirmation.
Review the names, then choose **Delete … unused Collections**, or **Cancel** to keep them.
The built-in Collection and Collections referenced by Sources or retained photographs are kept.
The server checks current references when each deletion is attempted; a Collection that changed or became referenced is skipped.
Inspect the deleted and skipped counts. More unused Collections may remain after a full batch; choose **Clear unused** again to review another batch.

Cleanup stops if an outcome is uncertain, and earlier deletions may already have succeeded.
If a save or delete has an uncertain result, choose **Check saved Collections** and inspect the saved state before deliberately trying again.
