using LiteDB;

namespace CommonLib.Helper;

internal static class DatabaseExceptionHelper
{
    public static bool IsRetryableException(Exception ex)
    {
        return ex is IOException ||
               ex is UnauthorizedAccessException ||
               ex is LiteException liteEx && (
                   liteEx.Message.Contains("database is locked") ||
                   liteEx.Message.Contains("lock timeout") ||
                   liteEx.Message.Contains("Database timeout") ||
                   liteEx.Message.Contains("timeout")
               );
    }
}