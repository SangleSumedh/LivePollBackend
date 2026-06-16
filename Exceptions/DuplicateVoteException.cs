namespace live_poll_backend.Exceptions;

public class DuplicateVoteException : Exception
{
    public DuplicateVoteException()
        : base("You have already voted on this question") { }
}
