# Memory reduction review

## Prioritized plan and outcome

1. **Remove duplicate task-reference arrays (implemented).** `GoogleTasksClient` already returns `List<TaskRecord>` for each fetched page set. `MainViewModel` now stores those same lists in its cache maps and uses them as the selected-list view. Task creation, completion, editing, and deletion update the canonical lists rather than allocating full replacement arrays or lists. The review checked selection, refresh, delete rollback, and cache-save paths for consistent references.
2. **Remove a cache-save JSON copy (implemented).** Cache JSON is written into an unpooled `ArrayBufferWriter<byte>` and its written segment is passed directly to DPAPI. Encryption, the on-disk format, and atomic temporary-file replacement remain the same. The new round-trip test covers replacement, Unicode, long notes, completed tasks, and cache ownership fields.
3. **Measure UI row memory before changing layout (follow-up).** The window places three `ItemsControl`s in an outer `ScrollViewer`, so it creates visual rows for every visible task. A virtualization redesign may save more memory for large lists, but it needs measured large-list usage and interaction checks for scroll position, inline editing, drag behavior, and completed rows.

## Expected savings and limits

For a fetched list with *N* task records, the selected-list reference no longer causes an additional `TaskRecord` reference array of roughly `8 * N` bytes on x64, excluding array overhead. Mutations avoid further O(N) temporary reference arrays and lists; exact savings depend on list size and operation. Cache saves avoid one full plaintext JSON byte-array copy, approximately the serialized cache size at save time. These are allocation estimates from code paths, not measured process working-set reductions. No connected account or representative large task dataset was available for a reliable before/after process measurement.

## Verification

- Baseline: Release solution build passed with 0 warnings and 0 errors; 36 core tests passed.
- Final: Release solution build passed with 0 warnings and 0 errors; 37 core tests passed.
- `git diff --check` passed. Final review found no blocking correctness or memory regressions in the changed paths.

Credentials and cache ownership checks were not changed. The cache remains encrypted with DPAPI; task API request and response handling were not changed.
