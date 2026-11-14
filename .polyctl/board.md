# Board

**Where** each pre-authorised piece of work is. The acceptance boundaries are in `backlog.md`;
this file says only the state and, for a parked item, **what it waits on**.

States: `queued` → `running` → (`blocked` ⇄) → `verified` → `done`
Row format: `- [ID] title · key: value` — `needs:` is required for `blocked`.
Dependency: `needs: B1, B2` (on a queued item) → it becomes READY once those are done.

The implementer edits this file directly. `polyctl board` reads it, and `polyctl board add` admits one item to it, with its acceptance boundary.
`polyctl slice start`, `polyctl slice block` and `polyctl slice abandon` move an item and record each move in `.polyctl/ledger/slices.jsonl`.

## running

## blocked

## queued

## inbox

## verified

## done
