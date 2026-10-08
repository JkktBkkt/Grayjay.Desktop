using System.Runtime.CompilerServices;
using Grayjay.ClientServer.Controllers;
using Grayjay.Engine.Models.Video.Additions;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class WidevineLicenseExecutorTests
{
    private sealed class FakeWidevineSource : IWidevineSource
    {
        private int _factoryCalls;
        public ManualResetEventSlim Gate { get; } = new ManualResetEventSlim(true);
        public SemaphoreSlim FactoryEntered { get; } = new SemaphoreSlim(0);
        public int FactoryCalls => Volatile.Read(ref _factoryCalls);

        public string LicenseUri => "https://license.example.com";
        public string? ServiceCertificate => null;
        public bool HasLicenseRequestExecutor => true;

        public RequestExecutor GetLicenseRequestExecutor()
        {
            Interlocked.Increment(ref _factoryCalls);
            FactoryEntered.Release();
            Gate.Wait();
            return (RequestExecutor)RuntimeHelpers.GetUninitializedObject(typeof(RequestExecutor));
        }
    }

    [TestMethod]
    public void CurrentGeneration_ReturnsCachedExecutor()
    {
        using var detailsState = new DetailsController.DetailsState();
        var source = new FakeWidevineSource();
        long generation = detailsState.CachedDashGeneration;

        var first = detailsState.GetOrCreateLicenseRequestExecutor(source, generation);
        var second = detailsState.GetOrCreateLicenseRequestExecutor(source, generation);

        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, source.FactoryCalls);
    }

    [TestMethod]
    public void StaleGeneration_ReturnsNullEvenWhenCached()
    {
        using var detailsState = new DetailsController.DetailsState();
        var source = new FakeWidevineSource();
        long staleGeneration = detailsState.CachedDashGeneration;
        Assert.IsNotNull(detailsState.GetOrCreateLicenseRequestExecutor(source, staleGeneration));

        detailsState.ClearCachedDash();

        Assert.IsNull(detailsState.GetOrCreateLicenseRequestExecutor(source, staleGeneration));
    }

    [TestMethod]
    public void StaleGeneration_DoesNotReturnTheNextVideosExecutor()
    {
        using var detailsState = new DetailsController.DetailsState();
        var source = new FakeWidevineSource();
        long staleGeneration = detailsState.CachedDashGeneration;
        detailsState.ClearCachedDash();
        Assert.IsNotNull(detailsState.GetOrCreateLicenseRequestExecutor(source, detailsState.CachedDashGeneration));

        Assert.IsNull(detailsState.GetOrCreateLicenseRequestExecutor(source, staleGeneration));
    }

    [TestMethod]
    public async Task ConcurrentRequests_CreateOneExecutor()
    {
        using var detailsState = new DetailsController.DetailsState();
        var source = new FakeWidevineSource();
        source.Gate.Reset();
        long generation = detailsState.CachedDashGeneration;

        var firstRequest = Task.Run(() => detailsState.GetOrCreateLicenseRequestExecutor(source, generation));
        Assert.IsTrue(await source.FactoryEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        var secondRequest = Task.Run(() => detailsState.GetOrCreateLicenseRequestExecutor(source, generation));
        await source.FactoryEntered.WaitAsync(TimeSpan.FromMilliseconds(500));
        source.Gate.Set();

        var first = await firstRequest;
        var second = await secondRequest;
        Assert.AreEqual(1, source.FactoryCalls);
        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
    }
}
