# Memory reduction review

## Prioritized plan and outcome

1. **Remove duplicate task-reference arrays (implemented).** `GoogleTasksClient` already returns `List<TaskRecord>` for each fetched page set. `MainViewModel` now stores those same lists in its cache maps and uses them as the selected-list view. Task creation, completion, editing, and deletion update the canonical lists rather than allocating full replacement arrays or lists. The review checked selection, refresh, delete rollback, and cache-save paths for consistent references.
2. **Remove a cache-save JSON copy (implemented).** Cache JSON is written into an unpooled `ArrayBufferWriter<byte>` and its written segment is passed directly to DPAPI. Encryption, the on-disk format, and atomic temporary-file replacement remain the same. The new round-trip test covers replacement, Unicode, long notes, completed tasks, and cache ownership fields.
3. **Create editing controls on demand (implemented).** Closed rows no longer instantiate title and notes text boxes, quick-date buttons, calendar popups, or calendars. Content templates create them when details expand and remove them when details close. Draft values remain in the view model. Existing event handlers, element names, and calendar placement are preserved.
4. **Share the task context menu (implemented).** Rows use one window resource whose data context follows `PlacementTarget`. Closing the menu clears its target to avoid retaining a removed row. Existing list-move and list-creation behavior is preserved.
5. **Virtualization remains a possible future change.** The three task controls still use the existing outer scroll viewer. Changing this structure needs separate checks for scroll position, inline editing, draft insertion, and completed-row paging.

## Expected savings and limits

For a fetched list with *N* task records, the selected-list reference no longer causes an additional `TaskRecord` reference array of roughly `8 * N` bytes on x64, excluding array overhead. Mutations avoid further O(N) temporary reference arrays and lists; exact savings depend on list size and operation. Cache saves avoid one full plaintext JSON byte-array copy, approximately the serialized cache size at save time. These are allocation estimates from code paths, not measured process working-set reductions. No connected account or representative large task dataset was available for a reliable before/after process measurement.

## Verification

- Baseline: Release solution build passed with 0 warnings and 0 errors; 36 core tests passed.
- Final: Release solution build passed with 0 warnings and 0 errors; 37 core tests passed.
- `git diff --check` passed. Final review found no blocking correctness or memory regressions in the changed paths.

Credentials and cache ownership checks were not changed. The cache remains encrypted with DPAPI; task API request and response handling were not changed.

## UI memory measurement and verification (2026-10-03)

The WPF test loads the actual compiled `TaskRow` template on an STA thread, warms it, then creates and lays out 200 closed rows. It measures retained managed bytes with `GC.GetTotalMemory(true)` while keeping every row alive. Running this harness against the original template and the updated template in separate Release processes gave:

| Template | Managed bytes retained by 200 closed rows |
| --- | ---: |
| Original | 27,311,472 |
| Editors created on demand | 15,870,728 |
| Editors created on demand and shared menu | 10,580,552 |

The final reduction is approximately 16.7 MB (61%). These are synthetic row measurements, excluding process working set, native allocations, and representative connected-account data. The result is not a claim of a 61% reduction in total application memory. The harness retains the original scroll structure and does not open a visible window or contact Google.

All 37 existing tests pass. The additional WPF test verifies that closed rows contain no text editors, opening and reopening creates the editors, title and notes bindings update drafts, calendar placement remains connected, the shared menu switches tasks, and closing it releases its target and data context. Release solution build and `git diff --check` also pass. Manual interaction with a connected Google account remains unverified.
