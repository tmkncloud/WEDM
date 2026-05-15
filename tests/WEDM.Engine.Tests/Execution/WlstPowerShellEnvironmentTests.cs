using WEDM.Domain.Models;
using WEDM.Engine.Execution;
using Xunit;

namespace WEDM.Engine.Tests.Execution;

public sealed class WlstPowerShellEnvironmentTests
{
    [Fact]
    public void FromDeployment_UsesMiddlewareHomeAndConfiguredJavaHome()
    {
        var config = new DeploymentConfiguration
        {
            Paths = new PathConfiguration { MiddlewareHome = @"D:\Oracle\MW" },
            Java  = new JavaConfiguration { JavaHome = @"D:\Java\jdk-17" },
        };

        var env = WlstPowerShellEnvironment.FromDeployment(config);

        Assert.Equal(@"D:\Oracle\MW", env.OracleHome);
        Assert.Equal(@"D:\Java\jdk-17", env.JavaHome);
    }

    [Fact]
    public void BuildWlstLaunchBody_InjectsOracleAndJavaHome()
    {
        var body = WlstPowerShellEnvironment.BuildWlstLaunchBody(
            @"D:\Oracle\MW\wlserver\common\bin\wlst.cmd",
            @"D:\temp\create.py",
            new WlstExecutionEnvironment
            {
                OracleHome = @"D:\Oracle\MW",
                JavaHome   = @"D:\Java\jdk-17",
            });

        Assert.Contains("$env:ORACLE_HOME = 'D:\\Oracle\\MW'", body);
        Assert.Contains("$env:JAVA_HOME = 'D:\\Java\\jdk-17'", body);
        Assert.Contains("$env:PATH", body);
        Assert.Contains("Start-Process", body);
    }
}
