namespace ChatServer;

public interface IChatServer
{
    Task StartAsync();

    Task StopAsync();
}