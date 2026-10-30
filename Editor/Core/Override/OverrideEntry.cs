using System;

namespace FRGVersionControl.Core.OverrideMode
{
    /// <summary>
    /// One entry of the ignore-on-commit registry, as svn-core reports it (override.list and the
    /// snapshot of override.begin/end/forget; SVN_CORE_API.md §5.8). The sidecar itself is owned
    /// by svn-core.
    ///
    /// Phase 2 Step E SVN port — schema break vs prior LFS shape:
    ///   pre_override_pointer_blob_oid (Git SHA-1) → pre_ignore_on_commit_revision (int) — the
    ///     working-copy's BASE revision when override began. Mode A reconciliation compares
    ///     this against the current upstream HEAD revision via `svn info` on the repo URL.
    ///   post_edit_content_sha256: was Git's blob hash; now SHA-256 hex of the working-tree
    ///     bytes after the most recent save. Used by Mode F (silent clobber detection).
    ///   stashed: REMOVED — svn shelve is unavailable in svn 1.14.5 (Phase 1 Addendum Test 3),
    ///     so the parked-override mode is dropped. Old sidecar entries with stashed=true
    ///     are refused per Phase 2 plan resolved decision #10 (require clean state).
    ///   lock_id_at_start: was an LFS lock ID; now empty for SVN since SVN tokens are
    ///     working-copy-internal (not stable cross-WC). Reconciliation Mode B compares
    ///     by lock_holder_at_start instead.
    /// </summary>
    [Serializable]
    public class OverrideEntry
    {
        public string path;
        public string started_at;                       // ISO 8601 UTC
        public int pre_ignore_on_commit_revision;        // svn info revision at override start (BASE rev)
        public string post_edit_content_sha256;         // SHA-256 hex of working-tree bytes after last save
        public string lock_id_at_start;                 // empty for SVN; retained for forward-compat
        public string lock_holder_at_start;             // SVN lock owner username at override start

        public OverrideEntry() { }

        public OverrideEntry(string path, int preRevision, string postSha,
                             string lockId, string lockHolder)
        {
            this.path = path;
            this.started_at = DateTime.UtcNow.ToString("O");
            this.pre_ignore_on_commit_revision = preRevision;
            this.post_edit_content_sha256 = postSha;
            this.lock_id_at_start = lockId ?? "";
            this.lock_holder_at_start = lockHolder ?? "";
        }
    }
}
