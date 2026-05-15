using FluentAssertions;
using WEDM.Domain.Models;
using WEDM.UI.ViewModels.Wizard;
using Xunit;

namespace WEDM.Engine.Tests.UI;

/// <summary>Guards ComboBox ItemsSource population (interaction is template-tested manually).</summary>
public sealed class WizardComboBoxDataTests
{
    [Fact]
    public void WelcomeViewModel_ExposesAllDeploymentEnvironments()
    {
        var vm = new WelcomeViewModel();
        vm.DeploymentEnvironmentOptions.Should().HaveCount(Enum.GetValues<DeploymentEnvironmentKind>().Length);
        vm.DeploymentEnvironmentOptions.Should().Contain(DeploymentEnvironmentKind.Dev);
        vm.DeploymentEnvironmentOptions.Should().Contain(DeploymentEnvironmentKind.Prod);
    }

    [Fact]
    public void VersionSelectionViewModel_ExposesMultipleTargetEnvironments()
    {
        var vm = new VersionSelectionViewModel();
        vm.Environments.Count.Should().BeGreaterThan(1);
        vm.Environments.Should().Contain("Production");
    }

    [Fact]
    public void DatabaseConfigViewModel_ExposesEnterpriseNlsCharsets()
    {
        var vm = new DatabaseConfigViewModel();
        vm.NlsCharsets.Should().HaveCountGreaterThan(4);
        vm.NlsCharsets[0].Should().Be("AL32UTF8");
        vm.NlsCharsets.Should().Contain("UTF8");
        vm.NlsCharsets.Should().Contain("ZHS16GBK");
    }
}
