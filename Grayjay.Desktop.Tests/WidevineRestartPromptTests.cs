using Grayjay.ClientServer.States;
using JustCef;

namespace Grayjay.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public class WidevineRestartPromptTests
{
    private const string DrmScript = "const drm = bridge.supportedFeatures.includes(\"HLSWidevineSource\");";
    private const string ClearScript = "source.getHome = function() { return []; };";
    private static readonly WidevineStatus Ready = new(WidevineState.Ready, WidevineUnavailableReason.NotApplicable, null, "4.10.2934.0");
    private static readonly WidevineStatus Unavailable = new(WidevineState.Unavailable, WidevineUnavailableReason.NoUsableCdm, null, null);

    // StateWidevine is process-wide and the flag only ever goes from false to true, so the cases run in this order.
    [TestMethod]
    public void MarksOnlyDrmScriptsEvaluatedWhileUnavailable()
    {
        try
        {
            StateWidevine.SetStatus(Unavailable);
            Assert.IsFalse(StateWidevine.IsPlaybackAvailable);

            StateWidevine.NoteClientEvaluating(ClearScript);
            StateWidevine.NoteClientEvaluating(null);
            Assert.IsFalse(StateWidevine.ClientEvaluatedWithoutPlayback, "A script without HLSWidevineSource must not be marked.");

            StateWidevine.SetStatus(Ready);
            StateWidevine.NoteClientEvaluating(DrmScript);
            Assert.IsFalse(StateWidevine.ClientEvaluatedWithoutPlayback, "A DRM script evaluated while playback is available must not be marked.");

            StateWidevine.SetStatus(Unavailable);
            StateWidevine.NoteClientEvaluating(DrmScript);
            Assert.IsTrue(StateWidevine.ClientEvaluatedWithoutPlayback, "A DRM script evaluated while playback is unavailable must be marked.");
        }
        finally
        {
            StateWidevine.SetStatus(Unavailable);
        }
    }
}
