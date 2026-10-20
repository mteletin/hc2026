extern alias app;
using System.Collections.Immutable;
using System.Reflection;
using Xunit;
using AppFiles = app::Hc2000.App.Media.HddFiles;
using AppPublication = app::Hc2000.HostFiles.HddContainerPublication;
using AppAdmission = app::Hc2000.HostFiles.NativeImageAdmission;
using AppPlatform = app::Hc2000.HostFiles.ImageAdmissionPlatformMode;

namespace Hc2000.Tools.Tests;

[Collection("Offline replacement private files")]
public sealed class HddSharedPublicationTests
{
    [Fact]
    public void AppAndToolsPublishByteIdenticalContainersFromTheSameCapture()
    {
        using var toolsDomain = new HddOfflineReplacementFixturesTests();
        var appDomain = Path.Combine(Path.GetTempPath(), "hc-shared-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDomain);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(appDomain, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var scope = AppAdmission.EnterPrivateTestDomain(appDomain,
                OperatingSystem.IsLinux() ? AppPlatform.GuardedLinux : AppPlatform.LegacyOnly);
            var tools = new HddOfflineReplacementFixturesTests.PrivateCase("shared-tools");
            var host = new HddOfflineReplacementFixturesTests.PrivateCase("shared-app");
            var captured = HddOfflineReplacementFixturesTests.Payload(0x43);
            using var toolAttempt = tools.Call(captured);
            tools.New(toolAttempt.Receipt);
            var request = host.Request;
            var identity = request.ExpectedVolume;
            var limits = request.Limits;
            var usage = request.Usage;
            using var appAttempt = AppFiles.ReplaceContainer(new(request.TargetPath,
                new(new(identity.Store.ContainerId, identity.Store.Revision, identity.Store.MetadataSha256), identity.Name, identity.PayloadSha256),
                request.OwnerEpoch, request.AttemptId, new(true, true, true, true),
                new(limits.MaxFileBytes, limits.MaxEntries, limits.MaxIndexBytes, limits.MaxMetadataBytes, limits.MaxSelectedBytes,
                    limits.MaxResidentBytes, limits.MaxSnapshotBytes, limits.MaxCopyWorkBytes, limits.MaxTotalLiveBytes,
                    limits.MaxTransactionBytes, limits.MaxIoBytes),
                new(usage.ConcurrentSelectedImages, usage.ResidentBytes, usage.SnapshotBytes, usage.TransferBufferBytes)),
                ImmutableArray.CreateRange(captured));
            Assert.Equal(AppPublication.OfflineOutcome.KnownNew, appAttempt.Receipt.Outcome);
            Assert.Equal(toolAttempt.Receipt.IntendedFileSha256.ToArray(), appAttempt.Receipt.IntendedFileSha256.ToArray());
            Assert.Equal(toolAttempt.Receipt.Charge.ActualIoBytes, appAttempt.Receipt.Charge.ActualIoBytes);
            // Stream the actual containers, not merely their receipts, without another arena allocation.
            using var left = File.OpenRead(tools.Target);
            using var right = File.OpenRead(host.Target);
            Assert.Equal(left.Length, right.Length);
            var l = new byte[32768]; var r = new byte[32768];
            while (left.Position < left.Length)
            {
                var count = (int)Math.Min(l.Length, left.Length - left.Position);
                left.ReadExactly(l.AsSpan(0, count)); right.ReadExactly(r.AsSpan(0, count));
                Assert.True(l.AsSpan(0, count).SequenceEqual(r.AsSpan(0, count)));
            }
            host.CopiesUnchanged();
        }
        finally { Directory.Delete(appDomain, true); }
    }

    [Fact]
    public void BothHostsLinkPublicationWithoutCliOrMachineInItsSurface()
    {
        foreach (var assembly in new[] { typeof(StorageCommand).Assembly, typeof(AppFiles).Assembly })
        {
            var publication = assembly.GetType("Hc2000.HostFiles.HddContainerPublication");
            Assert.NotNull(publication);
            foreach (var type in publication!.GetNestedTypes(BindingFlags.Public).Append(publication))
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var types = member switch
                    {
                        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
                        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
                        PropertyInfo property => new[] { property.PropertyType },
                        FieldInfo field => new[] { field.FieldType },
                        _ => Array.Empty<Type>()
                    };
                    Assert.All(types, fieldType => Assert.DoesNotContain("Hc2000.Machine", fieldType.ToString()));
                }
        }
        Assert.DoesNotContain(typeof(AppFiles).Assembly.GetReferencedAssemblies(), a => a.Name == "hc");
        Assert.Null(typeof(AppFiles).Assembly.GetType("Hc2000.Tools.StorageCommand"));
    }
}
