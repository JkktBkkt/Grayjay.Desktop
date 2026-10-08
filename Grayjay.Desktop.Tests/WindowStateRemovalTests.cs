using Grayjay.ClientServer.States;
using Microsoft.AspNetCore.Http;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class WindowStateRemovalTests
{
    private static DefaultHttpContext Context(string windowId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["WindowID"] = windowId;
        return context;
    }

    [TestMethod]
    public void RemoveState_DisposesAndForgetsTheWindow()
    {
        var windowId = Guid.NewGuid().ToString();
        var state = Context(windowId).GetState();
        var temporaryDirectory = state.LocalMedia.CreateTemporaryDirectory();

        StateWindow.RemoveState(windowId);

        Assert.IsFalse(StateWindow.GetAllStates().Contains(state));
        Assert.IsFalse(Directory.Exists(temporaryDirectory));
    }

    [TestMethod]
    public void RemoveState_RejectsLaterVideoLoads()
    {
        var windowId = Guid.NewGuid().ToString();
        var state = Context(windowId).GetState();
        Assert.IsTrue(state.DetailsState.TryBeginVideoLoad(10, out _));

        StateWindow.RemoveState(windowId);

        Assert.IsFalse(state.DetailsState.IsCurrentVideoRequest(10));
        Assert.IsFalse(state.DetailsState.TryBeginVideoLoad(11, out _));
    }

    [TestMethod]
    public void RemoveState_UnknownId_DoesNothing()
    {
        var windowId = Guid.NewGuid().ToString();
        var state = Context(windowId).GetState();

        StateWindow.RemoveState(Guid.NewGuid().ToString());

        Assert.IsTrue(StateWindow.GetAllStates().Contains(state));
        StateWindow.RemoveState(windowId);
    }

    [TestMethod]
    public void GetWindowId_ReadsHeaderThenQuery()
    {
        var context = new DefaultHttpContext();
        Assert.IsNull(StateWindow.GetWindowId(context));

        context.Request.QueryString = new QueryString("?windowId=from-query");
        Assert.AreEqual("from-query", StateWindow.GetWindowId(context));

        context.Request.Headers["WindowID"] = "from-header";
        Assert.AreEqual("from-header", StateWindow.GetWindowId(context));
    }
}
