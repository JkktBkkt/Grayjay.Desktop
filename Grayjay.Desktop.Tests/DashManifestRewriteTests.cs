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

    private static XElement[] Representations(XDocument document) => document.Descendants(Mpd + "Representation").ToArray();

    private static string ResolveForRepresentation(XDocument document, XElement representation, string value)
    {
        var levels = representation.AncestorsAndSelf().Reverse();
        var resolved = new Uri(BaseUrls(document.Root!)[0]);
        foreach (var level in levels.Skip(1))
        {
            var levelBase = BaseUrls(level).FirstOrDefault();
            if (levelBase != null)
                resolved = new Uri(resolved, levelBase);
        }
        return new Uri(resolved, value).AbsoluteUri;
    }

    [TestMethod]
    public void InheritedTemplate_ResolvesAgainstRepresentationBaseUrl()
    {
        var (document, _) = Rewrite("""
            <Period><AdaptationSet>
            <SegmentTemplate media="../../seg-$Number$.m4s" initialization="../../init.m4s" timescale="1000" duration="2000"/>
            <Representation id="v1" bandwidth="1"><BaseURL>a/b/</BaseURL></Representation>
            </AdaptationSet></Period>
            """);

        var representation = Representations(document).Single();
        var template = representation.Element(Mpd + "SegmentTemplate")!;
        Assert.AreEqual("../../seg-$Number$.m4s", template.Attribute("media")!.Value);
        Assert.AreEqual("../../init.m4s", template.Attribute("initialization")!.Value);
        Assert.IsNull(template.Attribute("timescale"));
        Assert.AreEqual("http://127.0.0.1:1/proxy/DashRelative/A/dir/seg-$Number$.m4s", ResolveForRepresentation(document, representation, template.Attribute("media")!.Value));
    }

    [TestMethod]
    public void InheritedTemplateEscapingRepresentationBaseUrl_IsMadeAbsoluteAgainstIt()
    {
        var (document, _) = Rewrite("""
            <Period><AdaptationSet>
            <SegmentTemplate media="../seg.m4s"/>
            <Representation id="v1" bandwidth="1"><BaseURL>https://other.example/</BaseURL></Representation>
            </AdaptationSet></Period>
            """);

        var template = Representations(document).Single().Element(Mpd + "SegmentTemplate")!;
        Assert.AreEqual("http://127.0.0.1:1/proxy/DashRelative/C/seg.m4s", template.Attribute("media")!.Value);
    }

    [TestMethod]
    public void PeriodTemplateWithAdaptationSetBaseUrl_IsPushedToEachRepresentation()
    {
        var (document, _) = Rewrite("""
            <Period>
            <SegmentTemplate media="seg-$RepresentationID$-$Number$.m4s"/>
            <AdaptationSet><BaseURL>video/</BaseURL>
            <Representation id="v1" bandwidth="1"/>
            <Representation id="v2" bandwidth="2"/>
            </AdaptationSet></Period>
            """);

        foreach (var representation in Representations(document))
        {
            Assert.AreEqual("seg-$RepresentationID$-$Number$.m4s", representation.Element(Mpd + "SegmentTemplate")!.Attribute("media")!.Value);
        }
    }

    [TestMethod]
    public void RepresentationTemplateAttributes_AreNotOverwritten()
    {
        var (document, _) = Rewrite("""
            <Period><AdaptationSet>
            <SegmentTemplate media="../../as-$Number$.m4s" initialization="../../init.m4s"/>
            <Representation id="v1" bandwidth="1"><BaseURL>x/</BaseURL><SegmentTemplate media="own-$Number$.m4s"/></Representation>
            </AdaptationSet></Period>
            """);

        var templates = Representations(document).Single().Elements(Mpd + "SegmentTemplate").ToArray();
        Assert.AreEqual(1, templates.Length);
        Assert.AreEqual("own-$Number$.m4s", templates[0].Attribute("media")!.Value);
        Assert.AreEqual("../../init.m4s", templates[0].Attribute("initialization")!.Value);
    }

    [TestMethod]
    public void NearerTemplateWithoutBaseUrlBetween_IsNotCopied()
    {
        var (document, _) = Rewrite("""
            <Period>
            <SegmentTemplate media="period-$Number$.m4s"/>
            <AdaptationSet><SegmentTemplate media="as-$Number$.m4s"/><Representation id="v1" bandwidth="1"/></AdaptationSet>
            </Period>
            """);

        Assert.IsNull(Representations(document).Single().Element(Mpd + "SegmentTemplate"));
        Assert.AreEqual("as-$Number$.m4s", Single(document, "AdaptationSet").Element(Mpd + "SegmentTemplate")!.Attribute("media")!.Value);
    }

    [TestMethod]
    public void InheritedSegmentList_IsClonedOntoRepresentationWithBaseUrl()
    {
        var (document, _) = Rewrite("""
            <Period><AdaptationSet>
            <SegmentList duration="2"><Initialization sourceURL="../init.mp4"/><SegmentURL media="../s1.m4s"/><SegmentURL media="../s2.m4s"/></SegmentList>
            <Representation id="v1" bandwidth="1"><BaseURL>https://other.example/v/</BaseURL></Representation>
            </AdaptationSet></Period>
            """);

        var representation = Representations(document).Single();
        var segmentList = representation.Element(Mpd + "SegmentList")!;
        Assert.IsNull(segmentList.Attribute("duration"));
        Assert.AreEqual("http://127.0.0.1:1/proxy/DashRelative/C/init.mp4", ResolveForRepresentation(document, representation, segmentList.Element(Mpd + "Initialization")!.Attribute("sourceURL")!.Value));
        CollectionAssert.AreEqual(
            new[] { "http://127.0.0.1:1/proxy/DashRelative/C/s1.m4s", "http://127.0.0.1:1/proxy/DashRelative/C/s2.m4s" },
            segmentList.Elements(Mpd + "SegmentURL").Select(segmentUrl => ResolveForRepresentation(document, representation, segmentUrl.Attribute("media")!.Value)).ToArray());
        Assert.AreEqual(2, Single(document, "AdaptationSet").Element(Mpd + "SegmentList")!.Elements(Mpd + "SegmentURL").Count());
    }

    [TestMethod]
    public void InheritedTemplateWithoutBaseUrlBetween_IsNotCopied()
    {
        var (document, _) = Rewrite("""
            <Period><AdaptationSet><SegmentTemplate media="seg-$Number$.m4s"/><Representation id="v1" bandwidth="1"/></AdaptationSet></Period>
            """);

        Assert.IsNull(Representations(document).Single().Element(Mpd + "SegmentTemplate"));
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
