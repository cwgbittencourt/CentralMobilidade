# Protected filesystem quarantine — T048

`FileSystemQuarantineStore` implements `IQuarantineStore`. Its constructor requires
`IConfiguration` (`Quarantine:Path`) and `ILogger<FileSystemQuarantineStore>`.
Use a dedicated, service-owned local directory; the adapter restricts that directory
to the current Windows identity (protected inheritable ACL) or Unix mode 0700, and
creates Unix objects with mode 0600. Worker/application wiring remains T053.

Each opaque UUID maps internally to one immutable versioned file: original bytes,
bounded JSON metadata (at most 4 KiB), then a fixed trailer containing the envelope
SHA-256, metadata length and format signature. Metadata also contains the raw-content
SHA-256. A 64 KiB buffer streams the caller's content into a private same-directory
staging file. The file is flushed to disk before an exclusive atomic rename publishes
it. No separate metadata/content pair can become partially visible. Failed or cancelled
writes remove their own staging file and leave the caller's stream open.

Concurrent writers, including separate store instances, race on the exclusive rename.
The loser verifies the published object and accepts only identical content digest and
metadata. Conflicts preserve the original. Reads validate both hashes and the metadata
before returning a bounded raw-only stream from the same open file handle. Metadata
reads also validate the object; corrupt objects fail closed. No public result contains
a path. IO failures become provider-neutral exceptions without inner exceptions, and
logs include only opaque identifiers, line number and catalogued error code.

Retention must fall between creation and creation + 30 days, in UTC. Deletion is
inclusive at the stored deadline, removes the complete object and tolerates repeated
calls. T061 supplies scheduling and deletion evidence. A corrupt object requires
operational remediation rather than trusting a corrupt deletion deadline.

The implementation uses filesystem permissions, not encryption. The hashes detect
corruption, not malicious rewriting by the service identity or host administrator.
The root and existing object reject reparse points, but a hostile process with the
same identity or control over ancestor directories is outside this boundary.
Atomic publication and file flush are tested without claiming power-loss durability
of the directory entry, crash cleanup of staging files, or protection from host loss.
T054/T057/T060 enforce authorization/audit before administrative content access.

Validation: 16 filesystem integration scenarios pass on Windows and on Linux as UID
10001, including the original 11 T039 cases, store re-instantiation, metadata conflict,
raw corruption, cancellation and an earlier deletion deadline.
