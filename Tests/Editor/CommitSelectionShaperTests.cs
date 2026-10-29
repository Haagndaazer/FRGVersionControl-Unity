using System.Collections.Generic;
using NUnit.Framework;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    public class CommitSelectionShaperTests
    {
        private static System.Func<string, SvnFileStatus?> Snapshot(params (string path, SvnFileStatus status)[] entries)
        {
            var d = new Dictionary<string, SvnFileStatus>();
            foreach (var e in entries) d[e.path] = e.status;
            return p => d.TryGetValue(p, out var s) ? s : (SvnFileStatus?)null;
        }

        [Test]
        public void AddedFolderMetas_OfEveryAncestor_AreAppended()
        {
            var r = CommitSelectionShaper.ExpandFolderMetas(
                new[] { "Assets/New/Sub/leaf.png" },
                Snapshot(("Assets/New.meta", SvnFileStatus.Added), ("Assets/New/Sub.meta", SvnFileStatus.Added)));
            CollectionAssert.AreEqual(new[] { "Assets/New/Sub/leaf.png", "Assets/New.meta", "Assets/New/Sub.meta" }, r);
        }

        [Test]
        public void ModifiedOrCommittedFolderMetas_AreNeverPulledIn()
        {
            var r = CommitSelectionShaper.ExpandFolderMetas(
                new[] { "Assets/Old/leaf.png" },
                Snapshot(("Assets/Old.meta", SvnFileStatus.Modified)));
            CollectionAssert.AreEqual(new[] { "Assets/Old/leaf.png" }, r);
            r = CommitSelectionShaper.ExpandFolderMetas(new[] { "Assets/Old/leaf.png" }, Snapshot());
            CollectionAssert.AreEqual(new[] { "Assets/Old/leaf.png" }, r);
        }

        [Test]
        public void AlreadySelectedMeta_IsNotDuplicated_AndBackslashesMatch()
        {
            var r = CommitSelectionShaper.ExpandFolderMetas(
                new[] { @"Assets\New\leaf.png", "Assets/New.meta" },
                Snapshot(("Assets/New.meta", SvnFileStatus.Added)));
            CollectionAssert.AreEqual(new[] { @"Assets\New\leaf.png", "Assets/New.meta" }, r);
        }

        [Test]
        public void RootLevelFile_HasNoFolderMeta()
        {
            var r = CommitSelectionShaper.ExpandFolderMetas(new[] { "ProjectSettings/ProjectVersion.txt", "." }, Snapshot(("ProjectSettings.meta", SvnFileStatus.Added)));
            CollectionAssert.AreEqual(new[] { "ProjectSettings/ProjectVersion.txt", ".", "ProjectSettings.meta" }, r);
        }
    }
}
