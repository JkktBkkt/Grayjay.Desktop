using Grayjay.ClientServer.Controllers;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class WidevineLicenseStatusTests
{
    [DataTestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(429)]
    [DataRow(451)]
    public void ClientErrorsPassThrough(int upstreamCode)
    {
        Assert.AreEqual(upstreamCode, DetailsController.LicenseServerFailureStatus(upstreamCode));
    }

    [TestMethod]
    public void ConflictIsNotPassedThrough()
    {
        Assert.AreEqual(403, DetailsController.LicenseServerFailureStatus(409));
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(302)]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    public void OtherFailuresAreBadGateway(int upstreamCode)
    {
        Assert.AreEqual(502, DetailsController.LicenseServerFailureStatus(upstreamCode));
    }
}
