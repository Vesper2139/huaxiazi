using System;

namespace Huaxiazi.Services;

/// <summary>Raised when the local archive cannot be opened because its SQLite file is corrupt.</summary>
public sealed class DatabaseCorruptedException : InvalidOperationException
{
    public DatabaseCorruptedException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
