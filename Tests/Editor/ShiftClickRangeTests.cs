using System;
using NUnit.Framework;
using FRGVersionControl.Core.Svn;
using FRGVersionControl.UI;

namespace FRGVersionControl.Tests
{
    public class ShiftClickRangeTests
    {
        private static readonly string[] s_Order = { "Assets/a.png", "Assets/b.png", "Assets/c.png", "Assets/d.png", "Assets/e.png" };

        private static ShiftClickRange Published(params string[] keys)
        {
            var range = new ShiftClickRange(StringComparer.OrdinalIgnoreCase);
            range.BeginPass();
            foreach (var k in keys) range.Add(k);
            range.EndPass();
            return range;
        }

        [Test]
        public void PlainClick_SetsOnlyTheClickedRow_AndBecomesTheAnchor()
        {
            var range = Published(s_Order);
            CollectionAssert.AreEqual(new[] { "Assets/b.png" }, range.Resolve("Assets/b.png", shift: false));
            Assert.AreEqual("Assets/b.png", range.Anchor);
        }

        [Test]
        public void ShiftClick_BelowTheAnchor_SetsTheRunInclusive()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);
            CollectionAssert.AreEqual(new[] { "Assets/b.png", "Assets/c.png", "Assets/d.png" },
                range.Resolve("Assets/d.png", shift: true));
        }

        [Test]
        public void ShiftClick_AboveTheAnchor_SetsTheRunInclusive()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/e.png", false);
            CollectionAssert.AreEqual(new[] { "Assets/b.png", "Assets/c.png", "Assets/d.png", "Assets/e.png" },
                range.Resolve("Assets/b.png", shift: true));
        }

        [Test]
        public void ShiftClick_MovesTheAnchorToTheClickedRow()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/a.png", false);
            range.Resolve("Assets/c.png", true);
            Assert.AreEqual("Assets/c.png", range.Anchor);
            CollectionAssert.AreEqual(new[] { "Assets/c.png", "Assets/d.png", "Assets/e.png" },
                range.Resolve("Assets/e.png", true));
        }

        [Test]
        public void ShiftClick_OnTheAnchorItself_SetsOnlyThatRow()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/c.png", false);
            CollectionAssert.AreEqual(new[] { "Assets/c.png" }, range.Resolve("Assets/c.png", true));
        }

        [Test]
        public void ShiftClick_WithNoAnchor_ActsLikeAPlainClick()
        {
            var range = Published(s_Order);
            CollectionAssert.AreEqual(new[] { "Assets/d.png" }, range.Resolve("Assets/d.png", true));
            Assert.AreEqual("Assets/d.png", range.Anchor);
        }

        [Test]
        public void ShiftClick_WhenTheAnchorIsNoLongerVisible_ActsLikeAPlainClick()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);

            range.BeginPass();
            foreach (var k in new[] { "Assets/a.png", "Assets/c.png", "Assets/d.png" }) range.Add(k);
            range.EndPass();

            CollectionAssert.AreEqual(new[] { "Assets/d.png" }, range.Resolve("Assets/d.png", true));
        }

        [Test]
        public void ShiftClick_OnAKeyMissingFromTheOrder_SetsOnlyThatKey()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);
            CollectionAssert.AreEqual(new[] { "Assets/z.png" }, range.Resolve("Assets/z.png", true));
        }

        [Test]
        public void TheAnchor_SurvivesARebuild_ThatKeepsItsRow()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);

            range.BeginPass();
            foreach (var k in new[] { "Assets/new.png", "Assets/a.png", "Assets/b.png", "Assets/c.png" }) range.Add(k);
            range.EndPass();

            CollectionAssert.AreEqual(new[] { "Assets/new.png", "Assets/a.png", "Assets/b.png" },
                range.Resolve("Assets/new.png", true));
        }

        [Test]
        public void Keys_AreComparedWithTheGivenComparer()
        {
            var range = Published(s_Order);
            range.Resolve("assets/A.PNG", false);
            CollectionAssert.AreEqual(new[] { "Assets/a.png", "Assets/b.png", "Assets/c.png" },
                range.Resolve("Assets/C.png", true));
        }

        [Test]
        public void APassInProgress_DoesNotReplaceThePublishedOrder()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/a.png", false);

            range.BeginPass();
            range.Add("Assets/a.png");

            CollectionAssert.AreEqual(new[] { "Assets/a.png", "Assets/b.png", "Assets/c.png" },
                range.Resolve("Assets/c.png", true));
            CollectionAssert.AreEqual(s_Order, range.Order);
        }

        [Test]
        public void AnAbandonedPass_IsDiscardedByTheNextOne()
        {
            var range = new ShiftClickRange(StringComparer.OrdinalIgnoreCase);
            range.BeginPass();
            range.Add("Assets/stale.png");
            range.BeginPass();
            range.Add("Assets/a.png");
            range.Add("Assets/b.png");
            range.EndPass();

            CollectionAssert.AreEqual(new[] { "Assets/a.png", "Assets/b.png" }, range.Order);
        }

        [Test]
        public void Add_OutsideAPass_IsIgnored()
        {
            var range = Published(s_Order);
            range.Add("Assets/late.png");
            range.EndPass();
            CollectionAssert.AreEqual(s_Order, range.Order);
        }

        [Test]
        public void Clear_ForgetsTheOrderAndTheAnchor()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);
            range.Clear();

            Assert.IsNull(range.Anchor);
            Assert.AreEqual(0, range.Order.Count);
            CollectionAssert.AreEqual(new[] { "Assets/d.png" }, range.Resolve("Assets/d.png", true));
        }

        [Test]
        public void ResetAnchor_KeepsTheOrder_ButTheNextShiftClickIsPlain()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);
            range.ResetAnchor();

            CollectionAssert.AreEqual(s_Order, range.Order);
            CollectionAssert.AreEqual(new[] { "Assets/d.png" }, range.Resolve("Assets/d.png", true));
        }

        [Test]
        public void EmptyOrNullClickedKey_SetsNothing_AndKeepsTheAnchor()
        {
            var range = Published(s_Order);
            range.Resolve("Assets/b.png", false);

            Assert.AreEqual(0, range.Resolve(null, true).Count);
            Assert.AreEqual(0, range.Resolve("", true).Count);
            Assert.AreEqual("Assets/b.png", range.Anchor);
        }

        [TestCase(false, SvnFileStatus.Modified, false)]
        [TestCase(false, SvnFileStatus.Untracked, false)]
        [TestCase(false, SvnFileStatus.Conflict, false)]
        [TestCase(true, SvnFileStatus.Modified, true)]
        [TestCase(true, SvnFileStatus.Conflict, false)]
        public void ChangesRows_AreInTheIgnoreOnCommitList_OnlyWhenOverriddenAndNotInConflict(
            bool isOverride, SvnFileStatus status, bool expected)
        {
            Assert.AreEqual(expected, FrgVersionControlWindow.IsOverrideListRow(isOverride, status));
        }

        [Test]
        public void Order_IsACopy_ThatTheNextPassDoesNotChange()
        {
            var range = Published(s_Order);
            var before = range.Order;
            range.BeginPass();
            range.Add("Assets/x.png");
            range.EndPass();
            CollectionAssert.AreEqual(s_Order, before);
            CollectionAssert.AreEqual(new[] { "Assets/x.png" }, range.Order);
        }

        [Test]
        public void Resolve_AgainstAFixedList_WorksWithoutAPass()
        {
            var range = new ShiftClickRange();
            range.Resolve(s_Order, "Assets/d.png", false);
            CollectionAssert.AreEqual(new[] { "Assets/a.png", "Assets/b.png", "Assets/c.png", "Assets/d.png" },
                range.Resolve(s_Order, "Assets/a.png", true));
        }
    }
}
