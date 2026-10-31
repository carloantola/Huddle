using ChatServer;

public class ChatServerFactory : IChatServerFactory
{
    public IChatServer Create()
    {
        return new ChatServer.ChatServer();
    }
}

public interface IChatServerFactory
{
    IChatServer Create();
}