# Elkjøp Toolkit

Solution containing multiple support tools (`Elkjop Toolkit` (unified root dispatcher), `Support Tool`, `Account Setup` (formerly `Bypass OOBE`), `Toolkit.Shared`, and future additions).

## Multi-chat project — read this first

The user works on this solution from **two separate Claude chats**, alternating between them. Neither chat sees the other's edits or conversation history, so:

- **Files may have changed since your memory of them was formed.** A method rename, a deleted class, a moved file — any of it can happen in the other chat with no notice here.
- **Do not trust your own memory's description of a file's shape.** Re-read the actual current file contents before editing anything, even (especially) files you're confident you already know.
- **Treat on-disk state as the source of truth, memory as a hint.** If what you find on disk contradicts memory, disk wins — update or correct the memory rather than acting on the stale version.
- If the IDE surfaces a "file modified outside this session" notice, that's a strong signal the other chat has been active. Read the file in full before proceeding, don't just patch around the diff.
- When you learn something durable about this solution, save it to memory so the *other* chat can pick it up next time it looks — that's the main way the two chats stay in sync at all.
