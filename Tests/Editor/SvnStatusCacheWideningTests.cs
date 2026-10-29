using NUnit.Framework;
using FRGVersionControl.Core;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// Pin the API surface of SvnStatusCache after Slice 1's widening from SvnFileStatus →
    /// SvnStatusEntry. These don't drive a real refresh (no svn process in unit tests); they
    /// verify the cache APIs are reachable + the empty/null contracts hold.
    /// </summary>
    public class SvnStatusCacheWideningTests
    {
        [SetUp]
        public void ClearCacheBeforeEach()
        {
            SvnStatusCache.Clear();
        }

        [Test]
        public void Get_EmptyCache_ReturnsNone()
        {
            var s = SvnStatusCache.Get("Assets/Anything.cs");
            Assert.AreEqual(SvnFileStatus.None, s);
        }

        [Test]
        public void GetEntry_EmptyCache_ReturnsNull()
        {
            var e = SvnStatusCache.GetEntry("Assets/Anything.cs");
            Assert.IsNull(e);
        }

        [Test]
        public void Get_NullPath_ReturnsNone()
        {
            Assert.AreEqual(SvnFileStatus.None, SvnStatusCache.Get(null));
            Assert.AreEqual(SvnFileStatus.None, SvnStatusCache.Get(""));
        }

        [Test]
        public void GetEntry_NullPath_ReturnsNull()
        {
            Assert.IsNull(SvnStatusCache.GetEntry(null));
            Assert.IsNull(SvnStatusCache.GetEntry(""));
        }

        [Test]
        public void SnapshotAll_EmptyCache_ReturnsEmptyList()
        {
            var snap = SvnStatusCache.SnapshotAll();
            Assert.IsNotNull(snap);
            Assert.AreEqual(0, snap.Count);
        }

        [Test]
        public void Clear_ResetsCount()
        {
            SvnStatusCache.Clear();
            Assert.AreEqual(0, SvnStatusCache.Count);
        }
    }
}
