using System.ComponentModel;
using Microsoft.Data.Sqlite;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class OperationalFailureTests
{
    [Fact]
    public void OnlyExpectedOperationalErrorsAreRecoverable()
    {
        Exception[] recoverable = [new IOException(), new UnauthorizedAccessException(), new HttpRequestException(),
            new Win32Exception(), new StellarisOperationException()];
        var nullReference = Assert.Throws<NullReferenceException>(() => ((object)null!).ToString());
        Exception[] bugs = [new InvalidOperationException(), new ArgumentException(), new NotSupportedException(),
            nullReference, new InvalidDataException(), new SqliteException("error", 1)];
        Assert.All(recoverable, exception => Assert.True(OperationalFailure.IsRecoverable(exception)));
        Assert.All(bugs, exception => Assert.False(OperationalFailure.IsRecoverable(exception)));
    }

    [Fact]
    public void HttpTimeoutIsRecoverableButUserCancellationIsNot()
    {
        var timeout = new TaskCanceledException("HTTP timeout", new TimeoutException());
        Assert.True(OperationalFailure.IsRecoverable(timeout));
        Assert.False(OperationalFailure.IsRecoverable(timeout, new CancellationToken(true)));
        Assert.False(OperationalFailure.IsRecoverable(new TaskCanceledException()));
    }
}
