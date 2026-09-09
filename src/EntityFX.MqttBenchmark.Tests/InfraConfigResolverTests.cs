using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using EntityFX.MqttBenchmark.Bomber;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass, DoNotParallelize]
public class InfraConfigResolverTests
{
    [TestMethod]
    public void Resolve_EscapesArraySecretsAndDeletesOnSuccessfulAndExceptionalCallerExit()
    {
        WithEnvironment((directory, source) =>
        {
            string path;
            using (var lease = InfraConfigEnvironmentResolver.Resolve(source))
            {
                path = lease.Path;
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                Assert.AreEqual("quote-\"\\-line\nvalue", json.RootElement.GetProperty("items")[0].GetString());
                if (OperatingSystem.IsWindows())
                {
                    var acl = new FileInfo(path).GetAccessControl();
                    Assert.IsTrue(acl.AreAccessRulesProtected);
                    var owner = WindowsIdentity.GetCurrent().User;
                    foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        Assert.AreEqual(owner, rule.IdentityReference, "Only the current account may read runtime secrets.");
                }
            }
            Assert.IsFalse(File.Exists(path));
            string? exceptionalPath = null;
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                using var lease = InfraConfigEnvironmentResolver.Resolve(source);
                exceptionalPath = lease.Path;
                throw new InvalidOperationException("caller failed");
            });
            Assert.IsFalse(File.Exists(exceptionalPath));
        });
    }

    [TestMethod]
    public void Resolve_ConstructionFailureAfterAllocationLeavesNoRuntimeFile()
    {
        WithEnvironment((directory, source) =>
        {
            // A hostile inherited ACL allows creation but denies modifying attributes.
            // The previous implementation wrote plaintext, then failed SetAttributes and leaked the file.
            if (!OperatingSystem.IsWindows()) return;
            var temp = Path.Combine(directory, "temp");
            Directory.CreateDirectory(temp);
            var acl = new DirectoryInfo(temp).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteAttributes,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Deny));
            new DirectoryInfo(temp).SetAccessControl(acl);
            var originalTmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                Environment.SetEnvironmentVariable("TMP", temp);
                try { using var lease = InfraConfigEnvironmentResolver.Resolve(source); }
                catch (UnauthorizedAccessException) { }
                Assert.AreEqual(0, Directory.GetFileSystemEntries(temp).Length, "Construction failure must clean every allocated runtime path.");
            }
            finally { Environment.SetEnvironmentVariable("TMP", originalTmp); }
        });
    }

    [TestMethod]
    public void Resolve_MissingVariableDoesNotRevealSecretOrAllocateRuntimeFile()
    {
        WithEnvironment((directory, source) =>
        {
            Environment.SetEnvironmentVariable("MQTT_STAND_TEST_SECRET", null);
            var error = Assert.ThrowsException<InvalidDataException>(() => InfraConfigEnvironmentResolver.Resolve(source));
            StringAssert.Contains(error.Message, "MQTT_STAND_TEST_SECRET");
            Assert.IsFalse(error.Message.Contains("line\nvalue"));
        });
    }

    private static void WithEnvironment(Action<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mqttsecret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "infra.json");
        File.WriteAllText(source, "{\"items\":[\"${MQTT_STAND_TEST_SECRET}\"]}");
        var original = Environment.GetEnvironmentVariable("MQTT_STAND_TEST_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("MQTT_STAND_TEST_SECRET", "quote-\"\\-line\nvalue");
            action(directory, source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MQTT_STAND_TEST_SECRET", original);
            Directory.Delete(directory, true);
        }
    }
}
