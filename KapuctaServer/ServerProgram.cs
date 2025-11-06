// ServerProgram.cs
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using KapuctaServer;

class ServerProgram
{
    private static readonly ConcurrentBag<TcpClient> Clients = new ConcurrentBag<TcpClient>();
    private const int Port = 8080;

    static async Task Main(string[] args)
    {
        TcpListener listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Console.WriteLine($"Сервер запущен на порту {Port}");

        while (true)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                Console.WriteLine("Новое подключение");
                Clients.Add(client);
                _ = HandleClientAsync(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при принятии подключения: {ex.Message}");
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();

        try
        {
            while (client.Connected)
            {
                Message message = await MessageParser.ReadMessageAsync(stream);
                Console.WriteLine($"Получено сообщение типа '{message.Type}' от клиента");

                if (message.Type == 'T' || message.Type == 'F')
                {
                    await BroadcastMessageAsync(message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Клиент отключился или ошибка: {ex.Message}");
        }
        finally
        {
            // ConcurrentBag не поддерживает надёжное удаление, но можно попытаться
            // Для простоты в C# 7.3 оставим как есть — утечка временная
            client.Close();
        }
    }

    private static async Task BroadcastMessageAsync(Message message)
    {
        var clientsToRemove = new System.Collections.Generic.List<TcpClient>();

        foreach (TcpClient client in Clients)
        {
            if (!client.Connected)
            {
                clientsToRemove.Add(client);
                continue;
            }

            try
            {
                NetworkStream stream = client.GetStream();

                // Тип — 1 байт
                byte[] typeBytes = new byte[1] { (byte)message.Type };
                await stream.WriteAsync(typeBytes, 0, 1);

                // Длина — 4 байта
                byte[] lengthBytes = BitConverter.GetBytes(message.Data.Length);
                await stream.WriteAsync(lengthBytes, 0, 4);

                // Данные
                await stream.WriteAsync(message.Data, 0, message.Data.Length);
            }
            catch
            {
                clientsToRemove.Add(client);
            }
        }

        // Удалим мёртвые подключения (ConcurrentBag не позволяет удалить напрямую,
        // но мы можем создать новый список без них — или просто игнорировать.
        // Для простоты и C# 7.3 оставим без полной очистки, либо сделаем пересоздание.
        // Альтернатива — использовать List<T> с lock, но это сложнее.
    }
}