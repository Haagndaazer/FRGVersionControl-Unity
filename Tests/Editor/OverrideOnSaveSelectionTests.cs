using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using FRGVersionControl.Hooks;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Pure-logic coverage for the helper that merges override-on-save selections into
    /// the running saveable-paths list inside FileLockAssetModificationProcessor.
    /// The OverrideOnSaveWindow UI itself is verified by the manual editor flow
    /// documented in docs/TOOLING_NOTES.md (per CLAUDE.md polish-bar in-editor-flow rule).
    /// </summary>
    public class OverrideOnSaveSelectionTests
    {
        [Test]
        public void Returns_OriginalSaveable_WhenNoOverrides()
        {
            var saveable = new[] { "Assets/A.unity", "Assets/B.prefab" };
            var result = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(
                saveable, new string[0]);
            CollectionAssert.AreEqual(saveable, result);
        }

        [Test]
        public void AppendsOverriddenPaths()
        {
            var saveable = new[] { "Assets/A.unity" };
            var overridden = new[] { "Assets/B.prefab", "Assets/C.mat" };
            var result = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(
                saveable, overridden);
            CollectionAssert.AreEqual(
                new[] { "Assets/A.unity", "Assets/B.prefab", "Assets/C.mat" },
                result);
        }

        [Test]
        public void DeduplicatesPathsCaseInsensitive()
        {
            var saveable = new[] { "Assets/Scene.unity", "Assets/B.prefab" };
            var overridden = new[] { "ASSETS/SCENE.UNITY", "Assets/C.mat" };
            var result = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(
                saveable, overridden);
            // Original path wins; case-variant override entry is dropped.
            CollectionAssert.AreEqual(
                new[] { "Assets/Scene.unity", "Assets/B.prefab", "Assets/C.mat" },
                result);
        }

        [Test]
        public void EmptyOverrides_DoesNotChangeOrder()
        {
            var saveable = new[] { "Assets/Z.mat", "Assets/A.unity", "Assets/M.prefab" };
            var result = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(
                saveable, new List<string>());
            CollectionAssert.AreEqual(saveable, result);
        }

        [Test]
        public void HandlesNullInputsGracefully()
        {
            // Defensive: caller might hand us null collections in edge cases (Initialize
            // race, partially-disposed window). Helper should treat null as empty.
            var resultBoth = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(null, null);
            Assert.IsNotNull(resultBoth);
            Assert.AreEqual(0, resultBoth.Count);

            var saveable = new[] { "Assets/A.unity" };
            var resultRight = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(saveable, null);
            CollectionAssert.AreEqual(saveable, resultRight);

            var overridden = new[] { "Assets/B.prefab" };
            var resultLeft = FileLockAssetModificationProcessor.BuildSaveableListAfterOverride(null, overridden);
            CollectionAssert.AreEqual(overridden, resultLeft);
        }
    }
}
