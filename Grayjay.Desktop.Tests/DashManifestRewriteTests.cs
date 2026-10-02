using System.Xml.Linq;
using Grayjay.ClientServer.Controllers;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class DashManifestRewriteTests
{
    private static readonly XNamespace Mpd = "urn:mpeg:dash:schema:mpd:2011";
    private static readonly Uri ManifestUri = new Uri("https://cdn.example/dir/manifest.mpd");

    private static readonly Dictionary<string, string> ProxyRoots = new()
    {
        ["https://cdn.example/"] = "http://127.0.0.1:1/proxy/DashRelative/A/",
        ["https://cdn-b.example/"] = "http://127.0.0.1:1/proxy/DashRelative/B/",
        ["https://other.example/"] = "http://127.0.0.1:1/proxy/DashRelative/C/"
    };

    private static (XDocument Document, string? Location) Rewrite(string body)
    {
        var document = XDocument.Parse($"""<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">{body}</MPD>""");
        var location = DetailsController.RewriteDashManifestForProxy(document, ManifestUri, root => ProxyRoots[root]);
        return (document, location);
    }

    private static string[] BaseUrls(XElement element) => element.Elements(Mpd + "BaseURL").Select(baseUrl => baseUrl.Value).ToArray();

    private static XElement Single(XDocument document, string name) => document.Descendants(Mpd + name).Single();

    [TestMethod]
    public void NestedRelativeBaseUrl_StaysRelative()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/content/</BaseURL>
            <Period><BaseURL>video/</BaseURL><AdaptationSet><SegmentTemplate media="seg-$Number%05d$.m4s" initialization="init.m4s"/></AdaptationSet></Period>
            """);

        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:1/proxy/DashRelative/A/content/" }, BaseUrls(document.Root!));
        CollectionAssert.AreEqual(new[] { "video/" }, BaseUrls(Single(document, "Period")));
        Assert.AreEqual("seg-$Number%05d$.m4s", Single(document, "SegmentTemplate").Attribute("media")!.Value);
        Assert.AreEqual("init.m4s", Single(document, "SegmentTemplate").Attribute("initialization")!.Value);
    }

    [TestMethod]
    public void TwoRootBaseUrls_RelativeChildStaysRelative()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/v/</BaseURL>
            <BaseURL>https://cdn-b.example/v/</BaseURL>
            <Period><BaseURL>1080/</BaseURL></Period>
            """);

        CollectionAssert.AreEqual(new[]
        {
            "http://127.0.0.1:1/proxy/DashRelative/A/v/",
            "http://127.0.0.1:1/proxy/DashRelative/B/v/"
        }, BaseUrls(document.Root!));
        CollectionAssert.AreEqual(new[] { "1080/" }, BaseUrls(Single(document, "Period")));
    }

    [TestMethod]
    public void RelativeChildEscapingOneRoot_IsResolvedAgainstFirstRoot()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/a/b/</BaseURL>
            <BaseURL>https://cdn-b.example/</BaseURL>
            <Period><BaseURL>../x/</BaseURL></Period>
            """);

        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:1/proxy/DashRelative/A/a/x/" }, BaseUrls(Single(document, "Period")));
    }

    [TestMethod]
    public void EscapingTemplateAttribute_IsMadeAbsoluteAndProxied()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/a/b/</BaseURL>
            <Period><AdaptationSet><SegmentTemplate media="../../../seg-$Number%05d$.m4s" initialization="../../../init-$RepresentationID$.m4s"/></AdaptationSet></Period>
            """);

        var template = Single(document, "SegmentTemplate");
        Assert.AreEqual("http://127.0.0.1:1/proxy/DashRelative/A/seg-$Number%05d$.m4s", template.Attribute("media")!.Value);
        Assert.AreEqual("http://127.0.0.1:1/proxy/DashRelative/A/init-$RepresentationID$.m4s", template.Attribute("initialization")!.Value);
    }

    [TestMethod]
    public void EscapingBaseUrl_IsMadeAbsoluteAndProxied()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/a/</BaseURL>
            <Period><BaseURL>../../../x/</BaseURL></Period>
            """);

        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:1/proxy/DashRelative/A/x/" }, BaseUrls(Single(document, "Period")));
    }

    [TestMethod]
    public void TemplateAttributeInsideRoot_StaysRelative()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/a/b/</BaseURL>
            <Period><AdaptationSet><SegmentTemplate media="../seg-$Number%05d$.m4s"/></AdaptationSet></Period>
            """);

        Assert.AreEqual("../seg-$Number%05d$.m4s", Single(document, "SegmentTemplate").Attribute("media")!.Value);
    }

    [TestMethod]
    public void RootRelativeAttribute_IsProxied()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/a/</BaseURL>
            <Period><AdaptationSet><Representation><SegmentList><SegmentURL media="/abs/seg-1.m4s"/></SegmentList></Representation></AdaptationSet></Period>
            """);

        Assert.AreEqual("http://127.0.0.1:1/proxy/DashRelative/A/abs/seg-1.m4s", Single(document, "SegmentURL").Attribute("media")!.Value);
    }

    [TestMethod]
    public void CrossOriginBaseUrl_UsesItsOwnProxyRoot()
    {
        var (document, _) = Rewrite("""
            <BaseURL>https://cdn.example/a/</BaseURL>
            <Period><AdaptationSet><BaseURL>https://other.example/b/</BaseURL><SegmentTemplate media="seg-$Number$.m4s"/></AdaptationSet></Period>
            """);

        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:1/proxy/DashRelative/C/b/" }, BaseUrls(Single(document, "AdaptationSet")));
        Assert.AreEqual("seg-$Number$.m4s", Single(document, "SegmentTemplate").Attribute("media")!.Value);
    }

    [TestMethod]
    public void RootRelativeBaseUrl_IsResolvedAgainstManifest()
    {
        var (document, _) = Rewrite("""
            <BaseURL>media/</BaseURL>
            <Period/>
            """);

        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:1/proxy/DashRelative/A/dir/media/" }, BaseUrls(document.Root!));
    }

    [TestMethod]
    public void Location_IsRemovedAndReturned()
    {
        var (document, location) = Rewrite("""
            <Location>../live.mpd</Location>
            <PatchLocation>https://cdn.example/patch</PatchLocation>
            <Period/>
            """);

        Assert.AreEqual("https://cdn.example/live.mpd", location);
        Assert.IsFalse(document.Root!.Elements(Mpd + "Location").Any());
        Assert.IsFalse(document.Root!.Elements(Mpd + "PatchLocation").Any());
        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:1/proxy/DashRelative/A/dir/manifest.mpd" }, BaseUrls(document.Root!));
    }
}
