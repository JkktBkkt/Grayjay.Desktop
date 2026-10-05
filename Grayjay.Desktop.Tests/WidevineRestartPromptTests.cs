using Grayjay.ClientServer.States;

namespace Grayjay.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public class WidevineRestartPromptTests
{
    private const string DrmScript = "const drm = bridge.supportedFeatures.includes(\"HLSWidevineSource\");";
    private const string ClearScript = "source.getHome = function() { return []; };";

    // StateWidevine is process-wide and the flag only ever goes from false to true, so the cases run in this order.
    [TestMethod]
    public void MarksOnlyDrmScriptsEvaluatedWhileUnavailable()
    {
        try
        {
            StateWidevine.SetCdmLoadedAtStartup(false);
            Assert.IsFalse(StateWidevine.IsPlaybackAvailable);

            StateWidevine.NoteClientEvaluating(ClearScript);
            StateWidevine.NoteClientEvaluating(null);
            Assert.IsFalse(StateWidevine.ClientEvaluatedWithoutPlayback, "A script without HLSWidevineSource must not be marked.");

            StateWidevine.SetCdmLoadedAtStartup(true);
            StateWidevine.NoteClientEvaluating(DrmScript);
            Assert.IsFalse(StateWidevine.ClientEvaluatedWithoutPlayback, "A DRM script evaluated while playback is available must not be marked.");

            StateWidevine.SetCdmLoadedAtStartup(false);
            StateWidevine.NoteClientEvaluating(DrmScript);
            Assert.IsTrue(StateWidevine.ClientEvaluatedWithoutPlayback, "A DRM script evaluated while playback is unavailable must be marked.");
        }
        finally
        {
            StateWidevine.SetCdmLoadedAtStartup(false);
        }
    }
}
