using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

// === ОПРЕДЕЛЯЕМ СВОЙ Message ===
public class Message
{
    public char Type { get; set; }
    public byte[] Data { get; set; }
}

// === ПАРСЕР (перенесён из MessageParser.cs для надёжности) ===
public static class MessageParser
{
    public static async Task<Message> ReadMessageAsync(NetworkStream stream)
    {
        var typeByte = await ReadExactlyAsync(stream, 1);
        char type = (char)typeByte[0];

        var lengthBytes = await ReadExactlyAsync(stream, 4);
        int length = BitConverter.ToInt32(lengthBytes, 0);

        if (length < 0 || length > 100_000_000)
            throw new InvalidDataException("Некорректная длина сообщения");

        byte[] data = await ReadExactlyAsync(stream, length);
        return new Message { Type = type, Data = data };
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
            if (read == 0)
                throw new EndOfStreamException("Клиент отключился");
            totalRead += read;
        }
        return buffer;
    }
}

// === СЕРВЕР ===
class ServerProgram
{
    private static readonly ConcurrentBag<TcpClient> Clients = new ConcurrentBag<TcpClient>();
    private const int Port = 1337;
    private static readonly string UsersFile = "users.txt";
    private static readonly object FileLock = new object();

    static ServerProgram()
    {
        if (!File.Exists(UsersFile))
            File.Create(UsersFile).Close();
    }

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
        var stream = client.GetStream();
        UserSession session = null;

        try
        {
            var authMessage = await MessageParser.ReadMessageAsync(stream);
            if (authMessage.Type != 'A')
            {
                Console.WriteLine("Первое сообщение не является аутентификацией");
                client.Close();
                return;
            }

            string authData = Encoding.UTF8.GetString(authMessage.Data); // ✅ Data, а не Text
            string[] parts = authData.Split(new string[] { " | " }, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                Console.WriteLine("Некорректный формат аутентификации");
                client.Close();
                return;
            }

            string password = parts[0].Trim();
            string requestedName = parts[1].Trim();

            if (password.Contains('|') || requestedName.Contains('|') ||
                password.Contains('\n') || requestedName.Contains('\n'))
            {
                Console.WriteLine("Запрещённые символы в пароле или имени");
                client.Close();
                return;
            }

            string userId;
            string finalName;

            lock (FileLock)
            {
                var users = File.ReadAllLines(UsersFile);
                var existing = users.FirstOrDefault(u => u.StartsWith(password + " | "));
                if (existing != null)
                {
                    var userParts = existing.Split(new string[] { " | " }, StringSplitOptions.None);
                    finalName = userParts[1];
                    userId = Math.Abs(password.GetHashCode()).ToString();
                    Console.WriteLine($"Вход: {finalName} ({userId})");
                }
                else
                {
                    finalName = requestedName;
                    userId = Math.Abs(password.GetHashCode()).ToString();
                    string newRecord = $"{password} | {finalName}";
                    File.AppendAllLines(UsersFile, new[] { newRecord });
                    Console.WriteLine($"Регистрация: {finalName} ({userId})");
                }
            }

            string response = $"{userId} | {finalName}";
            await SendMessageAsync(client, 'A', response);

            session = new UserSession { Client = client, UserId = userId, Name = finalName };
            Clients.Add(client);

            while (client.Connected)
            {
                var message = await MessageParser.ReadMessageAsync(stream);
                Console.WriteLine($"Сообщение от {session.Name}: {Encoding.UTF8.GetString(message.Data)}");

                if (message.Type == 'T' || message.Type == 'F')
                {
                    await BroadcastMessageAsync(message, session);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Клиент отключился: {ex.Message}");
        }
        finally
        {
            if (session != null)
            {
                Clients.TryTake(out _);
            }
            client.Close();
        }
    }

    private static async Task BroadcastMessageAsync(Message message, UserSession sender)
    {
        var clientsToRemove = new List<TcpClient>();

        foreach (var client in Clients)
        {
            if (!client.Connected)
            {
                clientsToRemove.Add(client);
                continue;
            }

            try
            {
                if (message.Type == 'T')
                {
                    string textWithSender = $"[{sender.Name}]: {Encoding.UTF8.GetString(message.Data)}"; // ✅
                    await SendMessageAsync(client, 'T', textWithSender);
                }
                else if (message.Type == 'F')
                {
                    await SendMessageAsync(client, 'F', Encoding.UTF8.GetString(message.Data)); // ✅
                }
            }
            catch
            {
                clientsToRemove.Add(client);
            }
        }

        foreach (var dead in clientsToRemove)
            Clients.TryTake(out _);
    }

    private static async Task SendMessageAsync(TcpClient client, char type, string data)
    {
        var stream = client.GetStream();
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        await stream.WriteAsync(new byte[] { (byte)type }, 0, 1);
        await stream.WriteAsync(BitConverter.GetBytes(dataBytes.Length), 0, 4);
        await stream.WriteAsync(dataBytes, 0, dataBytes.Length);
        await stream.FlushAsync();
    }
}

class UserSession
{
    public TcpClient Client;
    public string UserId;
    public string Name;
}