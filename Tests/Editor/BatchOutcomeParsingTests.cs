using System.Linq;
using NUnit.Framework;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    public class BatchOutcomeParsingTests
    {
        private const string RevertLine =
            "{\"v\":1,\"id\":\"u7\",\"ok\":true,\"result\":{\"reverted\":[\"Assets\\\\Art\\\\rock.fbx\"],\"failed\":[{\"path\":\"Assets/missing.png\",\"error\":\"Skipped by svn: not under version control, or nothing to revert. Check that the file is under version control, then refresh status.\"}],\"unchanged\":[\"Assets/clean.png\"]}}";

        private const string UnlockLine =
            "{\"v\":1,\"id\":\"u8\",\"ok\":true,\"result\":{\"released\":[\"Assets/Art/rock.fbx\"],\"failed\":[{\"path\":\"Assets/other.png\",\"svnCode\":\"E195013\",\"line\":\"svn: E195013: 'C:\\\\wc\\\\Assets\\\\other.png' is not locked in this working copy\"}]}}";

        [Test]
        public void ParseRevert_SplitsTheThreeLists_AndMatchesSvnSpelling()
        {
            var o = BatchOutcomes.ParseRevert(RevertLine);
            Assert.IsTrue(o.WasReverted("Assets/Art/rock.fbx"));
            Assert.IsTrue(o.IsUnchanged("Assets/clean.png"));
            Assert.IsNotNull(o.FailureFor("Assets/missing.png"));
            StringAssert.EndsWith("refresh status.", o.FailureFor("Assets/missing.png").error);
            Assert.IsFalse(o.WasReverted("Assets/clean.png"));
        }

        [Test]
        public void ParseUnlock_ReleasedAndFailed_PerPath()
        {
            var o = BatchOutcomes.ParseUnlock(UnlockLine);
            Assert.IsTrue(o.WasReleased("Assets\\Art\\rock.fbx"));
            Assert.AreEqual("E195013", o.FailureFor("Assets/other.png").svnCode);
            Assert.IsNull(o.FailureFor("Assets/Art/rock.fbx"));
        }

        [Test]
        public void Parse_GarbageOrEmpty_YieldsEmptyOutcomes()
        {
            Assert.IsEmpty(BatchOutcomes.ParseRevert("").reverted);
            Assert.IsEmpty(BatchOutcomes.ParseUnlock("{\"ok\":true}").released);
        }

        [Test]
        public void PathsMatch_IsBoundaryAware()
        {
            Assert.IsTrue(BatchOutcomes.PathsMatch(@"C:\wc\Assets\a.png", "Assets/a.png"));
            Assert.IsTrue(BatchOutcomes.PathsMatch("Assets/a.png", "ASSETS/A.PNG"));
            Assert.IsFalse(BatchOutcomes.PathsMatch("Assets/ba.png", "a.png"));
            Assert.IsFalse(BatchOutcomes.PathsMatch("", "a.png"));
            Assert.IsFalse(BatchOutcomes.PathsMatch("sub/a.txt", "a.txt"), "a relative reported path never claims a shorter requested one");
            Assert.IsFalse(BatchOutcomes.PathsMatch("a.txt", "sub/a.txt"));
        }

        [Test]
        public void ParseErrorPerPath_ReadsEntriesFromAnErrorFrame()
        {
            const string line = "{\"v\":1,\"id\":\"u9\",\"ok\":false,\"error\":{\"kind\":\"lock_held_by_other\",\"message\":\"m\",\"perPath\":[{\"path\":\"Assets/x.png\",\"svnCode\":\"E195013\",\"line\":\"not locked\"}]}}";
            var per = BatchOutcomes.ParseErrorPerPath(line);
            Assert.AreEqual(1, per.Count);
            Assert.AreEqual("Assets/x.png", per[0].path);
            Assert.IsEmpty(BatchOutcomes.ParseErrorPerPath("{\"ok\":true}"));
        }

        [Test]
        public void Chunk_SplitsAtTheSize()
        {
            var items = Enumerable.Range(0, 230).Select(i => "p" + i).ToList();
            var chunks = BatchOutcomes.Chunk(items, BatchOutcomes.ChunkSize).ToList();
            Assert.AreEqual(3, chunks.Count);
            Assert.AreEqual(100, chunks[0].Length);
            Assert.AreEqual(30, chunks[2].Length);
        }
    }
}
