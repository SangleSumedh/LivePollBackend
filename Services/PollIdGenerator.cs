namespace live_poll_backend.Services;

public static class PollIdGenerator
{
    public static string Generate()
    {
        // 6-character uppercase alphanumeric code (matches Firebase format)
        var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
