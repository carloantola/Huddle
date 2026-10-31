public class ChatClientFactory : IChatClientFactory
{
      public IChatClient Create()
    {
        return new ChatClient.ChatClient();
    }
}

public interface IChatClientFactory
{
    IChatClient Create();
}