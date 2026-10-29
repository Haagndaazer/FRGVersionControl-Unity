using System;

namespace FRGVersionControl.Core.Shelves
{
    /// <summary>
    /// One conflict surfaced during shelf-apply. Compares the recorded baseline SHA from
    /// the shelf to the current WC bytes — a mismatch means the target file has changed
    /// since the shelf was created. The artist gets per-file choice (keep mine / take
    /// shelved / show diff / abort) — never silent overwrite (no-silent-loss invariant).
    /// </summary>
    public class ShelfConflict
    {
        public enum Kind
        {
            /// <summary>Target file in WC has changed since shelf was created.</summary>
            BaselineDiverged,
            /// <summary>Target file in WC has been deleted since shelf was created.</summary>
            BaselineMissing,
            /// <summary>Shelf entry's .work or .base file is unreadable.</summary>
            ShelfCorrupt,
        }

        public string relPath;
        public Kind kind;
        public string baseSha256Recorded;
        public string baseSha256Current;
        public string detail;
    }
}
