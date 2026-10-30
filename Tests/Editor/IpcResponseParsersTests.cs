using System;
using NUnit.Framework;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>svn-core JSON → Unity types, against a real captured response and escape-heavy fixtures.</summary>
    public class IpcResponseParsersTests
    {
        private static readonly string U = ((char)92).ToString() + "u";

        private const string RealStatusResponse =
            @"{""v"":1,""id"":""t"",""ok"":true,""result"":{""entries"":[{""path"":""a.txt"",""wcStatus"":""normal"",""propStatus"":""normal"",""lockState"":""K"",""lockOwner"":""colto"",""lockedRev"":0},{""path"":""b.txt"",""wcStatus"":""normal"",""propStatus"":""normal"",""lockState"":""K"",""lockOwner"":""colto"",""lockedRev"":0},{""path"":""new.txt"",""wcStatus"":""unversioned"",""propStatus"":""none"",""lockState"":""None"",""lockedRev"":0},{""path"":""c.txt"",""wcStatus"":""modified"",""propStatus"":""normal"",""changelist"":""WIP"",""lockState"":""None"",""lockedRev"":0}]}}";

        private static string Frame(string entriesJson) =>
            @"{""v"":1,""id"":""u1"",""ok"":true,""result"":{""entries"":[" + entriesJson + "]}}";

        private static string Entry(string path, string wc, string prop = "normal", string lockState = "None", string owner = null, string changelist = null)
        {
            var s = @"{""path"":" + path + @",""wcStatus"":""" + wc + @""",""propStatus"":""" + prop + @"""";
            if (changelist != null) s += @",""changelist"":" + changelist;
            s += @",""lockState"":""" + lockState + @"""";
            if (owner != null) s += @",""lockOwner"":" + owner;
            return s + @",""lockedRev"":0}";
        }

        [Test]
        public void RealResponse_StatusMap_MapsEveryEntry()
        {
            var map = IpcResponseParsers.ParseStatusMap(RealStatusResponse);
            Assert.AreEqual(4, map.Count);
            Assert.AreEqual(LockState.K, map["a.txt"].lockState);
            Assert.AreEqual("colto", map["a.txt"].lockOwner);
            Assert.AreEqual(SvnFileStatus.None, map["a.txt"].status);
            Assert.AreEqual(SvnFileStatus.Untracked, map["new.txt"].status);
            Assert.AreEqual("", map["new.txt"].lockOwner, "omitted lockOwner becomes empty");
            Assert.AreEqual(SvnFileStatus.Modified, map["c.txt"].status);
            Assert.AreEqual("WIP", map["c.txt"].changelist);
            Assert.IsNull(map["a.txt"].changelist, "omitted changelist stays null");
        }

        [Test]
        public void RealResponse_LockMap_KeepsOnlyLockedEntries()
        {
            var map = IpcResponseParsers.ParseLockMap(RealStatusResponse);
            CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, map.Keys);
            Assert.AreEqual(LockState.K, map["b.txt"].state);
            Assert.AreEqual("colto", map["b.txt"].lockOwner);
        }

        [TestCase("modified", "normal", SvnFileStatus.Modified)]
        [TestCase("added", "none", SvnFileStatus.Added)]
        [TestCase("deleted", "none", SvnFileStatus.Deleted)]
        [TestCase("replaced", "none", SvnFileStatus.Replaced)]
        [TestCase("conflicted", "normal", SvnFileStatus.Conflict)]
        [TestCase("missing", "none", SvnFileStatus.Missing)]
        [TestCase("unversioned", "none", SvnFileStatus.Untracked)]
        [TestCase("ignored", "none", SvnFileStatus.Ignored)]
        [TestCase("normal", "normal", SvnFileStatus.None)]
        [TestCase("normal", "modified", SvnFileStatus.Modified)]
        [TestCase("normal", "conflicted", SvnFileStatus.Modified)]
        [TestCase("external", "none", SvnFileStatus.None)]
        public void WcStatus_MapsToFileStatus_IncludingPropOnlyChanges(string wc, string prop, SvnFileStatus expected)
        {
            var e = IpcResponseParsers.ParseFirstStatusEntry(Frame(Entry(@"""x.png""", wc, prop)));
            Assert.AreEqual(expected, e.status);
        }

        [TestCase("O", "bob", LockState.O)]
        [TestCase("B", "alice", LockState.B)]
        [TestCase("T", "artist", LockState.T)]
        public void LockStates_OtherThanKept_CarryTheServerOwner(string state, string owner, LockState expected)
        {
            var json = Frame(Entry(@"""m.mat""", "normal", "normal", state, @"""" + owner + @""""));
            var status = IpcResponseParsers.ParseFirstStatusEntry(json);
            Assert.AreEqual(expected, status.lockState);
            Assert.AreEqual(owner, status.lockOwner);
            var locks = IpcResponseParsers.ParseLockMap(json);
            Assert.AreEqual(expected, locks["m.mat"].state);
            Assert.AreEqual(owner, locks["m.mat"].lockOwner);
        }

        [Test]
        public void SpecialCharacters_InPathOwnerAndChangelist_SurviveIntact()
        {
            var json = Frame(
                Entry(@"""Assets/{Icons}/Foo[1]/Bob" + U + @"0027s \\ " + U + "D83D" + U + @"DE00.png""", "modified", "normal", "O",
                      @"""weird}owner""", @"""WIP ]{\""quoted\""}""") + "," +
                Entry(@"""Assets/after.png""", "added", "none"));
            var map = IpcResponseParsers.ParseStatusMap(json);
            Assert.AreEqual(2, map.Count, "the entry after the special one must still be read");
            var key = "Assets/{Icons}/Foo[1]/Bob's \\ " + char.ConvertFromUtf32(0x1F600) + ".png";
            Assert.IsTrue(map.ContainsKey(key), "decoded path: " + string.Join(" | ", map.Keys));
            Assert.AreEqual("weird}owner", map[key].lockOwner);
            Assert.AreEqual("WIP ]{\"quoted\"}", map[key].changelist);
            Assert.AreEqual(SvnFileStatus.Added, map["Assets/after.png"].status);
        }

        [Test]
        public void DuplicatePath_LastEntryWins()
        {
            var map = IpcResponseParsers.ParseStatusMap(Frame(Entry(@"""d.png""", "modified") + "," + Entry(@"""d.png""", "added", "none")));
            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(SvnFileStatus.Added, map["d.png"].status);
        }

        [Test]
        public void WellFormedFrameWithoutEntries_IsEmpty_NotAnError()
        {
            Assert.IsEmpty(IpcResponseParsers.ParseStatusMap(@"{""v"":1,""id"":""u"",""ok"":true,""result"":{}}"));
            Assert.IsEmpty(IpcResponseParsers.ParseStatusMap(Frame("")));
            Assert.IsEmpty(IpcResponseParsers.ParseLockMap(@"{""v"":1,""id"":""u"",""ok"":false,""error"":{""kind"":""protocol"",""message"":""x""}}"),
                "an error frame has no result; callers short-circuit on !Ok before parsing anyway");
            Assert.IsNull(IpcResponseParsers.ParseFirstStatusEntry(Frame("")));
        }

        [Test]
        public void GarbledFrame_Throws_SoCallersKeepTheirPriorCache()
        {
            Assert.Catch<Exception>(() => IpcResponseParsers.ParseStatusMap(@"{""v"":1,""ok"":true,""result"":{""entries"":[{""path"":""a.txt"",""wcSta"));
            Assert.Catch<Exception>(() => IpcResponseParsers.ParseLockMap("not json at all"));
        }

        [Test]
        public void LogEntries_KeepMessagesWithBracesAndQuotes()
        {
            var json = @"{""v"":1,""id"":""u"",""ok"":true,""result"":{""revisions"":[" +
                       @"{""revision"":12,""author"":""colto"",""date"":""2026-10-29T12:00:00Z"",""message"":""fix {a} [b] \""c\"" Bob" + U + @"0027s"",""paths"":[{""path"":""/x}.cs"",""action"":""M"",""kind"":""file""}]}," +
                       @"{""revision"":11,""author"":""ann"",""date"":""2026-10-28T12:00:00Z"",""message"":""second"",""paths"":[]}" +
                       @"],""hasMore"":false}}";
            var log = IpcResponseParsers.ParseLogEntries(json);
            Assert.AreEqual(2, log.Count);
            Assert.AreEqual(12, log[0].revision);
            Assert.AreEqual("fix {a} [b] \"c\" Bob's", log[0].message);
            Assert.AreEqual("second", log[1].message);
            Assert.IsEmpty(IpcResponseParsers.ParseLogEntries(@"{""v"":1,""id"":""u"",""ok"":true,""result"":{""revisions"":[],""hasMore"":false}}"));
        }

        [Test]
        public void ChangelistNames_DecodeEscapesAndBrackets()
        {
            var names = IpcResponseParsers.ParseNamesArray(@"{""v"":1,""id"":""u"",""ok"":true,""result"":{""names"":[""WIP"",""Bob" + U + @"0027s"",""x]y"",""q\""t""]}}");
            CollectionAssert.AreEqual(new[] { "WIP", "Bob's", "x]y", "q\"t" }, names);
        }

        [Test]
        public void ChangelistMembership_PathsWithBracketsAndApostrophes_AreNotTruncated()
        {
            var json = @"{""v"":1,""id"":""u"",""ok"":true,""result"":{""membership"":{" +
                       @"""WIP"":[""Assets/Foo[1].png"",""Assets/Bob" + U + @"0027s Mesh.fbx""]," +
                       @"""odd ]{\""name\""}"":[""Assets/z.png""]," +
                       @"""empty"":[]}}}";
            var m = IpcResponseParsers.ParseChangelistMembership(json);
            Assert.AreEqual(3, m.Count, string.Join(" | ", m.Keys));
            CollectionAssert.AreEqual(new[] { "Assets/Foo[1].png", "Assets/Bob's Mesh.fbx" }, m["WIP"]);
            CollectionAssert.AreEqual(new[] { "Assets/z.png" }, m["odd ]{\"name\"}"]);
            Assert.IsEmpty(m["empty"]);
        }

        [Test]
        public void PropList_StringMap_DecodesEscapesAndKeepsMultiLineValues()
        {
            var json = @"{""v"":1,""id"":""u"",""ok"":true,""result"":{""props"":{" +
                       @"""svn:needs-lock"":""*""," +
                       @"""svn:ignore"":""Library\nTemp\n""," +
                       @"""frg:notes"":""say \""hi\"" Bob" + U + @"0027s }]""}}}";
            var m = IpcResponseParsers.ParseStringMap(json, "props");
            Assert.AreEqual(3, m.Count);
            Assert.AreEqual("*", m["svn:needs-lock"]);
            Assert.AreEqual("Library" + ((char)10) + "Temp" + ((char)10), m["svn:ignore"]);
            Assert.AreEqual("say \"hi\" Bob's }]", m["frg:notes"]);
            Assert.IsEmpty(IpcResponseParsers.ParseStringMap(@"{""v"":1,""id"":""u"",""ok"":true,""result"":{""props"":{}}}", "props"));
            Assert.Catch<System.Exception>(() => IpcResponseParsers.ParseStringMap(@"{""result"":{""props"":{""a"":""b", "props"));
        }

        [Test]
        public void ChangelistMembership_MissingOrEmpty_IsEmpty_TruncatedThrows()
        {
            Assert.IsEmpty(IpcResponseParsers.ParseChangelistMembership(@"{""v"":1,""id"":""u"",""ok"":true,""result"":{""membership"":{}}}"));
            Assert.IsEmpty(IpcResponseParsers.ParseChangelistMembership(@"{""v"":1,""id"":""u"",""ok"":true,""result"":{}}"));
            Assert.Catch<Exception>(() => IpcResponseParsers.ParseChangelistMembership(@"{""result"":{""membership"":{""WIP"":[""a.png"""));
        }
    }
}
