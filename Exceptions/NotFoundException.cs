namespace live_poll_backend.Exceptions;

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}
