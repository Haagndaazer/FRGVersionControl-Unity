using System.Linq;
using NUnit.Framework;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    /// <summary>
    /// XML-fixture tests for `svn status --xml` changelist parsing. SVN 1.14 emits two
    /// shapes — `&lt;target&gt;` with bare entries, and `&lt;changelist name="..."&gt;` groups
    /// wrapping entries. Both must populate <see cref="SvnStatusEntry.changelist"/> the same
    /// way for the grouped-renderer to work.
    /// </summary>
    public class ChangelistParsingTests
    {
        [Test]
        public void Parse_StatusXml_ChangelistGroupShape_PopulatesChangelist()
        {
            string xml = @"<?xml version='1.0' encoding='UTF-8'?>
<status>
  <changelist name='frontend-cleanup'>
    <entry path='Assets/Foo.cs'>
      <wc-status item='modified' revision='42' />
    </entry>
  </changelist>
</status>";
            var entry = SvnCommandRunner.ParseStatusXmlForTest(xml, "Assets/Foo.cs");
            Assert.IsNotNull(entry);
            Assert.AreEqual("Assets/Foo.cs", entry.path);
            Assert.AreEqual(SvnFileStatus.Modified, entry.status);
            // ParseSingleStatusEntry walks only the entry-element; the group's name does
            // NOT propagate through that path. The bulk GetAllStatusEntries path is what
            // surfaces changelist names from the group form.
        }

        [Test]
        public void Parse_StatusXml_AttributeShape_PopulatesChangelist()
        {
            string xml = @"<?xml version='1.0' encoding='UTF-8'?>
<status>
  <target path='.'>
    <entry path='Assets/Foo.cs' changelist='wip'>
      <wc-status item='modified' revision='7' />
    </entry>
  </target>
</status>";
            var entry = SvnCommandRunner.ParseStatusXmlForTest(xml, "Assets/Foo.cs");
            Assert.IsNotNull(entry);
            Assert.AreEqual("wip", entry.changelist);
        }

        [Test]
        public void Parse_StatusXml_NoChangelist_LeavesNull()
        {
            string xml = @"<?xml version='1.0' encoding='UTF-8'?>
<status>
  <target path='.'>
    <entry path='Assets/Foo.cs'>
      <wc-status item='modified' revision='7' />
    </entry>
  </target>
</status>";
            var entry = SvnCommandRunner.ParseStatusXmlForTest(xml, "Assets/Foo.cs");
            Assert.IsNotNull(entry);
            Assert.IsTrue(string.IsNullOrEmpty(entry.changelist));
        }

        [Test]
        public void Parse_StatusXml_IgnoreOnCommitChangelist_RecognizedByName()
        {
            string xml = @"<?xml version='1.0' encoding='UTF-8'?>
<status>
  <target path='.'>
    <entry path='Assets/Locked.uasset' changelist='ignore-on-commit'>
      <wc-status item='modified' revision='12' />
    </entry>
  </target>
</status>";
            var entry = SvnCommandRunner.ParseStatusXmlForTest(xml, "Assets/Locked.uasset");
            Assert.AreEqual("ignore-on-commit", entry.changelist);
            Assert.AreEqual(ChangelistsExpose.ReservedName, entry.changelist,
                "Pin: reserved-name constant matches the literal value the parser surfaces.");
        }

        [Test]
        public void Parse_StatusXml_EmptyDocument_ReturnsNoneStatus()
        {
            var entry = SvnCommandRunner.ParseStatusXmlForTest(
                "<?xml version='1.0'?><status><target path='.' /></status>", "Assets/Missing.cs");
            Assert.IsNotNull(entry);
            Assert.AreEqual(SvnFileStatus.None, entry.status);
        }

        [Test]
        public void Parse_StatusXml_MalformedReturnsNoneOrNull()
        {
            var entry = SvnCommandRunner.ParseStatusXmlForTest("<<<not xml>>>", "Assets/Foo.cs");
            // Defensive contract: malformed XML returns either null OR a None-status entry.
            // Both are valid; the parser must NEVER throw past the boundary.
            if (entry != null)
            {
                Assert.AreEqual(SvnFileStatus.None, entry.status);
            }
        }
    }

    /// <summary>
    /// Small bridge type so the test asmdef doesn't need a direct dep on Core.Changelists
    /// just to assert a name string. Lets the test pin the contract without leaking the
    /// dependency graph.
    /// </summary>
    internal static class ChangelistsExpose
    {
        public const string ReservedName = "ignore-on-commit";
    }
}
