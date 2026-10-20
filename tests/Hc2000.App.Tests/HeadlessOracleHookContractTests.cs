using System.Reflection;
using Xunit;

namespace Hc2000.App.Tests;

// Metadata only: never invoke CpmWarmBoot, construct a worker, or boot a guest.
public sealed class HeadlessOracleHookContractTests
{
    private static MethodInfo WarmBoot => typeof(HeadlessOracle).GetMethod(
        "CpmWarmBoot", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void ExistingFourFlagsRemainOptionalAndDefaultFalse()
    {
        var flags = WarmBoot.GetParameters().Take(4).ToArray();
        Assert.Equal(new[] { "proveD", "proveDip", "proveDipCopy", "proveMbasic" },
            flags.Select(p => p.Name).ToArray());
        foreach (var flag in flags)
        {
            Assert.Equal(typeof(bool), flag.ParameterType);
            Assert.True(flag.IsOptional);
            Assert.Equal(false, flag.DefaultValue);
        }
    }

    [Fact]
    public void ProofCallbackIsAppendedOptionalNullAndUsesExistingContext()
    {
        var parameters = WarmBoot.GetParameters();
        Assert.Equal(5, parameters.Length);
        var callback = parameters[4];
        Assert.Equal("prove", callback.Name);
        Assert.True(callback.IsOptional);
        Assert.Null(callback.DefaultValue);
        var context = typeof(HeadlessOracle).GetNestedType("OracleContext", BindingFlags.NonPublic);
        Assert.NotNull(context);
        Assert.Equal(typeof(Func<,,>).MakeGenericType(context, typeof(string), typeof(Task)),
            callback.ParameterType);
    }

    [Fact]
    public void WarmBootRetainsPrivateInstanceAsyncTaskContract()
    {
        Assert.True(WarmBoot.IsPrivate);
        Assert.False(WarmBoot.IsStatic);
        Assert.Equal(typeof(Task), WarmBoot.ReturnType);
        Assert.NotNull(WarmBoot.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>());
    }
}
