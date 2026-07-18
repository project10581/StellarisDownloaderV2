using StellarisDownloader.App.Models;
using StellarisDownloader.App.Services;

namespace StellarisDownloader.Tests;

public sealed class SteamCommunitySecurityPolicyTests
{
    [Theory]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=123")]
    [InlineData("https://www.steamcommunity.com/workshop/")]
    [InlineData("https://sub.domain.steamcommunity.com/")]
    public void TrustedSteamHttpsSourcesAreAccepted(string source)
    {
        Assert.True(SteamCommunitySecurityPolicy.IsTrustedMessageSource(new Uri(source)));
    }

    [Theory]
    [InlineData("http://steamcommunity.com/")]
    [InlineData("https://steamcommunity.com.evil.test/")]
    [InlineData("https://notsteamcommunity.com/")]
    [InlineData("https://steamcommunity.com@evil.test/")]
    [InlineData("https://user@steamcommunity.com/")]
    [InlineData("https://@steamcommunity.com/")]
    [InlineData("file:///C:/private/file.html")]
    [InlineData("about:blank")]
    public void UntrustedMessageSourcesAreRejected(string source)
    {
        Assert.False(SteamCommunitySecurityPolicy.IsTrustedMessageSource(new Uri(source)));
    }

    [Fact]
    public void TrustedSteamHttpsNavigationStaysInWebView()
    {
        var target = new Uri("https://steamcommunity.com/workshop/");

        var decision = SteamCommunitySecurityPolicy.DecideNavigation(target);

        Assert.Equal(BrowserNavigationDisposition.OpenInWebView, decision.Disposition);
        Assert.Same(target, decision.Target);
    }

    [Theory]
    [InlineData("http://steamcommunity.com/")]
    [InlineData("https://example.com/")]
    [InlineData("https://steamcommunity.com.evil.test/")]
    [InlineData("https://user@steamcommunity.com/")]
    [InlineData("https://@steamcommunity.com/")]
    public void OtherHttpNavigationUsesSystemBrowser(string targetUrl)
    {
        var target = new Uri(targetUrl);

        var decision = SteamCommunitySecurityPolicy.DecideNavigation(target);

        Assert.Equal(BrowserNavigationDisposition.OpenInSystemBrowser, decision.Disposition);
        Assert.Same(target, decision.Target);
    }

    [Theory]
    [InlineData("file:///C:/private/file.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("cmd:calc.exe")]
    [InlineData("about:blank")]
    public void NonHttpNavigationIsRejected(string targetUrl)
    {
        var decision = SteamCommunitySecurityPolicy.DecideNavigation(new Uri(targetUrl));

        Assert.Equal(BrowserNavigationDisposition.Reject, decision.Disposition);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void MissingNavigationTargetIsRejected()
    {
        var decision = SteamCommunitySecurityPolicy.DecideNavigation(null);

        Assert.Equal(BrowserNavigationDisposition.Reject, decision.Disposition);
        Assert.Null(decision.Target);
    }
}
