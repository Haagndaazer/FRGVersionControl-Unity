using NUnit.Framework;
using FRGVersionControl.Core;
using FRGVersionControl.Core.IpcClient;
using FRGVersionControl.Core.Svn;

namespace FRGVersionControl.Tests
{
    public class SignInCheckTests
    {
        private static SvnLogin.Status Status(string resultJson) =>
            SvnLogin.ParseStatus("{\"v\":1,\"id\":\"p\",\"ok\":true,\"result\":" + resultJson + "}");

        private const string SignedIn =
            "{\"url\":\"https://svn.example/svn/x\",\"usesPasswordCache\":true,\"projectLogin\":{\"username\":\"ann\",\"realm\":\"x\"},\"savedLogins\":[]}";
        private const string NoLogin =
            "{\"url\":\"https://svn.example/svn/x\",\"usesPasswordCache\":true,\"savedLogins\":[]}";
        private const string LocalRepo =
            "{\"url\":\"file:///C:/repo\",\"usesPasswordCache\":false,\"localUsername\":\"colto\",\"savedLogins\":[]}";
        private const string LocalRepoNoName =
            "{\"url\":\"file:///C:/repo\",\"usesPasswordCache\":false,\"savedLogins\":[]}";

        [Test]
        public void LoginName_IsTheProjectLogin()
        {
            Assert.AreEqual("ann", FileLockManager.LoginNameFrom(Status(SignedIn)));
        }

        [Test]
        public void LoginName_IsNull_WhenAPasswordServerHasNoProjectLogin()
        {
            Assert.IsNull(FileLockManager.LoginNameFrom(Status(NoLogin)));
        }

        [Test]
        public void LoginName_IsTheLocalName_ForAUrlWithoutPasswords()
        {
            Assert.AreEqual("colto", FileLockManager.LoginNameFrom(Status(LocalRepo)));
            Assert.IsNull(FileLockManager.LoginNameFrom(Status(LocalRepoNoName)));
        }

        [Test]
        public void LoginName_IsNull_WithoutAnAnswer()
        {
            Assert.IsNull(FileLockManager.LoginNameFrom(null));
        }

        [TestCase(false, "auth_failed", true)]
        [TestCase(true, "auth_failed", false)]
        [TestCase(false, "unreachable", false)]
        [TestCase(false, "cert_untrusted", false)]
        [TestCase(false, "busy", false)]
        [TestCase(false, "protocol", false)]
        [TestCase(false, null, false)]
        public void OnlyARejectedLogin_CountsAsLoginRejection(bool ok, string kind, bool expected)
        {
            Assert.AreEqual(expected, IpcSvnClient.IsLoginRejection(new IpcRawResult { Ok = ok, ErrorKind = kind }));
        }

        [Test]
        public void NoResult_IsNotALoginRejection()
        {
            Assert.IsFalse(IpcSvnClient.IsLoginRejection(null));
        }

        [Test]
        public void ARejection_IsRemembered_UntilCleared()
        {
            var client = new IpcSvnClient(null, null, null);
            Assert.IsFalse(client.LoginRejected);

            client.NoteLoginRejected(client.LoginEpoch);
            Assert.IsTrue(client.LoginRejected);

            client.ClearLoginRejected();
            Assert.IsFalse(client.LoginRejected);
        }

        [Test]
        public void ARequestStartedBeforeASignIn_CannotMarkTheNewLoginRejected()
        {
            var client = new IpcSvnClient(null, null, null);
            int startedAt = client.LoginEpoch;

            client.ClearLoginRejected();
            client.NoteLoginRejected(startedAt);
            Assert.IsFalse(client.LoginRejected, "a request that started before the sign-in must not count");

            client.NoteLoginRejected(client.LoginEpoch);
            Assert.IsTrue(client.LoginRejected, "a request started after the sign-in still counts");
        }

        [Test]
        public void ScratchWorkingCopy_ARemovedLoginNeedsSignIn_AndComingBackClearsIt()
        {
            if (!ScratchWcGuard.IsScratch(out var reason)) Assert.Ignore(reason);
            var manager = FileLockManager.Instance;
            var real = SvnLogin.FetchStatus();
            Assert.IsNotNull(real.status, real.error);
            Assume.That(FileLockManager.LoginNameFrom(real.status), Is.Not.Null.And.Not.Empty,
                "the scratch working copy should not need a sign-in");
            Assume.That(manager.IsInitialized, "file locking should be running in the scratch working copy");

            try
            {
                manager.IpcClient.ClearLoginRejected();
                manager.ApplyLoginStatus(real.status);
                Assert.IsFalse(manager.NeedsSignIn, "a project with a login does not need signing in");

                manager.ApplyLoginStatus(Status(NoLogin));
                Assert.IsTrue(manager.NeedsSignIn, "a login that disappeared needs signing in");

                manager.ApplyLoginStatus(real.status);
                Assert.IsFalse(manager.NeedsSignIn, "the login coming back clears it");
                Assert.IsTrue(manager.IsInitialized);
            }
            finally
            {
                manager.ApplyLoginStatus(real.status);
                manager.IpcClient.ClearLoginRejected();
            }
        }
    }
}
