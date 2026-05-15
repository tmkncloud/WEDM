using WEDM.Engine.Discovery.Scanners;
using Xunit;

namespace WEDM.Engine.Tests.Discovery;

public sealed class FormsBinaryScannerTests
{
    [Fact]
    public void DetectDependencies_FindsWebUtilInBinaryPayload()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm-fmb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var fmb = Path.Combine(temp, "sample.fmb");
        var bytes = new byte[] { 0x00, 0x01, 0x57, 0x45, 0x42, 0x55, 0x54, 0x49, 0x4C, 0xFF };
        File.WriteAllBytes(fmb, bytes);

        var deps = FormsBinaryScanner.DetectDependencies(fmb);

        Assert.Contains(deps, d => d.Contains("WEBUTIL", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(temp, true);
    }

    [Fact]
    public void ScanFile_ReturnsFalse_ForUnrelatedBinary()
    {
        var temp = Path.Combine(Path.GetTempPath(), "wedm-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "data.bin");
        File.WriteAllBytes(file, [0x00, 0x01, 0x02, 0x03]);

        Assert.False(FormsBinaryScanner.ContainsPattern(file, "WEBUTIL"));

        Directory.Delete(temp, true);
    }
}
