using StellarisDownloader.App.Models;

namespace StellarisDownloader.App.Services;

public static class SteamCommunitySecurityPolicy
{
    private const string TrustedHost = "steamcommunity.com";

    public static bool IsTrustedMessageSource(Uri? source) =>
        source is not null
        && source.IsAbsoluteUri
        && string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !HasUserInfo(source)
        && IsTrustedHost(source.IdnHost);

    public static BrowserNavigationDecision DecideNavigation(Uri? target)
    {
        if (target is null || !target.IsAbsoluteUri)
        {
            return new BrowserNavigationDecision(BrowserNavigationDisposition.Reject, null);
        }

        if (IsTrustedMessageSource(target))
        {
            return new BrowserNavigationDecision(
                BrowserNavigationDisposition.OpenInWebView,
                target);
        }

        if (string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserNavigationDecision(
                BrowserNavigationDisposition.OpenInSystemBrowser,
                target);
        }

        return new BrowserNavigationDecision(BrowserNavigationDisposition.Reject, null);
    }

    private static bool IsTrustedHost(string host) =>
        string.Equals(host, TrustedHost, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith($".{TrustedHost}", StringComparison.OrdinalIgnoreCase);

    private static bool HasUserInfo(Uri uri)
    {
        if (uri.UserInfo.Length != 0)
        {
            return true;
        }

        var source = uri.OriginalString;
        var authorityStart = source.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
        {
            return false;
        }

        authorityStart += 3;
        var authorityEnd = source.IndexOfAny(['/', '\\', '?', '#'], authorityStart);
        var authority = authorityEnd < 0
            ? source.AsSpan(authorityStart)
            : source.AsSpan(authorityStart, authorityEnd - authorityStart);
        return authority.Contains('@');
    }
}
