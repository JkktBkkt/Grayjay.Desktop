using System.Reflection;
using System.Runtime.ExceptionServices;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.Models.Downloads;
using Grayjay.ClientServer.States;
using Grayjay.ClientServer.Store;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Video.Sources;
using PlatformID = Grayjay.Engine.Models.General.PlatformID;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class DownloadFilePathTests
{
    private string _directory = null!;

    private string StoreDirectory => Path.Combine(_directory, "store");
    private string OldDownloads => Path.Combine(_directory, "old-downloads");
    private string NewDownloads => Path.Combine(_directory, "new-downloads");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "download-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StoreDirectory);
        Directory.CreateDirectory(OldDownloads);
        Directory.CreateDirectory(NewDownloads);
    }

    [TestCleanup]
    public void Cleanup()
    {
        DeleteDirectory(_directory);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path))
        {
            File.Delete(file);
        }
        foreach (string child in Directory.EnumerateDirectories(path))
        {
            DeleteDirectory(child);
        }
        Directory.Delete(path);
    }

    private static T Invoke<T>(Type type, string name, params object?[] arguments)
    {
        var method = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        try
        {
            return (T)method.Invoke(null, arguments)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private ManagedStore<VideoLocal> OpenStore()
        => new ManagedStore<VideoLocal>("downloaded", new DirectoryInfo(StoreDirectory))
            .WithUnique(local => local.VideoDetails.ID)
            .WithKeepUnloadable()
            .Load();

    private StringStore OpenSetting() => new StringStore("downloads_directory", null, new DirectoryInfo(StoreDirectory)).Load();

    private static string WriteFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "data " + name);
        return path;
    }

    private static VideoLocal Local(string id, string videoPath, string? audioPath = null, string? subtitlePath = null)
    {
        var local = new VideoLocal(new PlatformVideoDetails
        {
            ID = new PlatformID { Value = id },
            Name = "Paths: " + id,
            Url = "https://test/" + id
        });
        local.VideoSources.Add(new LocalVideoSource { Container = "video/mp4", Width = 640, Height = 360, FilePath = videoPath });
        if (audioPath != null)
        {
            local.AudioSources.Add(new LocalAudioSource { Container = "audio/mp4", FilePath = audioPath });
        }
        if (subtitlePath != null)
        {
            local.SubtitleSources.Add(new LocalSubtitleSource { Name = "English", Format = "text/vtt", FilePath = subtitlePath });
        }
        return local;
    }

    private static VideoLocal Record(ManagedStore<VideoLocal> store, string id) => store.GetObjects().Single(local => local.VideoDetails.ID.Value == id);

    private int ResolveMovedFiles(string downloadsDirectory, ManagedStore<VideoLocal> store)
        => Invoke<int>(typeof(StateDownloads), "ResolveMovedFiles", downloadsDirectory, store);

    private bool MoveDownloads(string newDirectory, ManagedStore<VideoLocal> store, StringStore setting, string? defaultDirectory = null, Action<int>? onMoving = null, Action<int, int>? onProgress = null)
        => Invoke<bool>(typeof(StateDownloads), "MoveDownloads", newDirectory, store, setting, defaultDirectory ?? OldDownloads, onMoving, onProgress);

    private static void RunCleanup(string downloadsDirectory, ManagedStore<VideoLocal> store, bool deleteUnknown)
        => Invoke<object?>(typeof(StateDownloads), "CleanupFiles", downloadsDirectory, store, deleteUnknown);

    private static bool DeleteDownloaded(VideoLocal local, ManagedStore<VideoLocal> store)
        => Invoke<bool>(typeof(StateDownloads), "DeleteDownloaded", local, store);

    private static void DeletePartialFiles(VideoDownload download, ManagedStore<VideoLocal> store)
        => Invoke<object?>(typeof(StateDownloads), "DeletePartialFiles", download, store);

    [DataTestMethod]
    [DataRow(@"C:\Users\user\Desktop\Grayjay.Desktop-win-x64-v2\Grayjay.Desktop-win-x64-v2\downloads\")]
    [DataRow("/home/user/old-portable/downloads/")]
    public void RecordsWithAnOldFolderPointAtTheFileInTheDownloadsFolder(string oldFolder)
    {
        string video = WriteFile(NewDownloads, "Mt5Uat93SOY__1920x1080_.mp4");
        string audio = WriteFile(NewDownloads, "Mt5Uat93SOY__en-128000_.mp4a");
        string subtitle = WriteFile(NewDownloads, "Mt5Uat93SOY__English_.vtt");
        string staleDash = Path.Combine(NewDownloads, "Mt5Uat93SOY__1920x1080_.mp4.dash");
        Directory.CreateDirectory(Path.Combine(staleDash, "OLDFINGERPRINT"));
        File.WriteAllText(Path.Combine(staleDash, "OLDFINGERPRINT", "manifest.mpd"), "<MPD/>");
        var store = OpenStore();
        store.Save(Local("moved", oldFolder + "Mt5Uat93SOY__1920x1080_.mp4", oldFolder + "Mt5Uat93SOY__en-128000_.mp4a", oldFolder + "Mt5Uat93SOY__English_.vtt"));
        string presentVideo = WriteFile(OldDownloads, "present__640x360_.mp4");
        store.Save(Local("present", presentVideo));

        Assert.AreEqual(1, ResolveMovedFiles(NewDownloads, store));

        var reloaded = OpenStore();
        var moved = Record(reloaded, "moved");
        Assert.AreEqual(video, moved.VideoSources[0].FilePath);
        Assert.AreEqual(audio, moved.AudioSources[0].FilePath);
        Assert.AreEqual(subtitle, moved.SubtitleSources[0].FilePath);
        Assert.IsFalse(Directory.Exists(staleDash));
        Assert.AreEqual(presentVideo, Record(reloaded, "present").VideoSources[0].FilePath);
        Assert.AreEqual(0, ResolveMovedFiles(NewDownloads, reloaded));
    }

    [TestMethod]
    public void ResolvedRecordsExportAndDeleteTheirFiles()
    {
        string video = WriteFile(NewDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", @"D:\Grayjay\downloads\abc__640x360_.mp4"));
        ResolveMovedFiles(NewDownloads, store);
        var local = Record(OpenStore(), "abc");
        string exportFolder = Path.Combine(_directory, "export");
        Directory.CreateDirectory(exportFolder);

        Assert.IsNull(Invoke<string?>(typeof(DownloadController), "ExportToFolder", local, exportFolder));
        Assert.AreEqual(1, Directory.GetFiles(exportFolder).Length);

        local.DeleteFiles();
        Assert.IsFalse(File.Exists(video));
    }

    [TestMethod]
    public void RecordsWhoseFileIsNowhereAreLeftAlone()
    {
        var store = OpenStore();
        store.Save(Local("gone", Path.Combine(OldDownloads, "gone__640x360_.mp4")));

        Assert.AreEqual(0, ResolveMovedFiles(NewDownloads, store));
        Assert.AreEqual(Path.Combine(OldDownloads, "gone__640x360_.mp4"), Record(OpenStore(), "gone").VideoSources[0].FilePath);
    }

    [TestMethod]
    public void ExportOfAMissingFileNamesTheFile()
    {
        string missing = Path.Combine(OldDownloads, "missing__640x360_.mp4");
        string audio = WriteFile(OldDownloads, "missing__en-128000_.mp4a");

        string? failure = Invoke<string?>(typeof(DownloadController), "ExportToFolder", Local("missing", missing, audio), NewDownloads);

        Assert.IsNotNull(failure);
        StringAssert.Contains(failure, "Downloaded file is missing");
        StringAssert.Contains(failure, missing);
        Assert.AreEqual(0, Directory.GetFiles(NewDownloads).Length);
    }

    [TestMethod]
    public void MissingExportFileSkipsAbsentSourcesAndFindsTheMissingOne()
    {
        string present = WriteFile(OldDownloads, "present__640x360_.mp4");
        string missing = Path.Combine(OldDownloads, "missing.vtt");

        Assert.IsNull(Invoke<string?>(typeof(DownloadController), "MissingExportFile", new object?[] { new string?[] { present, null, "" } }));
        Assert.AreEqual(missing, Invoke<string?>(typeof(DownloadController), "MissingExportFile", new object?[] { new string?[] { present, null, missing } }));
    }

    [TestMethod]
    public void ChangingTheFolderMovesTheFilesAndUpdatesRecordsAndSetting()
    {
        string video = WriteFile(OldDownloads, "abc__1920x1080_.mp4");
        string audio = WriteFile(OldDownloads, "abc__en-128000_.mp4a");
        string subtitle = WriteFile(OldDownloads, "abc__English_.vtt");
        string dash = video + ".dash";
        Directory.CreateDirectory(Path.Combine(dash, "FINGERPRINT"));
        File.WriteAllText(Path.Combine(dash, "FINGERPRINT", "manifest.mpd"), "<MPD/>");
        string other = WriteFile(OldDownloads, "other__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", video, audio, subtitle));
        store.Save(Local("other", other));
        var setting = OpenSetting();

        MoveDownloads(NewDownloads, store, setting);

        Assert.AreEqual(0, Directory.GetFiles(OldDownloads).Length);
        Assert.IsFalse(Directory.Exists(dash));
        var reloaded = OpenStore();
        var abc = Record(reloaded, "abc");
        Assert.AreEqual(Path.Combine(NewDownloads, "abc__1920x1080_.mp4"), abc.VideoSources[0].FilePath);
        Assert.AreEqual(Path.Combine(NewDownloads, "abc__en-128000_.mp4a"), abc.AudioSources[0].FilePath);
        Assert.AreEqual(Path.Combine(NewDownloads, "abc__English_.vtt"), abc.SubtitleSources[0].FilePath);
        Assert.AreEqual("data abc__1920x1080_.mp4", File.ReadAllText(abc.VideoSources[0].FilePath));
        Assert.AreEqual(Path.Combine(NewDownloads, "other__640x360_.mp4"), Record(reloaded, "other").VideoSources[0].FilePath);
        Assert.AreEqual(NewDownloads, OpenSetting().GetCopy());
    }

    [TestMethod]
    public void MovingBackToTheDefaultFolderClearsTheSetting()
    {
        string video = WriteFile(NewDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", video));
        var setting = OpenSetting();
        setting.Save(NewDownloads);

        MoveDownloads(OldDownloads, store, setting, OldDownloads);

        Assert.IsNull(OpenSetting().GetCopy());
        Assert.AreEqual(Path.Combine(OldDownloads, "abc__640x360_.mp4"), Record(OpenStore(), "abc").VideoSources[0].FilePath);
    }

    [TestMethod]
    public void AFolderWithOtherFilesIsRefused()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string personal = WriteFile(NewDownloads, "holiday.jpg");
        var store = OpenStore();
        store.Save(Local("abc", video));
        var setting = OpenSetting();

        var exception = Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, setting));

        StringAssert.Contains(exception.Message, "holiday.jpg");
        Assert.IsTrue(File.Exists(video));
        Assert.IsTrue(File.Exists(personal));
        Assert.AreEqual(video, Record(OpenStore(), "abc").VideoSources[0].FilePath);
        Assert.IsNull(OpenSetting().GetCopy());
    }

    [TestMethod]
    public void AFolderWithOnlyFileManagerFilesCountsAsEmpty()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string finderFile = WriteFile(NewDownloads, ".DS_Store");
        string explorerFile = WriteFile(NewDownloads, "desktop.ini");
        string thumbnails = WriteFile(NewDownloads, "Thumbs.db");
        string dolphinFile = WriteFile(NewDownloads, ".directory");
        var store = OpenStore();
        store.Save(Local("abc", video));

        MoveDownloads(NewDownloads, store, OpenSetting());

        Assert.AreEqual(Path.Combine(NewDownloads, "abc__640x360_.mp4"), Record(OpenStore(), "abc").VideoSources[0].FilePath);
        Assert.IsTrue(File.Exists(finderFile));
        Assert.IsTrue(File.Exists(explorerFile));
        Assert.IsTrue(File.Exists(thumbnails));
        Assert.IsTrue(File.Exists(dolphinFile));
    }

    [TestMethod]
    public void TheFileCountIsReportedBeforeAnyFileMoves()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string audio = WriteFile(OldDownloads, "abc__en-128000_.mp4a");
        var store = OpenStore();
        store.Save(Local("abc", video, audio));
        var reports = new List<int>();

        MoveDownloads(NewDownloads, store, OpenSetting(), onMoving: fileCount =>
        {
            reports.Add(fileCount);
            Assert.IsTrue(File.Exists(video));
            Assert.AreEqual(0, Directory.GetFiles(NewDownloads).Length);
        });

        CollectionAssert.AreEqual(new[] { 2 }, reports);
        Assert.IsFalse(File.Exists(video));
    }

    [TestMethod]
    public void NothingIsReportedWhenNoFileHasToMove()
    {
        WriteFile(NewDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", Path.Combine(OldDownloads, "abc__640x360_.mp4")));
        var reports = new List<int>();

        MoveDownloads(NewDownloads, store, OpenSetting(), onMoving: reports.Add);

        Assert.AreEqual(0, reports.Count);
    }

    [TestMethod]
    public void AFolderWithASubfolderIsRefused()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        Directory.CreateDirectory(Path.Combine(NewDownloads, "photos"));
        var store = OpenStore();
        store.Save(Local("abc", video));

        Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, OpenSetting()));
        Assert.IsTrue(File.Exists(video));
    }

    [TestMethod]
    public void FilesAlreadyMovedByTheOldFolderButtonAreAdopted()
    {
        string movedEarlier = WriteFile(NewDownloads, "abc__640x360_.mp4");
        string stillOld = WriteFile(OldDownloads, "def__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", Path.Combine(OldDownloads, "abc__640x360_.mp4")));
        store.Save(Local("def", stillOld));

        MoveDownloads(NewDownloads, store, OpenSetting());

        var reloaded = OpenStore();
        Assert.AreEqual(movedEarlier, Record(reloaded, "abc").VideoSources[0].FilePath);
        Assert.AreEqual(Path.Combine(NewDownloads, "def__640x360_.mp4"), Record(reloaded, "def").VideoSources[0].FilePath);
        Assert.IsFalse(File.Exists(stillOld));
    }

    [TestMethod]
    public void ACopyLeftByAnInterruptedMoveIsReplaced()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string target = Path.Combine(NewDownloads, "abc__640x360_.mp4");
        File.WriteAllText(target, "partial");
        var store = OpenStore();
        store.Save(Local("abc", video));

        MoveDownloads(NewDownloads, store, OpenSetting());

        Assert.IsFalse(File.Exists(video));
        Assert.AreEqual("data abc__640x360_.mp4", File.ReadAllText(target));
        Assert.AreEqual(target, Record(OpenStore(), "abc").VideoSources[0].FilePath);
        Assert.AreEqual(NewDownloads, OpenSetting().GetCopy());
    }

    [TestMethod]
    public void AFileAnotherRecordPointsAtIsNotReplaced()
    {
        string oldVideo = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string newVideo = WriteFile(NewDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", oldVideo));
        store.Save(Local("other", newVideo));

        var exception = Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, OpenSetting()));

        StringAssert.Contains(exception.Message, "same name");
        Assert.AreEqual("data abc__640x360_.mp4", File.ReadAllText(newVideo));
        Assert.IsTrue(File.Exists(oldVideo));
        Assert.AreEqual(oldVideo, Record(OpenStore(), "abc").VideoSources[0].FilePath);
        Assert.IsNull(OpenSetting().GetCopy());
    }

    [TestMethod]
    public void AFolderThatDoesNotExistIsRefused()
    {
        var store = OpenStore();
        store.Save(Local("abc", WriteFile(OldDownloads, "abc__640x360_.mp4")));

        Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(Path.Combine(_directory, "nowhere"), store, OpenSetting()));
    }

    [TestMethod]
    public void UnloadableRecordsBlockTheMove()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", video));
        File.WriteAllText(Path.Combine(StoreDirectory, "downloaded", "broken"), "{ truncated");
        var reloaded = OpenStore();

        Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, reloaded, OpenSetting()));
        Assert.IsTrue(File.Exists(video));
    }

    [TestMethod]
    public void AFailedMoveMovesTheFilesBackAndKeepsTheSetting()
    {
        string video = WriteFile(OldDownloads, "aaa__640x360_.mp4");
        string lockedFolder = Path.Combine(_directory, "locked");
        Directory.CreateDirectory(lockedFolder);
        string audio = WriteFile(lockedFolder, "aaa__en-128000_.mp4a");
        var store = OpenStore();
        store.Save(Local("aaa", video, audio));
        var setting = OpenSetting();
        setting.Save("/previous/folder");

        //The video moves first, then the audio cannot leave its folder
        InvalidOperationException exception;
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                exception = Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, setting));
            }
        }
        else
        {
            File.SetUnixFileMode(lockedFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                if (CanWriteTo(lockedFolder))
                {
                    Assert.Inconclusive("Folder permissions are not enforced for this user.");
                }
                exception = Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, setting));
            }
            finally
            {
                File.SetUnixFileMode(lockedFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        StringAssert.Contains(exception.Message, "moved back");
        StringAssert.Contains(exception.Message, audio);
        Assert.IsTrue(File.Exists(video));
        Assert.IsTrue(File.Exists(audio));
        Assert.AreEqual(0, Directory.GetFiles(NewDownloads).Length);
        Assert.AreEqual("/previous/folder", OpenSetting().GetCopy());
        var aaa = Record(OpenStore(), "aaa");
        Assert.AreEqual(video, aaa.VideoSources[0].FilePath);
        Assert.AreEqual(audio, aaa.AudioSources[0].FilePath);
    }

    private static bool CanWriteTo(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, "probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [TestMethod]
    public void ChangingTheFolderIsRefusedWhileDownloading()
    {
        var property = typeof(StateDownloads).GetProperty(nameof(StateDownloads.IsDownloading))!;
        bool previous = StateDownloads.IsDownloading;
        property.SetValue(null, true);
        try
        {
            //A folder that does not exist, so a broken guard fails on validation and never moves the real downloads
            var exception = Assert.ThrowsException<InvalidOperationException>(() => StateDownloads.ChangeDownloadDirectory(Path.Combine(_directory, "nowhere")));
            StringAssert.Contains(exception.Message, "in progress");
        }
        finally
        {
            property.SetValue(null, previous);
        }
    }

    [TestMethod]
    public void ChangingTheFolderIsRefusedWhileAMoveRuns()
    {
        var field = typeof(StateDownloads).GetField("_movingDownloads", BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, true);
        try
        {
            //A folder that does not exist, so a broken guard fails on validation and never moves the real downloads
            var exception = Assert.ThrowsException<InvalidOperationException>(() => StateDownloads.ChangeDownloadDirectory(Path.Combine(_directory, "nowhere")));
            StringAssert.Contains(exception.Message, "already being moved");
        }
        finally
        {
            field.SetValue(null, false);
        }
    }

    [TestMethod]
    public void CleanupOfAChosenFolderDeletesNothing()
    {
        string known = WriteFile(NewDownloads, "abc__640x360_.mp4");
        string orphan = WriteFile(NewDownloads, "zzz__640x360_.mp4");
        string orphanSubtitle = WriteFile(NewDownloads, "zzz__English_.vtt");
        string personal = WriteFile(NewDownloads, "holiday.jpg");
        string personalTrip = WriteFile(NewDownloads, "Trip__2023-07_.mp4");
        string orphanDash = Path.Combine(NewDownloads, "zzz__640x360_.mp4.dash");
        Directory.CreateDirectory(orphanDash);
        var store = OpenStore();
        store.Save(Local("abc", known));

        RunCleanup(NewDownloads, store, false);

        Assert.IsTrue(File.Exists(known));
        Assert.IsTrue(File.Exists(orphan));
        Assert.IsTrue(File.Exists(orphanSubtitle));
        Assert.IsTrue(File.Exists(personal));
        Assert.IsTrue(File.Exists(personalTrip));
        Assert.IsTrue(Directory.Exists(orphanDash));
    }

    [TestMethod]
    public void CleanupKnowsFilesRecordedWithWindowsPaths()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string orphan = WriteFile(OldDownloads, "zzz__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", @"C:\Users\user\Grayjay\downloads\abc__640x360_.mp4"));

        RunCleanup(OldDownloads, store, true);

        Assert.IsTrue(File.Exists(video));
        Assert.IsFalse(File.Exists(orphan));
    }

    [TestMethod]
    public void ChosenFolderSettingFallsBackWhenTheFolderIsGone()
    {
        string fallback = Path.Combine(_directory, "default");

        Assert.AreEqual(fallback, Invoke<string>(typeof(StateDownloads), "ChooseDownloadsDirectory", null, fallback));
        Assert.AreEqual(fallback, Invoke<string>(typeof(StateDownloads), "ChooseDownloadsDirectory", Path.Combine(_directory, "unplugged"), fallback));
        Assert.AreEqual(NewDownloads, Invoke<string>(typeof(StateDownloads), "ChooseDownloadsDirectory", NewDownloads, fallback));
    }

    [TestMethod]
    public void ChoosingTheCurrentFolderFinishesAnInterruptedMove()
    {
        string movedVideo = WriteFile(NewDownloads, "abc__640x360_.mp4");
        string audioLeftBehind = WriteFile(OldDownloads, "abc__en-128000_.mp4a");
        string personal = WriteFile(NewDownloads, "holiday.jpg");
        var store = OpenStore();
        store.Save(Local("abc", movedVideo, audioLeftBehind));
        var setting = OpenSetting();
        setting.Save(NewDownloads);

        bool changed = MoveDownloads(NewDownloads, store, setting);

        Assert.IsTrue(changed);
        Assert.IsFalse(File.Exists(audioLeftBehind));
        Assert.IsTrue(File.Exists(personal));
        Assert.AreEqual(Path.Combine(NewDownloads, "abc__en-128000_.mp4a"), Record(OpenStore(), "abc").AudioSources[0].FilePath);
        Assert.AreEqual(NewDownloads, OpenSetting().GetCopy());
    }

    [TestMethod]
    public void ChoosingTheCurrentFolderWithNothingToMoveChangesNothing()
    {
        string video = WriteFile(NewDownloads, "abc__640x360_.mp4");
        string personal = WriteFile(NewDownloads, "holiday.jpg");
        var store = OpenStore();
        store.Save(Local("abc", video));
        var setting = OpenSetting();
        setting.Save(NewDownloads);
        var reports = new List<int>();

        bool changed = MoveDownloads(NewDownloads, store, setting, onMoving: reports.Add);

        Assert.IsFalse(changed);
        Assert.AreEqual(0, reports.Count);
        Assert.IsTrue(File.Exists(personal));
        Assert.AreEqual(video, Record(OpenStore(), "abc").VideoSources[0].FilePath);
        Assert.AreEqual(NewDownloads, OpenSetting().GetCopy());
    }

    [TestMethod]
    public void ChoosingTheDefaultFolderWhileOnTheFallbackClearsTheSetting()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string leftover = WriteFile(OldDownloads, "zzz__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", video));
        var setting = OpenSetting();
        setting.Save(Path.Combine(_directory, "unplugged"));

        bool changed = MoveDownloads(OldDownloads, store, setting, OldDownloads);

        Assert.IsTrue(changed);
        Assert.IsNull(OpenSetting().GetCopy());
        Assert.IsTrue(File.Exists(leftover));
        Assert.AreEqual(video, Record(OpenStore(), "abc").VideoSources[0].FilePath);
    }

    [TestMethod]
    public void ADriveRootWithSystemFoldersIsAccepted()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string lostAndFound = Path.Combine(NewDownloads, "lost+found");
        string trash = Path.Combine(NewDownloads, ".Trash-1000");
        Directory.CreateDirectory(lostAndFound);
        Directory.CreateDirectory(trash);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(trash, FileAttributes.Directory | FileAttributes.Hidden);
        }
        var store = OpenStore();
        store.Save(Local("abc", video));

        MoveDownloads(NewDownloads, store, OpenSetting());

        Assert.AreEqual(Path.Combine(NewDownloads, "abc__640x360_.mp4"), Record(OpenStore(), "abc").VideoSources[0].FilePath);
        Assert.IsTrue(Directory.Exists(lostAndFound));
        Assert.IsTrue(Directory.Exists(trash));
    }

    [TestMethod]
    public void ProgressIsReportedForEveryFile()
    {
        var store = OpenStore();
        store.Save(Local("abc", WriteFile(OldDownloads, "abc__640x360_.mp4"), WriteFile(OldDownloads, "abc__en-128000_.mp4a")));
        store.Save(Local("other", WriteFile(OldDownloads, "other__640x360_.mp4")));
        var reports = new List<(int Moved, int Total)>();

        MoveDownloads(NewDownloads, store, OpenSetting(), onProgress: (movedCount, totalCount) => reports.Add((movedCount, totalCount)));

        CollectionAssert.AreEqual(new[] { (1, 3), (2, 3), (3, 3) }, reports);
    }

    [TestMethod]
    public void RecordsFollowEachFileDuringTheMove()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        string audio = WriteFile(OldDownloads, "abc__en-128000_.mp4a");
        var store = OpenStore();
        store.Save(Local("abc", video, audio));
        var seenPaths = new List<(string Video, string Audio)>();

        MoveDownloads(NewDownloads, store, OpenSetting(), onProgress: (movedCount, totalCount) =>
        {
            var record = Record(store, "abc");
            seenPaths.Add((record.VideoSources[0].FilePath, record.AudioSources[0].FilePath));
        });

        Assert.AreEqual(2, seenPaths.Count);
        Assert.AreEqual(Path.Combine(NewDownloads, "abc__640x360_.mp4"), seenPaths[0].Video);
        Assert.AreEqual(audio, seenPaths[0].Audio);
        Assert.AreEqual(Path.Combine(NewDownloads, "abc__en-128000_.mp4a"), seenPaths[1].Audio);
    }

    [TestMethod]
    public void AFailedRollbackPointsRecordsAtTheFilesLeftBehind()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Uses Unix folder permissions to block moving the file back.");
        }
        string video = WriteFile(OldDownloads, "aaa__640x360_.mp4");
        string lockedFolder = Path.Combine(_directory, "locked");
        Directory.CreateDirectory(lockedFolder);
        string audio = WriteFile(lockedFolder, "aaa__en-128000_.mp4a");
        var store = OpenStore();
        store.Save(Local("aaa", video, audio));
        var setting = OpenSetting();
        var readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        var writable = readOnly | UnixFileMode.UserWrite;
        File.SetUnixFileMode(lockedFolder, readOnly);
        InvalidOperationException exception;
        try
        {
            if (CanWriteTo(lockedFolder))
            {
                Assert.Inconclusive("Folder permissions are not enforced for this user.");
            }
            //The video moves, then its old folder becomes read-only, so it cannot be moved back after the audio fails
            exception = Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, setting,
                onProgress: (movedCount, totalCount) => File.SetUnixFileMode(OldDownloads, readOnly)));
        }
        finally
        {
            File.SetUnixFileMode(lockedFolder, writable);
            File.SetUnixFileMode(OldDownloads, writable);
        }

        string movedVideo = Path.Combine(NewDownloads, "aaa__640x360_.mp4");
        StringAssert.Contains(exception.Message, "could not be moved back");
        Assert.IsTrue(File.Exists(movedVideo));
        Assert.IsTrue(File.Exists(audio));
        var reloaded = Record(OpenStore(), "aaa");
        Assert.AreEqual(movedVideo, reloaded.VideoSources[0].FilePath);
        Assert.AreEqual(audio, reloaded.AudioSources[0].FilePath);
        Assert.AreEqual(NewDownloads, OpenSetting().GetCopy());
    }

    [TestMethod]
    public void ADownloadDeletedDuringAMoveStaysDeleted()
    {
        var store = OpenStore();
        store.Save(Local("abc", WriteFile(OldDownloads, "abc__640x360_.mp4"), WriteFile(OldDownloads, "abc__en-128000_.mp4a")));
        store.Save(Local("other", WriteFile(OldDownloads, "other__640x360_.mp4")));
        Task<bool>? deletion = null;
        bool finishedDuringMove = false;

        MoveDownloads(NewDownloads, store, OpenSetting(), onProgress: (movedCount, totalCount) =>
        {
            if (movedCount == 1)
            {
                var record = Record(store, "abc");
                deletion = Task.Run(() => DeleteDownloaded(record, store));
                finishedDuringMove = deletion.Wait(300);
            }
        });

        Assert.IsNotNull(deletion);
        Assert.IsFalse(finishedDuringMove);
        Assert.IsTrue(deletion.Wait(5000));
        Assert.IsTrue(deletion.Result);
        var reloaded = OpenStore();
        Assert.IsFalse(reloaded.GetObjects().Any(local => local.VideoDetails.ID.Value == "abc"));
        Assert.AreEqual(Path.Combine(NewDownloads, "other__640x360_.mp4"), Record(reloaded, "other").VideoSources[0].FilePath);
        CollectionAssert.AreEquivalent(new[] { "other__640x360_.mp4" }, Directory.GetFiles(NewDownloads).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(0, Directory.GetFiles(OldDownloads).Length);
    }

    [TestMethod]
    public void AFailedSettingWriteMovesNothing()
    {
        string video = WriteFile(OldDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", video));
        var setting = OpenSetting();
        //A folder in place of the setting file makes every write fail
        Directory.CreateDirectory(Path.Combine(StoreDirectory, "downloads_directory"));

        var exception = Assert.ThrowsException<InvalidOperationException>(() => MoveDownloads(NewDownloads, store, setting));

        StringAssert.Contains(exception.Message, "download folder setting");
        Assert.IsTrue(File.Exists(video));
        Assert.AreEqual(0, Directory.GetFiles(NewDownloads).Length);
        Assert.IsNull(setting.GetCopy());
        Assert.AreEqual(video, Record(OpenStore(), "abc").VideoSources[0].FilePath);
    }

    [TestMethod]
    public void DeletePartialFilesRemovesLeftovers()
    {
        var download = new VideoDownload
        {
            VideoFilePath = WriteFile(NewDownloads, "zzz__640x360_.mp4"),
            AudioFilePath = WriteFile(NewDownloads, "zzz__en-128000_.mp4a"),
            SubtitleFilePath = WriteFile(NewDownloads, "zzz__English_.vtt")
        };
        string unrelated = WriteFile(NewDownloads, "holiday.jpg");

        DeletePartialFiles(download, OpenStore());

        Assert.IsFalse(File.Exists(download.VideoFilePath));
        Assert.IsFalse(File.Exists(download.AudioFilePath));
        Assert.IsFalse(File.Exists(download.SubtitleFilePath));
        Assert.IsTrue(File.Exists(unrelated));
    }

    [TestMethod]
    public void DeletePartialFilesKeepsFilesARecordUses()
    {
        string video = WriteFile(NewDownloads, "abc__640x360_.mp4");
        var store = OpenStore();
        store.Save(Local("abc", video));
        var download = new VideoDownload
        {
            VideoFilePath = video,
            AudioFilePath = WriteFile(NewDownloads, "abc__en-128000_.mp4a")
        };

        DeletePartialFiles(download, store);

        Assert.IsTrue(File.Exists(video));
        Assert.IsFalse(File.Exists(download.AudioFilePath));
    }
}
