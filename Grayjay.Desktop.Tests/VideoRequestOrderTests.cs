using System.Runtime.CompilerServices;
using Grayjay.ClientServer.Controllers;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Playback;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class VideoRequestOrderTests
{
    private const string VideoUrl = "https://test/video";

    private static void Begin(DetailsController.DetailsState state, long requestId)
    {
        Assert.IsTrue(state.TryBeginVideoLoad(requestId, out _));
    }

    private static bool Commit(DetailsController.DetailsState state, long requestId, PlaybackTracker? tracker = null)
    {
        return state.TryCommitVideoLoad(requestId, new PlatformVideoDetails() { Url = VideoUrl }, null, tracker, out _);
    }

    private static long BeginReload(DetailsController.DetailsState state)
    {
        Assert.IsTrue(state.TryBeginReload(out var requestId, out var url, out _));
        Assert.AreEqual(VideoUrl, url);
        return requestId;
    }

    // The tracker is only handed around here, so its plugin object is never needed.
    private static PlaybackTracker CreateTracker()
        => (PlaybackTracker)RuntimeHelpers.GetUninitializedObject(typeof(PlaybackTracker));

    [TestMethod]
    public void SingleLoad_Commits()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);

        Assert.IsTrue(state.IsCurrentVideoRequest(10));
        Assert.IsTrue(Commit(state, 10));
    }

    [TestMethod]
    public void NewerLoad_SupersedesOlderLoad()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Begin(state, 11);

        Assert.IsTrue(Commit(state, 11));
        Assert.IsFalse(Commit(state, 10));
    }

    [TestMethod]
    public void OlderLoad_ArrivingAfterNewerLoad_IsRejected()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 11);

        Assert.IsFalse(state.TryBeginVideoLoad(10, out _));
    }

    [TestMethod]
    public void NextLoad_TakesTheCommittedTracker()
    {
        var state = new DetailsController.DetailsState();
        var tracker = CreateTracker();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10, tracker));

        Assert.IsTrue(state.TryBeginVideoLoad(11, out var previousTracker));
        Assert.AreSame(tracker, previousTracker);
        Assert.IsNull(state.CloseVideo(12));
    }

    [TestMethod]
    public void Close_SupersedesLoadInFlight()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        state.CloseVideo(11);

        Assert.IsFalse(state.IsCurrentVideoRequest(10));
        Assert.IsFalse(Commit(state, 10));
    }

    [TestMethod]
    public void StaleClose_DoesNotSupersedeNewerLoad()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 11);
        state.CloseVideo(10);

        Assert.IsTrue(Commit(state, 11));
    }

    [TestMethod]
    public void LoadAfterClose_Commits()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        state.CloseVideo(11);
        Begin(state, 12);

        Assert.IsTrue(Commit(state, 12));
    }

    [TestMethod]
    public void Reload_OfCommittedVideo_Commits()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));

        Assert.IsTrue(Commit(state, BeginReload(state)));
    }

    [TestMethod]
    public void Reload_LosesToNewerUserLoad()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));
        var reloadRequestId = BeginReload(state);
        Begin(state, 11);

        Assert.AreEqual(10, reloadRequestId);
        Assert.IsTrue(Commit(state, 11));
        Assert.IsFalse(Commit(state, reloadRequestId));
    }

    [TestMethod]
    public void Reload_WhileUserLoadInFlight_IsRejected()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));
        Begin(state, 11);

        Assert.IsFalse(state.TryBeginReload(out _, out _, out _));
    }

    [TestMethod]
    public void Reload_AfterUserLoadCommits_UsesItsId()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));
        Begin(state, 11);
        Assert.IsTrue(Commit(state, 11));
        var reloadRequestId = BeginReload(state);

        Assert.AreEqual(11, reloadRequestId);
        Assert.IsTrue(Commit(state, reloadRequestId));
    }

    [TestMethod]
    public void Reload_AfterFailedUserLoad_IsRejected()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));
        // The load for 11 threw before committing.
        Begin(state, 11);

        Assert.IsFalse(state.TryBeginReload(out _, out _, out _));
    }

    [TestMethod]
    public void Reload_AfterClose_IsRejected()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));
        state.CloseVideo(11);

        Assert.IsFalse(state.TryBeginReload(out _, out _, out _));
    }

    [TestMethod]
    public void Reload_AfterDispose_IsRejected()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(Commit(state, 10));
        state.Dispose();

        Assert.IsFalse(state.TryBeginReload(out _, out _, out _));
    }

    [TestMethod]
    public void Reload_WithoutLoadedVideo_IsRejected()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Assert.IsTrue(state.TryCommitVideoLoad(10, null, null, null, out _));

        Assert.IsFalse(state.TryBeginReload(out _, out _, out _));
    }

    [TestMethod]
    public void Dispose_RejectsLoadsAndCommits()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        state.Dispose();

        Assert.IsFalse(state.IsCurrentVideoRequest(10));
        Assert.IsFalse(Commit(state, 10));
        Assert.IsFalse(state.TryBeginVideoLoad(11, out _));
    }

    [TestMethod]
    public void Dispose_BumpsVideoGeneration()
    {
        var state = new DetailsController.DetailsState();
        var generation = state.CachedDashGeneration;
        state.Dispose();

        Assert.AreNotEqual(generation, state.CachedDashGeneration);
    }

    [TestMethod]
    public void Commit_BumpsVideoGeneration()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        var generation = state.CachedDashGeneration;

        Assert.IsTrue(Commit(state, 10));
        Assert.AreNotEqual(generation, state.CachedDashGeneration);
    }

    [TestMethod]
    public void SupersededCommit_KeepsVideoGeneration()
    {
        var state = new DetailsController.DetailsState();
        Begin(state, 10);
        Begin(state, 11);
        Assert.IsTrue(Commit(state, 11));
        var generation = state.CachedDashGeneration;

        Assert.IsFalse(Commit(state, 10));
        Assert.AreEqual(generation, state.CachedDashGeneration);
    }
}
