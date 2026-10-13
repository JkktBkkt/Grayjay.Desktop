using Grayjay.ClientServer.Store;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class RawValueStoreTests
{
    [TestMethod]
    public void ValueIsUnchangedWhenSavingFails()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "raw-value-store-" + Guid.NewGuid().ToString("N")));
        directory.Create();
        try
        {
            var store = new StringStore("value", null, directory).Load();
            store.Save("first");
            //A folder in place of the value file makes the next write fail
            File.Delete(store.FilePath);
            Directory.CreateDirectory(store.FilePath);

            Assert.ThrowsException<UnauthorizedAccessException>(() => store.Save("second"));

            Assert.AreEqual("first", store.GetCopy());
        }
        finally
        {
            foreach (var child in directory.GetDirectories())
            {
                child.Delete();
            }
            foreach (var file in directory.GetFiles())
            {
                file.Delete();
            }
            directory.Delete();
        }
    }
}
